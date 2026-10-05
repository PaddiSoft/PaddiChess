using System.Text.Json;
using PaddiXiangqi.Core;

namespace PaddiXiangqi.Services;

public static class GameRecordStorage
{
    /// <summary>
    /// Read and validate off the UI thread. No live game is mutated, and no record is
    /// returned until its complete continuation and adjudication metadata are valid.
    /// Optional annotations outside the continuation are discarded, as older imports did.
    /// </summary>
    public static Task<GameRecord> ReadValidatedAsync(Stream stream, CancellationToken ct) => Task.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(stream);
        ct.ThrowIfCancellationRequested();
        GameRecord record;
        try
        {
            record = await JsonSerializer.DeserializeAsync<GameRecord>(stream, cancellationToken: ct).ConfigureAwait(false)
                ?? throw new FormatException("棋谱文件为空。");
        }
        catch (JsonException error) { throw new FormatException("棋谱 JSON 内容无效。", error); }
        if (record.Format != "PaddiXiangqi/1") throw new FormatException("棋谱格式版本不受支持。");
        if (string.IsNullOrWhiteSpace(record.StartFen)) throw new FormatException("棋谱缺少开始局面。");
        if (record.Moves is null) throw new FormatException("棋谱缺少着法列表。");
        if (record.CurrentPly < 0 || record.CurrentPly > record.Moves.Count)
            throw new FormatException("棋谱中的当前手数超出着法范围。");

        var validated = new XiangqiGame { ExternalAdjudication = record.ExternalAdjudication };
        validated.LoadFen(record.StartFen);
        // Keep a canonical engine-compatible FEN rather than forwarding unparsed
        // trailing fields. This does not alter the board or side to move.
        record.StartFen = validated.CurrentFen();
        for (int index = 0; index < record.Moves.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var uci = record.Moves[index];
            if (uci is null || !validated.TryMoveUci(uci, out _))
                throw new FormatException($"棋谱第 {index + 1} 手包含非法着法：{uci ?? "空值"}");
        }
        if (record.AgreedDrawPly is { } drawPly &&
            (drawPly < 1 || drawPly != validated.TotalPly || !validated.DeclareDraw()))
            throw new FormatException("棋谱中的和棋记录无效。");
        if (record.PendingDrawOfferPly is { } offerPly)
        {
            if (offerPly < 1 || record.PendingDrawOfferingRed is null || record.AgreedDrawPly is not null ||
                offerPly != validated.TotalPly || offerPly != record.CurrentPly ||
                validated.Result != GameResult.Ongoing || validated.RedToMove == record.PendingDrawOfferingRed)
                throw new FormatException("棋谱中的待回应提和记录无效。");
        }
        else if (record.PendingDrawOfferingRed is not null || !string.IsNullOrWhiteSpace(record.PendingDrawExplanation))
            throw new FormatException("棋谱中的提和说明或发起方缺少对应手数。");

        ct.ThrowIfCancellationRequested();
        var total = validated.TotalPly;
        record.Title = string.IsNullOrWhiteSpace(record.Title) ? "未命名棋谱" : record.Title;
        record.Notes = (record.Notes ?? []).Where(item => item.Key >= 0 && item.Key <= total && item.Value is not null)
            .ToDictionary(item => item.Key, item => item.Value);
        record.Scores = (record.Scores ?? []).Where(item => item.Key >= 0 && item.Key <= total && double.IsFinite(item.Value))
            .ToDictionary(item => item.Key, item => item.Value);
        record.ScoreLabels = (record.ScoreLabels ?? []).Where(item => item.Key >= 0 && item.Key <= total && item.Value is not null)
            .ToDictionary(item => item.Key, item => item.Value);
        var thoughts = new Dictionary<int, List<LlmThoughtRecord>>();
        foreach (var (ply, entries) in record.LlmThoughts ?? [])
        {
            ct.ThrowIfCancellationRequested();
            if (ply < 1 || ply > total || entries is null) continue;
            var saved = new List<LlmThoughtRecord>();
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                // Do not relabel a model's explanation onto another move. A malformed
                // optional entry may be omitted, never reconstructed or invented.
                if (entry is null || entry.Ply != ply) continue;
                entry.Side ??= "";
                entry.Model ??= "";
                entry.MoveNotation ??= "";
                entry.UciMove ??= "";
                entry.Action ??= "";
                entry.RequestedReasoningEffort ??= "";
                saved.Add(entry);
            }
            if (saved.Count > 0) thoughts[ply] = saved;
        }
        record.LlmThoughts = thoughts;
        ct.ThrowIfCancellationRequested();
        return record;
    }, ct);

    /// <summary>The caller keeps the current game until its recovery copy is complete.</summary>
    public static Task ArchiveRecoveryAsync(GameRecord snapshot, string folder, CancellationToken ct) => Task.Run(async () =>
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"接管重新同步-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.paddi.json");
        var temp = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16_384, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, snapshot, cancellationToken: ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }, ct);
}
