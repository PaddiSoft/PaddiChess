using PaddiXiangqi.Engine;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    // A connected observer may compute a move, but only Start authorizes input.
    private bool _externalObserving;
    private TaskCompletionSource<bool>? _externalObservationReady;
    private ExternalDecision? _externalPreparedDecision;
    private CancellationTokenSource? _externalDecisionCancellation;
    private ExternalControllerConfiguration? _externalActiveConfiguration;
    private string? _externalConfigurationError;

    private sealed record ExternalControllerConfiguration(bool Red, LlmConnectionSettings? Model,
        EngineSettings? EngineSettings, string? EnginePath, string? EvalPath);

    private void RefreshExternalControllerConfiguration()
    {
        if (!_externalObserving || !_ready) return;
        ExternalControllerConfiguration? next = null;
        string? error = null;
        try
        {
            var red = ExternalSideBox.SelectedIndex == 0;
            var model = ExternalControllerBox.SelectedIndex == 1 ? ReadLlmSettings(red) : null;
            next = new(red, model, model == null ? ReadPlayingEngineSettings() : null,
                model == null ? _engine.OverridePath : null, model == null ? _engine.OverrideEvalPath : null);
        }
        catch (Exception ex) { error = ex.Message; }
        if (next == _externalActiveConfiguration && error == _externalConfigurationError) return;
        _externalActiveConfiguration = next;
        _externalConfigurationError = error;
        _externalPreparedDecision = null;
        try { _externalDecisionCancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void BeginExternalObservation(bool allowInput)
    {
        if (_externalObserving || _externalFrame == null || _externalCalibration == null || _externalDesktop == null) return;
        var resume = _externalLinked && _externalTracker != null;
        _externalObserving = true;
        _externalRunning = allowInput;
        RefreshExternalControllerConfiguration();
        var red = ExternalSideBox.SelectedIndex == 0;
        var model = _externalActiveConfiguration?.Model;
        var fields = (ExternalFenBox.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2)
        { _externalObserving = _externalRunning = false; throw new FormatException("请填入有效 FEN。"); }
        if (_externalTurnKnown) fields[1] = ExternalTurnBox.SelectedIndex == 0 ? "w" : "b";
        var fen = resume ? _game.CurrentFen() : string.Join(' ', fields);
        _externalRecoveryMessage = null;
        _externalPreparedDecision = null;
        var cancellation = new CancellationTokenSource();
        _externalCancellation = cancellation;
        var initialized = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _externalObservationReady = initialized;
        CancelSearch(); StopLlmInsightWorker(); _enginePaused = true;
        RefreshGuidanceVisibility(); RefreshUi();
        _externalTask = RunExternalLifetimeAsync(resume, fen, red, model, cancellation, initialized);
    }

    private async Task PauseExternalObservationForSetupAsync()
    {
        if (!_externalObserving) return;
        _externalCancellation?.Cancel();
        if (_externalTask != null) await _externalTask;
    }

    private void ResumeExternalObservationAfterSetup(bool wasObserving)
    {
        if (wasObserving && !_closing && _externalPermissions.Ready && _externalPositionReady)
            BeginExternalObservation(allowInput: false);
    }

    private async Task RunExternalLifetimeAsync(bool resume, string fen, bool red,
        LlmConnectionSettings? model, CancellationTokenSource cancellation, TaskCompletionSource<bool> initialized)
    {
        try { await RunExternalSessionAsync(resume, fen, red, model, cancellation.Token); }
        catch (OperationCanceledException) { /* Session teardown also handles early initialization cancellation. */ }
        catch (Exception ex) { if (!_closing) ExternalStatusText.Text = ex.Message; }
        finally
        {
            initialized.TrySetResult(false);
            if (ReferenceEquals(_externalCancellation, cancellation))
            {
                _externalCancellation = null;
                _externalObserving = _externalRunning = false;
                _externalPreparedDecision = null;
                _externalActiveConfiguration = null;
                _externalConfigurationError = null;
                if (!_closing) RefreshUi();
            }
            cancellation.Dispose();
        }
    }
}
