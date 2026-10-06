using Avalonia.Interactivity;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private async void ExternalConnect_Click(object? sender, RoutedEventArgs e) => await ConnectExternalAsync(false);
    private bool _externalResyncing;
    private async void ExternalResync_Click(object? sender, RoutedEventArgs e)
    {
        if (_externalResyncing || _externalCalibrating) return;
        _externalResyncing = true;
        var resume = _externalRunning;
        var synced = false;
        try
        {
            _externalCancellation?.Cancel();
            if (_externalTask != null) await _externalTask;
            synced = await ConnectExternalAsync(true);
        }
        finally { _externalResyncing = false; if (!_closing) RefreshExternalControls(); }
        if (synced && resume && !_closing) ExternalStart_Click(sender, e);
    }
    private async Task<bool> ConnectExternalAsync(bool useCurrentTarget)
    {
        if (_externalRunning || _externalCalibrating || !await EnsureExternalPermissionsAsync()) return false;
        if (_externalRunning || _externalCalibrating || _closing) return false;
        var target = useCurrentTarget ? _externalFrame?.Window : ExternalWindowBox.SelectedItem as ExternalWindow;
        if (target == null) { ExternalStatusText.Text = "先选择要接管的窗口。"; return false; }
        _externalCalibrating = true;
        using var cancellation = new CancellationTokenSource(); _externalSetupCancellation = cancellation;
        RefreshExternalControls();
        try
        {
            await PauseExternalObservationForSetupAsync();
            if (_closing) return false;
            // A manual correction applies to its game only. New games and new
            // targets must re-infer turn/orientation instead of inheriting it.
            var newGame = _externalCompleted || (!useCurrentTarget &&
                (_externalFrame?.Window.Id != target.Id || _externalFrame?.Window.Pid != target.Pid));
            if (newGame && ExternalAutoTurnCheck.IsChecked == true)
            { _externalRequestedTurn = null; _externalTurnKnown = false; }
            ExternalStatusText.Text = "正在读取棋盘并同步局面…";
            var frame = await _externalDesktop!.CaptureAsync(target, cancellation.Token);
            BoardCalibration? geometry = null;
            if (useCurrentTarget && _externalFrame is { } previous && _externalCalibration is { } previousGeometry &&
                ExternalCaptureGeometry.TryRebaseCalibration(previous, frame, previousGeometry, out var mapped))
                geometry = mapped;
            geometry ??= await Task.Run(() => BoardLocator.Locate(frame), cancellation.Token);
            if (geometry == null) { ExternalStatusText.Text = "没有找到可靠棋盘，请使用“手动调整棋盘标定”。"; return false; }
            // Piece distribution in a late game cannot reliably tell us which way the board faces.
            // Preserve the established orientation when re-reading the same connected target.
            if (newGame || !useCurrentTarget || !_externalLinked)
            {
                var orientation = await Task.Run(() => BoardLocator.DetectOrientation(frame,geometry), cancellation.Token);
                if (orientation is { } redAtTop) ExternalOrientationBox.SelectedIndex = redAtTop ? 1 : 0;
            }
            if (_externalLinked) _game.GoToPly(_game.TotalPly);
            _externalFrame = frame; _externalCalibration = geometry; SetExternalPreview(frame);
            // No mouse input is sent while resynchronizing. Preserve the old game until recognition succeeds.
            var ready = await RecognizeExternalFrameAsync(frame);
            if (ready && !_externalLinked && !_externalTurnKnown && ExternalAutoTurnCheck.IsChecked == true)
                await TryReadExternalClockTurnAsync(cancellation.Token);
            if (ready && !_externalLinked && !_externalTurnKnown)
                ExternalStatusText.Text = "棋盘已同步；未找到可靠的行棋提示。开始接管后会观察下一步落子，也可手动指定当前方。";
            if (ready)
            {
                _externalCompleted = false;
                BeginExternalObservation(allowInput: false);
                if (_externalObservationReady != null && !await _externalObservationReady.Task) return false;
            }
            ExternalCalibrationText.Text = "棋盘已定位 · 可直接重新同步，无需重复标定";
            return ready;
        }
        catch (ExternalPermissionException ex)
        {
            await EnsureExternalPermissionsAsync(); ExternalPermissionMessage.Text = ex.Message;
        }
        catch (OperationCanceledException) { ExternalStatusText.Text = "已取消同步，原棋谱保留。"; }
        catch (Exception ex) { ExternalStatusText.Text = "同步未完成：" + ex.Message; }
        finally { _externalSetupCancellation = null; _externalCalibrating = false; RefreshExternalControls(); }
        return false;
    }

    private async Task TryReadExternalClockTurnAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS() || _externalFrame == null || _externalCalibration == null || _externalDesktop == null) return;
        var first = _externalFrame;
        var geometry = _externalCalibration with { RedAtTop = ExternalOrientationBox.SelectedIndex == 1 };
        var firstRead = ExternalTurnInference.ReadClocksAsync(first.Png, geometry, ct);
        await Task.Delay(1150, ct);
        var next = await _externalDesktop.CaptureAsync(first.Window, ct);
        if (!ExternalCaptureGeometry.TryRebaseCalibration(first, next, geometry, out var nextGeometry)) return;
        var stable = await Task.Run(() =>
            BoardObservation.Read(first, geometry).StableWith(BoardObservation.Read(next, nextGeometry)), ct);
        if (!stable) return;
        var before = await firstRead;
        var after = await ExternalTurnInference.ReadClocksAsync(next.Png, nextGeometry, ct);
        if (before is not { } firstClock || after is not { } lastClock) return;
        var topToMove = ExternalTurnInference.RunningClock(firstClock, lastClock);
        if (topToMove is not { } top) return;
        var red = top == geometry.RedAtTop;
        _externalFrame = next; _externalCalibration = nextGeometry;
        ConfirmExternalTurn(red, "已从目标窗口的运行计时器确认");
    }
}
