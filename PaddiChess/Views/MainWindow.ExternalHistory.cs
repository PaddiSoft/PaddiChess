using Avalonia.Interactivity;
using PaddiXiangqi.Core;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private ExternalHistoryStore? _externalHistory;
    private Task _externalHistoryDrain = Task.CompletedTask;
    private bool _externalHistoryOpening;
    private static string ExternalHistoryDirectory => Path.Combine(AppDataPaths.Current.DataDirectory, "ExternalHistory");

    private GameRecord CaptureExternalHistoryRecord() => new()
    {
        Title = RecordTitleBox.Text ?? "外部接管",
        StartFen = _game.StartFen, Moves = _game.History.Select(move => move.Uci).ToList(),
        Notes = new(_notes), Scores = new(_scores), ScoreLabels = new(_scoreLabels),
        LlmThoughts = ExportLlmThoughts(), CurrentPly = _game.Ply,
        AgreedDrawPly = _game.AgreedDrawPly, ExternalAdjudication = _game.ExternalAdjudication
    };

    private async Task BeginExternalHistoryAsync(string title)
    {
        if (_externalHistory is not null) return;
        _externalHistory = new ExternalHistoryStore(ExternalHistoryDirectory);
        await RecordExternalHistoryAsync("started", title);
    }

    private async Task RecordExternalHistoryAsync(string kind, string? message = null, string? move = null,
        IEnumerable<string>? candidates = null, string? observedFen = null, string? beforeFen = null)
    {
        if (_externalHistory is not { } history) return;
        try
        {
            var entry = new ExternalHistoryEvent
            {
                Kind = kind, Message = message ?? "", Move = move, Candidates = candidates?.ToArray() ?? [],
                Fen = _game.CurrentFen(), BeforeFen = beforeFen, ObservedFen = observedFen, Ply = _game.Ply
            };
            // Recognition candidates and input status may fluctuate while the same
            // board is on screen. Keep their evidence without repeatedly copying and
            // serializing the full game (including all model explanations) on the UI thread.
            var snapshot = kind is "started" or "confirmed" or "correction" or "corrected" or "paused" or "error"
                ? CaptureExternalHistoryRecord() : null;
            await history.AppendAsync(entry, snapshot);
        }
        catch (Exception error) { BoardFooter.Text = "接管记录保存失败：" + error.Message; }
    }

    private async Task FlushExternalHistoryAsync()
    {
        try
        {
            await _externalHistoryDrain;
            if (_externalHistory is { } history) await history.FlushAsync();
        }
        catch (Exception error) { BoardFooter.Text = "接管记录保存失败：" + error.Message; }
    }

    private Task EndExternalHistoryAsync(string reason)
    {
        if (_externalHistory is not { } history) return _externalHistoryDrain;
        // Detach synchronously before the first await. A second disconnect/close must
        // join this drain, and an old end operation must never detach a newer session.
        _externalHistory = null;
        var entry = new ExternalHistoryEvent { Kind = "ended", Message = reason, Fen = _game.CurrentFen(), Ply = _game.Ply };
        var drain = DrainExternalHistoryAsync(history, entry, CaptureExternalHistoryRecord());
        _externalHistoryDrain = _externalHistoryDrain.IsCompleted ? drain : Task.WhenAll(_externalHistoryDrain, drain);
        return _externalHistoryDrain;
    }

    private async Task DrainExternalHistoryAsync(ExternalHistoryStore history, ExternalHistoryEvent entry, GameRecord snapshot)
    {
        try { await history.AppendAsync(entry, snapshot); }
        catch (Exception error) { BoardFooter.Text = "接管记录保存失败：" + error.Message; }
        finally
        {
            try { await history.DisposeAsync(); }
            catch (Exception error) { BoardFooter.Text = "接管记录保存失败：" + error.Message; }
        }
    }

    private async void ExternalHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (_externalHistoryOpening || _closing) return;
        _externalHistoryOpening = true;
        try
        {
            await FlushExternalHistoryAsync();
            if (_closing) return;
            bool CanReplay() => !_externalLinked && !_externalObserving && !_externalRunning && !_externalCalibrating && !_closing;
            var dialog = new ExternalHistoryWindow(ExternalHistoryDirectory,
                Path.Combine(AppDataPaths.Current.DataDirectory, "Recovery"), CanReplay());
            var record = await dialog.ShowDialog<GameRecord?>(this);
            if (record is null || !CanReplay()) return;
            CancelSearch();
            _game.ExternalAdjudication = record.ExternalAdjudication;
            _game.LoadFen(record.StartFen);
            foreach (var uci in record.Moves) _game.TryMoveUci(uci, out _);
            if (record.AgreedDrawPly is not null) _game.DeclareDraw();
            _game.GoToPly(record.CurrentPly);
            ResetLlmInsights(); ImportLlmThoughts(record.LlmThoughts);
            _pendingLlmDrawOffer = null; _lastLlmDrawOfferPly = -8;
            _notes.Clear(); foreach (var item in record.Notes) _notes[item.Key] = item.Value;
            _scores.Clear(); foreach (var item in record.Scores) _scores[item.Key] = item.Value;
            _scoreLabels.Clear(); foreach (var item in record.ScoreLabels) _scoreLabels[item.Key] = item.Value;
            if (!_scores.ContainsKey(0)) _scores[0] = 0;
            _moveListDirty = true; RecordTitleBox.Text = record.Title;
            _enginePaused = true; _engine.RequestNewGame(); ResetAnalysisDisplay();
            ShowLlmInsightsForPly(_game.Ply); ClearSelection(); UpdateAnnotationEditor(); RefreshUi();
            BoardFooter.Text = "已加载接管记录，可用上一步 / 下一步复盘：" + record.Title;
        }
        finally { _externalHistoryOpening = false; }
    }
}
