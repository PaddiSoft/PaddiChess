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
        "started" => "开始记录", "confirmed" => "确认落子", "decision" => "选择着法",
        "sent" => "发送落子", "input-retry" => "补发终点", "verification" => "落子前核验", "correction" => "自动校正", "corrected" => "完成校正",
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
    private sealed record Write(ExternalHistoryEvent? Event, byte[]? Snapshot, TaskCompletionSource? Barrier);
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
        return _writes.Writer.WriteAsync(new Write(frozen, bytes, null), ct);
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
            await using var stream = new FileStream(EventsPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 16_384, FileOptions.Asynchronous);
            await using var log = new StreamWriter(stream, new UTF8Encoding(false));
            long sequence = 0;
            await foreach (var write in _writes.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                current = write;
                if (write.Event is { } entry)
                {
                    await log.WriteLineAsync(JsonSerializer.Serialize(entry with { Sequence = ++sequence })).ConfigureAwait(false);
                    await log.FlushAsync().ConfigureAwait(false);
                }
                if (write.Snapshot is { } snapshot)
                {
                    await File.WriteAllBytesAsync(temporary, snapshot).ConfigureAwait(false);
                    File.Move(temporary, RecordPath, overwrite: true);
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

    public static Task<IReadOnlyList<ExternalHistoryItem>> ListAsync(string root, string? recovery = null) => Task.Run(async () =>
    {
        var paths = Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Select(folder => Path.Combine(folder, "game.paddi.json"))
            : Enumerable.Empty<string>();
        if (recovery is not null && Directory.Exists(recovery))
            paths = paths.Concat(Directory.EnumerateFiles(recovery, "*.paddi.json"));
        var result = new List<ExternalHistoryItem>();
        foreach (var path in paths.Where(File.Exists).OrderByDescending(File.GetLastWriteTime).Take(500))
        {
            try
            {
                await using var stream = OpenRecordRead(path);
                // Listing does not need to materialize potentially long model responses.
                var record = await JsonSerializer.DeserializeAsync<RecordSummary>(stream).ConfigureAwait(false);
                if (record is null || record.Format != "PaddiXiangqi/1") continue;
                var events = Path.Combine(Path.GetDirectoryName(path)!, "history.jsonl");
                var isRecovery = !File.Exists(events);
                result.Add(new ExternalHistoryItem(path, isRecovery ? null : events, record.Title,
                    File.GetLastWriteTime(path), record.Moves?.Count ?? 0, isRecovery));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return (IReadOnlyList<ExternalHistoryItem>)result;
    });

    // Windows requires readers to permit deletion/rename while the writer commits
    // a replacement. The open handle keeps reading its complete original snapshot.
    public static FileStream OpenRecordRead(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read | FileShare.Delete, 16_384, FileOptions.Asynchronous);

    public static async Task<IReadOnlyList<ExternalHistoryEvent>> ReadEventsAsync(string? path)
    {
        if (path is null) return [];
        var events = new List<ExternalHistoryEvent>();
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 16_384, FileOptions.Asynchronous));
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            try
            {
                if (JsonSerializer.Deserialize<ExternalHistoryEvent>(line) is { } entry)
                    events.Add(entry with { Candidates = entry.Candidates ?? [] });
            }
            catch (JsonException) { } // A crash can leave the final JSONL line incomplete.
        }
        return events;
    }
}
