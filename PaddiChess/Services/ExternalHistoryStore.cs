using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using PaddiXiangqi.Core;

namespace PaddiXiangqi.Services;

/// <summary>Local, explicit game events only. No settings, endpoints, or credentials are accepted.</summary>
public sealed record ExternalHistoryEvent
{
    public long Sequence { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public string Kind { get; init; } = "";
    public string Message { get; init; } = "";
    public int Ply { get; init; }
    public string Fen { get; init; } = "";
    public string? BeforeFen { get; init; }
    public string? ObservedFen { get; init; }
    public string? Move { get; init; }
    public string[] Candidates { get; init; } = [];

    [JsonIgnore]
    public string Label => Kind switch
    {
        "performance" => "性能统计", "started" => "开始记录", "confirmed" => "确认落子", "decision" => "选择着法",
        "sent" => "发送落子", "input-completed" => "输入完成", "input-retry" => "补发终点", "verification" => "落子前核验", "correction" => "自动校正", "corrected" => "完成校正",
        "paused" => "暂停", "error" => "异常", "ended" => "结束记录",
        "observation" => "识别候选", "blocked" => "等待核验", "resumed" => "继续接管",
        _ => Kind
    };
    public override string ToString() => $"{At:HH:mm:ss.fff} · {Label} · 第 {Ply} 手" +
        (string.IsNullOrEmpty(Move) ? "" : $" · {Move}");
}

public sealed record ExternalHistoryItem(string RecordPath, string? EventsPath, string Title,
    DateTime UpdatedAt, int Plies, bool Recovery)
{
    public override string ToString() => $"{UpdatedAt:MM-dd HH:mm:ss} · {Title} · {Plies} 手" +
        (Recovery ? "（旧恢复记录）" : "");
}

/// <summary>
/// Each connection owns a unique folder. A bounded single writer preserves event order,
/// applies backpressure instead of dropping evidence, and atomically replaces its replay file.
/// Producers enqueue only changes, never capture frames. Flush/Dispose report disk failures.
/// </summary>
public sealed class ExternalHistoryStore : IAsyncDisposable
{
    private sealed record Write(ExternalHistoryEvent? Event, byte[]? Snapshot, TaskCompletionSource? Barrier, string? Title = null, int Plies = 0);
    private sealed record HistorySummary(string Title, int Plies, long Bytes);
    private sealed class RecordSummary
    {
        public string? Format { get; set; }
        public string Title { get; set; } = "未命名棋谱";
        public List<string>? Moves { get; set; }
    }
    private readonly Channel<Write> _writes = Channel.CreateBounded<Write>(new BoundedChannelOptions(128)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _writer;
    public string SessionDirectory { get; }
    public string RecordPath => Path.Combine(SessionDirectory, "game.paddi.json");
    public string EventsPath => Path.Combine(SessionDirectory, "history.jsonl");

    public ExternalHistoryStore(string root)
    {
        SessionDirectory = Path.Combine(root, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");
        _writer = Task.Run(WriteLoopAsync);
    }

    public ValueTask AppendAsync(ExternalHistoryEvent entry, GameRecord? snapshot = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // Freeze values before returning to the game loop; future annotations and corrected
        // branches must not mutate a snapshot that is waiting for disk I/O.
        var frozen = entry with { Candidates = entry.Candidates.ToArray() };
        var bytes = snapshot is null ? null : JsonSerializer.SerializeToUtf8Bytes(snapshot);
        return _writes.Writer.WriteAsync(new Write(frozen, bytes, null, snapshot?.Title, snapshot?.Moves.Count ?? 0), ct);
    }

    public async Task FlushAsync()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await _writes.Writer.WriteAsync(new Write(null, null, barrier)).ConfigureAwait(false); }
        catch (ChannelClosedException) { await _writer.ConfigureAwait(false); throw; }
        await barrier.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _writes.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    private async Task WriteLoopAsync()
    {
        Write? current = null;
        var temporary = RecordPath + ".tmp";
        try
        {
            Directory.CreateDirectory(SessionDirectory);
            using var lease = new FileStream(Path.Combine(SessionDirectory, ".active"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            await using var index = new FileStream(EventsPath + ".idx", FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, true);
            await using var stream = new FileStream(EventsPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 16_384, FileOptions.Asynchronous);
            await using var log = new StreamWriter(stream, new UTF8Encoding(false));
            long sequence = 0;
            await foreach (var write in _writes.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                current = write;
                if (write.Event is { } entry)
                {
                    if (sequence % ExternalHistoryPaging.PageSize == 0)
                    {
                        var offset = new byte[8];
                        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(offset, stream.Position);
                        await index.WriteAsync(offset).ConfigureAwait(false);
                        await index.FlushAsync().ConfigureAwait(false);
                    }
                    await log.WriteLineAsync(JsonSerializer.Serialize(entry with { Sequence = ++sequence })).ConfigureAwait(false);
                    await log.FlushAsync().ConfigureAwait(false);
                }
                if (write.Snapshot is { } snapshot)
                {
                    await File.WriteAllBytesAsync(temporary, snapshot).ConfigureAwait(false);
                    // MoveFileEx cannot overwrite an open destination on Windows even
                    // when its reader allows deletion. ReplaceFile preserves the old
                    // reader's snapshot while publishing the new file at this path.
                    if (File.Exists(RecordPath)) File.Replace(temporary, RecordPath, null);
                    else File.Move(temporary, RecordPath);
                    var summaryPath = Path.Combine(SessionDirectory, "summary.json");
                    await File.WriteAllTextAsync(summaryPath + ".tmp",
                        JsonSerializer.Serialize(new HistorySummary(write.Title ?? "未命名棋谱", write.Plies, snapshot.Length))).ConfigureAwait(false);
                    if (File.Exists(summaryPath)) File.Replace(summaryPath + ".tmp", summaryPath, null);
                    else File.Move(summaryPath + ".tmp", summaryPath);
                }
                write.Barrier?.TrySetResult();
                current = null;
            }
        }
        catch (Exception error)
        {
            current?.Barrier?.TrySetException(error);
            _writes.Writer.TryComplete(error);
            while (_writes.Reader.TryRead(out var pending)) pending.Barrier?.TrySetException(error);
            throw;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public static Task<IReadOnlyList<ExternalHistoryItem>> ListAsync(string root, string? recovery = null,
        CancellationToken ct = default, int page = 0) => Task.Run(async () =>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        var paths = Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Select(folder => Path.Combine(folder, "game.paddi.json"))
            : Enumerable.Empty<string>();
        if (recovery is not null && Directory.Exists(recovery))
            paths = paths.Concat(Directory.EnumerateFiles(recovery, "*.paddi.json"));
        var result = new List<ExternalHistoryItem>();
        var candidates = paths.Select(path => { ct.ThrowIfCancellationRequested(); return new FileInfo(path); })
            .Where(file => file.Exists).OrderByDescending(file => file.LastWriteTime).Skip(checked(page * 500)).Take(500);
        foreach (var file in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var path = file.FullName;
            try
            {
                var summaryPath = Path.Combine(Path.GetDirectoryName(path)!, "summary.json");
                if (File.Exists(summaryPath))
                {
                    try
                    {
                        await using var summaryStream = OpenRecordRead(summaryPath);
                        var summary = await JsonSerializer.DeserializeAsync<HistorySummary>(summaryStream, cancellationToken: ct);
                        if (summary != null && summary.Bytes == file.Length && File.GetLastWriteTimeUtc(summaryPath) >= file.LastWriteTimeUtc)
                        {
                            var logPath = Path.Combine(Path.GetDirectoryName(path)!, "history.jsonl");
                            result.Add(new(path, logPath, summary.Title, file.LastWriteTime, summary.Plies, false));
                            continue;
                        }
                    }
                    catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { }
                }
                await using var stream = OpenRecordRead(path);
                // Listing does not need to materialize potentially long model responses.
                var record = await JsonSerializer.DeserializeAsync<RecordSummary>(stream, cancellationToken: ct).ConfigureAwait(false);
                if (record is null || record.Format != "PaddiXiangqi/1") continue;
                var events = Path.Combine(Path.GetDirectoryName(path)!, "history.jsonl");
                var isRecovery = !File.Exists(events);
                result.Add(new ExternalHistoryItem(path, isRecovery ? null : events, record.Title,
                    File.GetLastWriteTime(path), record.Moves?.Count ?? 0, isRecovery));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return (IReadOnlyList<ExternalHistoryItem>)result;
    }, ct);

    // Windows requires readers to permit deletion/rename while the writer commits
    // a replacement. The open handle keeps reading its complete original snapshot.
    public static FileStream OpenRecordRead(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read | FileShare.Delete, 16_384, FileOptions.Asynchronous);

    public static Task<IReadOnlyList<ExternalHistoryEvent>> ReadEventsAsync(string? path,
        CancellationToken ct = default) => Task.Run(async () =>
    {
        ct.ThrowIfCancellationRequested();
        if (path is null) return (IReadOnlyList<ExternalHistoryEvent>)Array.Empty<ExternalHistoryEvent>();
        var events = new List<ExternalHistoryEvent>();
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 16_384, FileOptions.Asynchronous));
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (JsonSerializer.Deserialize<ExternalHistoryEvent>(line) is { } entry)
                    events.Add(entry with { Candidates = entry.Candidates ?? [] });
            }
            catch (JsonException) { } // A crash can leave the final JSONL line incomplete.
        }
        return (IReadOnlyList<ExternalHistoryEvent>)events;
    }, ct);
}
