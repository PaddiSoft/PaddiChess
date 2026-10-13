using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace PaddiXiangqi.Services;

public sealed record ExternalEventPage(IReadOnlyList<ExternalHistoryEvent> Events, int Number, bool HasNext);
public sealed record HistoryCleanupPlan(IReadOnlyList<string> Directories, long Bytes, DateTime Cutoff);

/// <summary>128-event pages backed by byte offsets; old JSONL files remain readable.</summary>
public static class ExternalHistoryPaging
{
    public const int PageSize = 128;
    private const int MaximumLineBytes = 4 * 1024 * 1024;

    public static Task<ExternalEventPage> ReadAsync(string? path, int page, CancellationToken ct = default) => Task.Run(async () =>
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        if (path is null) return new ExternalEventPage([], 0, false);
        await EnsureIndexAsync(path, ct);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16384, true);
        long offset = 0; var skipped = 0;
        // The index is optional. A stale/incomplete index can only cost a sequential scan.
        try
        {
            await using var index = new FileStream(path + ".idx", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
            if (index.Length >= ((long)page + 1) * 8)
            {
                index.Position = (long)page * 8;
                var bytes = new byte[8]; await index.ReadExactlyAsync(bytes, ct);
                var value = BinaryPrimitives.ReadInt64LittleEndian(bytes);
                if (value >= 0 && value < file.Length) { offset = value; skipped = page * PageSize; }
            }
        }
        catch (IOException) { }
        file.Position = offset;
        var result = new List<ExternalHistoryEvent>(PageSize);
        var count = skipped; var target = (long)page * PageSize;
        var more = false;
        await foreach (var line in CompleteLines(file, ct))
        {
            // Offsets count physical complete lines, including malformed entries.
            if (count++ < target) continue;
            if (count > target + PageSize) { more = true; break; }
            try
            {
                if (JsonSerializer.Deserialize<ExternalHistoryEvent>(line) is { } entry)
                    result.Add(entry with { Candidates = entry.Candidates ?? [] });
            }
            catch (JsonException) { }
        }
        return new ExternalEventPage(result, page, more);
    }, ct);

    public static Task<ExternalEventPage> ReadLastAsync(string? path, CancellationToken ct = default) => Task.Run(async () =>
    {
        ct.ThrowIfCancellationRequested();
        if (path is null) return new([], 0, false);
        await EnsureIndexAsync(path, ct);
        long page = 0;
        try
        {
            var info = new FileInfo(path + ".idx");
            if (info.Exists) page = Math.Max(0, info.Length / 8 - 1);
            else
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16384, true);
                long lines = 0;
                await foreach (var unused in CompleteLines(file, ct)) lines++;
                page = Math.Max(0, (lines - 1) / PageSize);
            }
        }
        catch (FileNotFoundException) { return new([], 0, false); }
        var result = await ReadAsync(path, checked((int)page), ct);
        if (page > 0 && result.Events.Count == 0) result = await ReadAsync(path, (int)page - 1, ct);
        return result;
    }, ct);

    private static async Task EnsureIndexAsync(string path, CancellationToken ct)
    {
        if (File.Exists(path + ".idx")) return;
        var temporary = path + ".idx." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16384, true))
            await using (var index = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                var offset = new byte[8]; await index.WriteAsync(offset, ct);
                var buffer = new byte[16384]; long position = 0, lines = 0; int count;
                while ((count = await source.ReadAsync(buffer, ct)) > 0)
                    for (int i = 0; i < count; i++)
                    {
                        position++;
                        if (buffer[i] != 10 || ++lines % PageSize != 0) continue;
                        BinaryPrimitives.WriteInt64LittleEndian(offset, position);
                        await index.WriteAsync(offset, ct);
                    }
            }
            try { File.Move(temporary, path + ".idx"); }
            catch (IOException) when (File.Exists(path + ".idx")) { }
        }
        catch (UnauthorizedAccessException) { } // Read-only imported histories still support sequential pages.
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async IAsyncEnumerable<byte[]> CompleteLines(Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        using var line = new MemoryStream();
        var oversized = false;
        try
        {
            int length;
            while ((length = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
                for (var i = 0; i < length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (buffer[i] == 10)
                    {
                        yield return oversized ? [] : line.ToArray();
                        line.SetLength(0); oversized = false;
                    }
                    else if (!oversized)
                    {
                        if (line.Length >= MaximumLineBytes) { line.SetLength(0); oversized = true; }
                        else line.WriteByte(buffer[i]);
                    }
                }
            // A growing/crashed writer may leave an unfinished last line: never parse it.
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static Task<HistoryCleanupPlan> PlanCleanupAsync(string root, int days, CancellationToken ct = default) => Task.Run(() =>
    {
        if (days is not (7 or 30 or 90)) throw new ArgumentOutOfRangeException(nameof(days));
        var cutoff = DateTime.Now.AddDays(-days);
        var folders = new List<string>(); long bytes = 0;
        if (Directory.Exists(root)) foreach (var folder in Directory.EnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
            var record = new FileInfo(Path.Combine(folder, "game.paddi.json"));
            if (!record.Exists || record.LastWriteTime >= cutoff) continue;
            folders.Add(folder);
            bytes += Directory.EnumerateFiles(folder).Select(p => new FileInfo(p).Length).Sum();
        }
        return new HistoryCleanupPlan(folders, bytes, cutoff);
    }, ct);

    // Explicit user action only. Recheck age and acquire the same lease used by writers.
    public static Task<int> DeleteAsync(HistoryCleanupPlan plan, CancellationToken ct = default) => Task.Run(() =>
    {
        var removed = 0;
        foreach (var folder in plan.Directories)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0
                    || File.GetLastWriteTime(Path.Combine(folder, "game.paddi.json")) >= plan.Cutoff) continue;
                using (new FileStream(Path.Combine(folder, ".active"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    // Delete only files this store owns. Leave unrelated content alone.
                    foreach (var name in new[] { "game.paddi.json", "game.paddi.json.tmp", "summary.json", "summary.json.tmp", "history.jsonl", "history.jsonl.idx" })
                        File.Delete(Path.Combine(folder, name));
                }
                File.Delete(Path.Combine(folder, ".active"));
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                removed++;
            }
            catch (IOException) { } // Active writer / concurrent deletion.
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }, ct);
}
