using PaddiXiangqi.Sessions;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private bool _syncExternalModels;
    private bool _externalChangingSide, _externalChangingTurn;
    private bool _externalTurnKnown = true;
    private bool? _externalRequestedTurn;
    private bool _externalPositionReady;
    private IExternalDesktop? _externalDesktop;
    private ExternalFrame? _externalFrame;
    private BoardCalibration? _externalCalibration;
    private ExternalBoardTracker? _externalTracker;
    private CancellationTokenSource? _externalCancellation;
    private CancellationTokenSource? _externalSetupCancellation;
    private Task? _externalTask;
    private bool _externalLinked, _externalRunning, _externalCalibrating;
    private bool _externalCompleted;
    private string? _externalPendingMove;
    private LlmMoveResult? _externalPendingThought;
    private string? _externalRecoveryMessage;
    private PikafishClient? _externalPlayingEngine;

    private async Task<PikafishClient> GetExternalPlayingEngineAsync()
    {
        if (_externalPlayingEngine is { } existing &&
            existing.OverridePath == _engine.OverridePath && existing.OverrideEvalPath == _engine.OverrideEvalPath &&
            existing.OverrideRuleOptions.Count == _engine.OverrideRuleOptions.Count &&
            existing.OverrideRuleOptions.All(item => _engine.OverrideRuleOptions.TryGetValue(item.Key, out var value) && value == item.Value))
            return existing;
        if (_externalPlayingEngine is not null) await _externalPlayingEngine.DisposeAsync();
        return _externalPlayingEngine = new PikafishClient
        { OverridePath = _engine.OverridePath, OverrideEvalPath = _engine.OverrideEvalPath,
          OverrideRuleOptions = new Dictionary<string, string>(_engine.OverrideRuleOptions, StringComparer.OrdinalIgnoreCase) };
    }

    private static async Task WarmExternalEngineAsync(PikafishClient engine, EngineSettings settings, CancellationToken ct)
    {
        try { await engine.PrepareAsync(settings, ct); }
        // Search reports a persistent engine error; preparation alone must not stop observation.
        catch (Exception) { }
    }

    private void ShowExternalRecovery(string? message)
    {
        if (_externalRecoveryMessage == message) return;
        _externalRecoveryMessage = message;
        RefreshExternalControls();
        if (message != null) ExternalStatusText.Text = message;
    }

    private async Task<ExternalFrame> CaptureExternalWithRecoveryAsync(IExternalDesktop desktop, ExternalWindow target, CancellationToken ct)
    {
        var retries = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return await desktop.CaptureAsync(target, ct); }
            catch (ExternalPermissionException) { throw; }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not FileNotFoundException &&
                ex is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
            {
                // Capture is read-only. Retry the same target, keeping both the confirmed game and
                // any submitted move. Never repeat input when its acknowledgement is uncertain.
                ShowExternalRecovery($"截图暂不可用，保留连接并自动重试（{++retries}） · {ex.Message}");
                await Task.Delay(Math.Min(2000, 500 * retries), ct);
            }
        }
    }

    private void InitializeExternalControls()
    {
        ExternalFenBox.Text = XiangqiGame.InitialFen;
        ExternalSpeedBox.SelectedIndex = Math.Clamp(_preferences.ExternalSyncSpeed, 0, 2);
        ExternalDeliveryBox.SelectedIndex = OperatingSystem.IsMacOS() ? Math.Clamp(_preferences.ExternalInputDelivery, 0, 2) : 0;
        ExternalDeliveryBox.IsEnabled = OperatingSystem.IsMacOS();
        ExternalScoreModeBox.SelectedIndex = Math.Clamp(_preferences.ExternalScoreMode, 0, 2);
        ExternalDeliveryBox.SelectionChanged += (_, _) => { UpdateExternalPerformanceHints(); SaveSettings(); };
        ExternalSpeedBox.SelectionChanged += (_, _) => SaveSettings();
        ExternalScoreModeBox.SelectionChanged += (_, _) =>
        {
            UpdateExternalPerformanceHints();
            if (_externalLinked) { StopLlmInsightWorker(); QueueExternalScore(); }
            SaveSettings();
        };
        RefreshSkins();
        if (!ExternalDesktop.Supported)
        {
            ExternalStatusText.Text = "Linux 版暂未接入外部窗口捕获与输入授权。本地对弈、引擎与大模型功能仍可使用。";
            ExternalRefreshButton.IsEnabled = ExternalConfigPanel.IsEnabled = ExternalStartButton.IsEnabled = false;
        }
        ExternalWindowBox.SelectionChanged += (_, _) =>
        {
            if (_externalLinked || _externalObserving || ExternalWindowBox.SelectedItem is not ExternalWindow selected)
            { if (_ready) RefreshExternalControls(); return; }
            // Replacing list items during refresh briefly clears selection. Only
            // choosing a different target invalidates the retained next-game geometry.
            if (_externalFrame?.Window is { } current && current.Id == selected.Id && current.Pid == selected.Pid)
            { if (_ready) RefreshExternalControls(); return; }
            _externalFrame = null; _externalCalibration = null;
            _externalCompleted = false;
            ExternalCalibrationText.Text = "窗口已改变，请重新标定";
            _externalPositionReady = false; _externalTurnKnown = false;
            if (_ready) RefreshExternalControls();
        };
        // Entering a FEN is itself an explicit position choice; validate it when starting.
        ExternalFenBox.TextChanged += (_, _) =>
        { _externalPositionReady = !string.IsNullOrWhiteSpace(ExternalFenBox.Text); if (_ready) RefreshExternalControls(); };
        ExternalOrientationBox.SelectionChanged += (_, _) =>
        { _externalPositionReady = false; SelectLowerExternalSide(); if (_ready) RefreshExternalControls(); };
        ExternalSideBox.SelectionChanged += (_, _) =>
        {
            if (!_externalChangingSide) ExternalAutoSideCheck.IsChecked = false;
            UpdateExternalModelLabel();
        };
        ExternalAutoSideCheck.IsCheckedChanged += (_, _) => SelectLowerExternalSide();
        ExternalTurnBox.SelectionChanged += (_, _) =>
        {
            if (_externalChangingTurn) return;
            if (_externalLinked && !_externalTurnKnown && _game.Ply == 0)
            {
                ConfirmExternalTurn(ExternalTurnBox.SelectedIndex == 0, "已手动指定行棋方", manual: true);
                return;
            }
            // A manual correction confirms this position, without changing the user's
            // preference to automatically identify later turns and subsequent games.
            _externalRequestedTurn = ExternalTurnBox.SelectedIndex == 0;
            _externalTurnKnown = true;
        };
        ExternalAutoTurnCheck.IsCheckedChanged += (_, _) =>
        {
            if (ExternalAutoTurnCheck.IsChecked != true)
            {
                if (_externalLinked && !_externalTurnKnown && _game.Ply == 0)
                    ConfirmExternalTurn(ExternalTurnBox.SelectedIndex == 0, "已手动指定行棋方", manual: true);
                else _externalTurnKnown = true;
                return;
            }
            if (_externalLinked) return;
            _externalRequestedTurn = null;
            var inferred = ExternalTurnInference.FromPosition(_game.CurrentFen());
            _externalTurnKnown = inferred.HasValue;
            SetExternalTurn(inferred);
        };
        ExternalControllerBox.SelectionChanged += (_, _) => { UpdateExternalModelLabel(); UpdateExternalPerformanceHints(); };
        RedActiveModelList.SelectionChanged += (_, _) => UpdateExternalModelLabel();
        BlackActiveModelList.SelectionChanged += (_, _) => UpdateExternalModelLabel();
        RedLlmReasoningBox.SelectionChanged += (_, _) => UpdateExternalModelLabel();
        BlackLlmReasoningBox.SelectionChanged += (_, _) => UpdateExternalModelLabel();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { _externalCancellation?.Cancel(); _externalSetupCancellation?.Cancel(); } };
        MainTabs.SelectionChanged += async (_, e) =>
        {
            if (e.Source != MainTabs) return;
            RefreshWorkspaceScene();
            if (MainTabs.SelectedIndex == 3) await EnsureExternalPermissionsAsync();
        };
        Activated += async (_, _) =>
        {
            if (MainTabs.SelectedIndex == 3 && !_externalRunning && !_externalCalibrating && !_closing)
                await EnsureExternalPermissionsAsync();
        };
        ExternalAnalysisCheck.IsCheckedChanged += (_, _) => AnalysisModeCheck.IsChecked = ExternalAnalysisCheck.IsChecked;
        AnalysisModeCheck.IsCheckedChanged += (_, _) => ExternalAnalysisCheck.IsChecked = AnalysisModeCheck.IsChecked;
        ExternalAnalysisCheck.IsChecked = AnalysisModeCheck.IsChecked;
        ExternalModelBox.SelectionChanged += (_, _) =>
        {
            if (_syncExternalModels || _externalRunning) return;
            (ExternalSideBox.SelectedIndex == 0 ? RedActiveModelList : BlackActiveModelList).SelectedItem = ExternalModelBox.SelectedItem;
        };
        ExternalEffortBox.SelectionChanged += (_, _) =>
        {
            if (_syncExternalModels || _externalRunning) return;
            (ExternalSideBox.SelectedIndex == 0 ? RedLlmReasoningBox : BlackLlmReasoningBox).SelectedIndex = ExternalEffortBox.SelectedIndex;
        };
        SelectLowerExternalSide();
        UpdateExternalModelLabel();
        UpdateExternalPerformanceHints();
        RefreshWorkspaceScene();
    }
    private void UpdateExternalPerformanceHints()
    {
        ExternalDeliveryHintText.Text = ExternalDeliveryBox.SelectedIndex switch
        {
            1 => "后台窗口事件：为目标窗口建立独立输入上下文，首步也不激活前台、不移动系统光标。切换桌面后须持续有可用截图；目标暂停渲染时等待恢复，不重复点击。",
            2 => "窗口事件自动聚焦：落子前激活并聚焦目标，再发送窗口内点击；系统光标不移动。目标在另一桌面时会切回其桌面。适合拒绝后台点击的窗口。",
            _ => "系统鼠标兼容更多游戏和镜像窗口；落子时会移动光标。macOS 可试选窗口事件自动聚焦。"
        };
        ExternalScoreHintText.Text = ExternalScoreModeBox.SelectedIndex switch
        {
            2 => "暂停额外评分；执棋计算仍正常进行。",
            1 => "补充双方局面评分：固定 1 线程、约 1 秒；我方引擎开始计算前会停止后台搜索。评分只显示在本客户端。",
            _ when ExternalControllerBox.SelectedIndex == 0 => "复用执棋搜索的评分，不启动第二个进程；未评分的局面留空。评分只显示在本客户端。",
            _ => "大模型执棋时，默认引擎用 1 线程在后台评分；评分仅显示在本客户端。"
        };
    }
    private void SelectLowerExternalSide()
    {
        if (ExternalAutoSideCheck.IsChecked != true || ExternalOrientationBox.SelectedIndex < 0) return;
        _externalChangingSide = true;
        try { ExternalSideBox.SelectedIndex = ExternalOrientationBox.SelectedIndex == 1 ? 1 : 0; }
        finally { _externalChangingSide = false; }
        UpdateExternalModelLabel();
    }
    private void SetExternalTurn(bool? red)
    {
        _externalChangingTurn = true;
        try { ExternalTurnBox.SelectedIndex = red.HasValue ? red.Value ? 0 : 1 : -1; }
        finally { _externalChangingTurn = false; }
    }
    private void InferExternalTurn(string fen)
    {
        if (_externalRequestedTurn is { } chosen) { SetExternalTurn(chosen); _externalTurnKnown = true; return; }
        if (ExternalAutoTurnCheck.IsChecked != true) { _externalTurnKnown = true; return; }
        var inferred = ExternalTurnInference.FromPosition(fen);
        _externalTurnKnown = inferred.HasValue;
        SetExternalTurn(inferred);
    }
    private void ConfirmExternalTurn(bool red, string evidence, bool manual = false)
    {
        if (_game.Ply != 0 || _externalTurnKnown) return;
        var fields = _game.StartFen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        fields[1] = red ? "w" : "b";
        _game.LoadFen(string.Join(' ', fields));
        if (manual) _externalRequestedTurn = null;
        SetExternalTurn(red);
        _externalTurnKnown = true;
        ExternalFenBox.Text = _game.CurrentFen();
        _scores.Clear(); _scoreLabels.Clear();
        ExternalStatusText.Text = $"{evidence}：当前轮到{(red ? "红" : "黑")}方。";
        RefreshUi();
    }
    private void ExternalChooseTurn_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !_externalLinked || _externalTracker == null) return;
        ConfirmExternalTurn((string?)button.Tag == "red", "已手动指定行棋方", manual: true);
    }
    private void ExternalShowAnalysis_Click(object? sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 0;
    private void ExternalSelectModel_Click(object? sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 2;
    private void UpdateExternalModelLabel()
    {
        var red = ExternalSideBox.SelectedIndex == 0;
        _syncExternalModels = true;
        try
        {
            var source = red ? RedActiveModelList : BlackActiveModelList;
            ExternalModelBox.ItemsSource = source.ItemsSource;
            ExternalModelBox.SelectedItem = source.SelectedItem;
            ExternalEffortBox.SelectedIndex = (red ? RedLlmReasoningBox : BlackLlmReasoningBox).SelectedIndex;
            ExternalModelPanel.IsVisible = ExternalControllerBox.SelectedIndex == 1;
        }
        finally { _syncExternalModels = false; }
        ExternalModelText.Text = ExternalControllerBox.SelectedIndex == 0
            ? $"将由 {DefaultEngine.Name} 接管{(red ? "红" : "黑")}方" + (ExternalAutoSideCheck.IsChecked == true ? "（棋盘下方）" : "")
            : $"{(red ? "红" : "黑")}方 · {LlmControllerLabel(red)} · 思考等级 {ReasoningLabel(LlmControls(red).Reasoning.SelectedIndex)}";
        RefreshExternalControllerConfiguration();
    }
    private async void ExternalRefresh_Click(object? sender, RoutedEventArgs e)
    {
        if (_externalRunning || _externalCalibrating || !await EnsureExternalPermissionsAsync()) return;
        if (_externalRunning || _externalCalibrating || _closing) return;
        ExternalRefreshButton.IsEnabled = false;
        try
        {
            _externalDesktop ??= ExternalDesktop.Create();
            var windows = await _externalDesktop.ListAsync(CancellationToken.None);
            var previousId = (ExternalWindowBox.SelectedItem as ExternalWindow)?.Id;
            var choices = windows.Where(w => w.Pid != Environment.ProcessId).ToArray();
            ExternalWindowBox.ItemsSource = choices;
            ExternalWindowBox.SelectedItem = choices.FirstOrDefault(w => w.Id == previousId)
                ?? choices.FirstOrDefault(w => w.Title.Contains("象棋", StringComparison.OrdinalIgnoreCase))
                ?? choices.FirstOrDefault();
            ExternalStatusText.Text = choices.Length == 0 ? "没有找到可见窗口，请先打开目标棋盘并退出最小化。"
                : "选择目标象棋窗口，然后截取并标定。";
        }
        catch (Exception ex) { ExternalStatusText.Text = ex.Message; }
        finally { ExternalRefreshButton.IsEnabled = !_externalLinked; }
    }
    private async void ExternalCalibrate_Click(object? sender, RoutedEventArgs e) => await CalibrateExternalAsync();
    private async Task CalibrateExternalAsync()
    {
        if (_externalRunning || _externalCalibrating || !await EnsureExternalPermissionsAsync()) return;
        if (_externalRunning || _externalCalibrating || _closing) return;
        if (ExternalWindowBox.SelectedItem is not ExternalWindow target)
        { ExternalStatusText.Text = "请先刷新并选择一个窗口。"; return; }
        var wasObserving = _externalObserving;
        _externalCalibrating = true;
        using var setupCancellation = new CancellationTokenSource(); _externalSetupCancellation = setupCancellation;
        RefreshExternalControls();
        try
        {
            await PauseExternalObservationForSetupAsync();
            _externalDesktop ??= ExternalDesktop.Create();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(setupCancellation.Token); timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var frame = await _externalDesktop.CaptureAsync(target, timeout.Token);
            var dialog = new ExternalCalibrationWindow(frame.Png, setupCancellation.Token);
            if (await dialog.ShowDialog<BoardCalibration?>(this) is { } selected)
            {
                // The opponent may have moved while the calibration dialog was open.
                // Keep the geometry, but always recognize a newly captured board.
                var current = await _externalDesktop.CaptureAsync(target, setupCancellation.Token);
                if (!ExternalCaptureGeometry.TryRebaseCalibration(frame, current, selected, out var currentCalibration))
                    throw new InvalidOperationException("标定期间窗口尺寸改变，请重新定位。");
                selected = currentCalibration;
                frame = current;
                _externalFrame = frame; _externalCalibration = selected; SetExternalPreview(frame);
                // Geometry is now new: an old pending move must never be replayed into it.
                _externalLinked = false; _externalTracker = null;
                _externalPendingMove = null; _externalPendingThought = null;
                RefreshExternalControls();
                var redAtTop = await Task.Run(() => BoardLocator.DetectOrientation(frame, selected));
                if (redAtTop is { } red) ExternalOrientationBox.SelectedIndex = red ? 1 : 0;
                ExternalCalibrationText.Text = "已标定 90 个交叉点 · " +
                    (redAtTop is null ? "方向无法确定，请手动选择" : redAtTop.Value ? "识别为红上黑下，请核对" : "识别为黑上红下，请核对");
                _externalPositionReady = false;
                await RecognizeExternalFrameAsync(frame);
                ExternalStatusText.Text = "棋盘已重新定位。请核对左侧局面、行棋方和朝向后开始接管。";
            }
        }
        catch (OperationCanceledException) { ExternalStatusText.Text = setupCancellation.IsCancellationRequested ? "已停止标定与识别。" : "截取窗口超时，请检查录屏权限和窗口状态。"; }
        catch (Exception ex) { ExternalStatusText.Text = ex.Message; }
        finally { _externalSetupCancellation = null; _externalCalibrating = false; ResumeExternalObservationAfterSetup(wasObserving); RefreshExternalControls(); }
    }
    private void ExternalStandard_Click(object? sender, RoutedEventArgs e)
        => ExternalStandardOpening_Click(sender, e);
    private void ExternalUsePosition_Click(object? sender, RoutedEventArgs e)
    { if (_externalObserving) return; ExternalFenBox.Text = _game.CurrentFen(); SetExternalTurn(_game.RedToMove); _externalTurnKnown = true; }

    private async void ExternalStart_Click(object? sender, RoutedEventArgs e)
    {
        if (_externalResyncing || _enginePluginWork) return;
        if (_externalRunning || _externalCalibrating || _externalStarting) return;
        _externalStarting = true;
        RefreshExternalControls();
        try { await StartExternalCoreAsync(sender, e); }
        catch (Exception ex) { if (!_closing) ExternalStatusText.Text = ex.Message; }
        finally { _externalStarting = false; if (!_closing) RefreshExternalControls(); }
    }
    private async Task StartExternalCoreAsync(object? sender, RoutedEventArgs e)
    {
        if (!await EnsureExternalPermissionsAsync()) return;
        if (_externalRunning || _externalCalibrating || _closing) return;
        if (_externalFrame == null || _externalCalibration == null || _externalDesktop == null)
        { ExternalStatusText.Text = "请先选择棋盘窗口，点击「① 连接并同步」，再开始接管。"; return; }
        // Completion retains the target and record, but the next session must read
        // the live game instead of reusing the last terminal screenshot.
        if (_externalCompleted && !await ConnectExternalAsync(true)) return;
        if (_closing) return;
        if (_externalLinked && _externalRequestedTurn is { } chosenTurn && chosenTurn != _game.RedToMove)
        {
            if (!await ConnectExternalAsync(true)) return;
            if (_closing) return;
        }
        if (_externalObserving)
        {
            RefreshExternalControllerConfiguration();
            if (_externalConfigurationError != null)
            { ExternalStatusText.Text = _externalConfigurationError; return; }
            if (_externalPreparedDecision is { Error: not null }) _externalPreparedDecision = null;
            _externalRunning = true;
            await RecordExternalHistoryAsync("resumed", "已允许接管落子，发送前复核当前棋盘");
            ExternalStatusText.Text = _externalPreparedDecision != null
                ? "已允许落子，正在复核最新棋盘…" : "已允许落子，等待当前局面的计算结果…";
            RefreshUi();
            return;
        }
        if (!_externalLinked && !_externalPositionReady)
        {
            _externalCalibrating = true; RefreshExternalControls();
            try { if (!await RecognizeExternalFrameAsync(_externalFrame)) return; }
            catch (Exception ex) { ExternalStatusText.Text = "识别未完成：" + ex.Message; return; }
            finally { _externalCalibrating = false; RefreshExternalControls(); }
            if (_closing) return;
        }
        if (_setupBoard != null) { ExternalStatusText.Text = "请先完成或取消摆棋。"; return; }
        bool red = ExternalSideBox.SelectedIndex == 0, llm = ExternalControllerBox.SelectedIndex == 1;
        LlmConnectionSettings? model;
        string fen;
        try
        {
            model = llm ? ReadLlmSettings(red) : null;
            if (_externalLinked && _externalTracker != null)
            {
                _game.GoToPly(_game.TotalPly);
                fen = _game.CurrentFen();
            }
            else
            {
                var fields = (ExternalFenBox.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2) throw new FormatException("请填入有效 FEN。");
                if (_externalTurnKnown) fields[1] = ExternalTurnBox.SelectedIndex == 0 ? "w" : "b";
                fen = string.Join(' ', fields);
            }
            var validation = new XiangqiGame { ExternalAdjudication = true }; validation.LoadFen(fen);
        }
        catch (Exception ex) { ExternalStatusText.Text = ex.Message; return; }
        BeginExternalObservation(allowInput: true);
        if (_externalTask != null) await _externalTask;
    }
    private async Task RunExternalSessionAsync(bool resume, string fen, bool red, LlmConnectionSettings? model, CancellationToken ct)
    {
        var initialized = _externalObservationReady;
        var desktop = _externalDesktop!;
        var calibration = _externalCalibration! with { RedAtTop = ExternalOrientationBox.SelectedIndex == 1 };
        var geometryFrame = _externalFrame!;
        var target = geometryFrame.Window;
        desktop.InputMode = ExternalInputBox.SelectedIndex == 1 ? ExternalInputMode.Drag : ExternalInputMode.Click;
        desktop.InputDelivery = ExternalDeliveryBox.SelectedIndex switch
        { 1 => ExternalInputDelivery.TargetWindow, 2 => ExternalInputDelivery.TargetWindowFocused, _ => ExternalInputDelivery.SystemCursor };
        int pollMs = ExternalSpeedBox.SelectedIndex switch { 0 => 33, 2 => 250, _ => 80 };
        var emergency = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Input, (_, _) =>
        {
            try { if (desktop.EscapePressed()) _externalCancellation?.Cancel(); }
            catch { _externalCancellation?.Cancel(); }
        });
        var engine = await GetExternalPlayingEngineAsync();
        if (!resume) engine.RequestNewGame();
        Task? warmup = null;
        Task? releaseLocalEngine = null;
        Task<Exception?>? inputTask = null;
        Task<ExternalDecision>? decisionTask = null;
        CancellationTokenSource? decisionCancellation = null;
        string? decisionFen = null;
        ExternalControllerConfiguration? decisionConfiguration = null;
        ExternalDecision? lastInputDecision = null;
        ExternalDecision? recordedDecision = null;
        var inputRetryAfter = DateTimeOffset.MinValue;
        string? submittedMove = null;
        GameResult? completedResult = null;
        using var inputLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var warmupLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var recognitionRefresh = new ExternalRecognitionRefresh(ct);
        await using var inputVerification = new ExternalRecognitionRefresh(ct, TimeSpan.FromMilliseconds(250));
        var inputRetry = new ExternalInputRetry();
        string? lastVerificationStatus = null;
        BoardObservation? submittedFrame = null;
        async Task<Exception?> SendInputAsync(double fx, double fy, double tx, double ty, bool finishSelection = false)
        {
            try
            {
                if (finishSelection) await desktop.CompleteSelectedMoveAsync(target, fx, fy, tx, ty, inputLifetime.Token);
                else await desktop.MoveAsync(target, fx, fy, tx, ty, inputLifetime.Token);
                return null;
            }
            catch (Exception ex) { return ex; } // Observed by the loop, including partial-input outcomes.
        }
        try
        {
            // Local analysis is already cancelled. Release its potentially large hash
            // without making live capture wait for the old search to drain.
            releaseLocalEngine = _engine.ReleaseResourcesAsync(ct);
            if (model == null) warmup = WarmExternalEngineAsync(engine, ReadPlayingEngineSettings(), warmupLifetime.Token);
            // Cancelled local work has generation-guarded callbacks and its own
            // engine/request. It must not block observation or this game's first move.
            ct.ThrowIfCancellationRequested();
            // External adjudication belongs to the target game. Local friendly-game
            // History adjudication must not stop screenshot tracking.
            _game.ExternalAdjudication = true;
            if (!resume)
            {
                var initial = await Task.Run(() => BoardObservation.Read(geometryFrame, calibration), ct);
                // Establish the reference from the recognized connection frame first.
                // The loop immediately reads a fresh frame and checks identity/size and
                // legal changes; a temporary capture failure must keep this reference.
                // Connection/editor already established this frame's board. Do not
                // classify it again against every bundled skin before observation
                // can begin; generic themes use the same verified reference.
                await EndExternalHistoryAsync("新接管会话替换当前局面");
                _game.LoadFen(fen); _notes.Clear(); _scores.Clear(); _scoreLabels.Clear();
                ResetLlmInsights(); ResetLlmThinking(); _pendingLlmDrawOffer = null; _lastLlmDrawOfferPly = -8;
                _externalPendingMove = null; _externalPendingThought = null; _externalMoveRecovery = null;
                _externalTracker = await Task.Run(() => new ExternalBoardTracker(initial, _game), ct);
                try { _externalSessionSkin = await Task.Run(() => BoardSkin.Learn("本次已核对棋盘", initial, _game, requireAllPieces:false), ct); } catch (InvalidOperationException) { }
                RecordTitleBox.Text = "外部接管 · " + target.Title;
                _moveListDirty = true; ClearSelection(); ResetAnalysisDisplay(); UpdateAnnotationEditor();
            }
            else if (_externalTracker == null) throw new InvalidOperationException("初始化尚未完成，点击重新同步即可恢复。");
            ct.ThrowIfCancellationRequested();
            await BeginExternalHistoryAsync("外部接管 · " + target.Title);
            if (resume) await RecordExternalHistoryAsync("resumed", "恢复截图观察，保留已有棋谱及未确认着法", move: _externalPendingMove);
            _externalLinked = true;
            _game.GoToPly(_game.TotalPly); Board.Flipped = calibration.RedAtTop; Board.Refresh(); RefreshUi();
            initialized?.TrySetResult(true);
            if (_externalTurnKnown) QueueExternalScore();
            var synchronization = new ExternalSynchronizationSession();
            var observationReader = new ExternalObservationReader(calibration);
            string? pending = _externalPendingMove;
            LlmMoveResult? pendingThought = _externalPendingThought;
            var moveSentAt = DateTimeOffset.UtcNow;
            var cadence = Stopwatch.StartNew();
            var sessionClock = Stopwatch.StartNew();
            long nextMetricsAt = 0;
            string? lastObservationEvent = null;
            string? lastBlockedEvent = null;
            var turnObserver = _externalTurnKnown ? null : new ExternalTurnObserver(_externalTracker.ConfirmedFrame, _game.CurrentFen());
            // Calibration is in screenshot pixels; input is in window points. A
            // display backing-scale change must replace both sampling and mapping
            // together, without treating it as a new board or reusing a cached read.
            bool UpdateCaptureGeometry(ExternalFrame current)
            {
                if (!ExternalCaptureGeometry.TryRebaseCalibration(geometryFrame, current, calibration, out var mapped))
                    throw new InvalidOperationException("窗口尺寸或身份变化，请停止并重新定位棋盘。");
                var changed = mapped != calibration;
                geometryFrame = current;
                target = current.Window;
                if (!changed) return false;
                calibration = mapped;
                _externalCalibration = mapped;
                _externalFrame = current;
                observationReader = new ExternalObservationReader(mapped);
                synchronization.RequireFreshObservation(sessionClock.Elapsed);
                if (!_externalTurnKnown)
                    turnObserver = new ExternalTurnObserver(_externalTracker!.ConfirmedFrame, _game.CurrentFen());
                return true;
            }
            var captureImmediately = true;
            while (!ct.IsCancellationRequested)
            {
                pollMs = ExternalSpeedBox.SelectedIndex switch { 0 => 33, 2 => 250, _ => 80 };
                // Sampling continues during search, model requests and native input. Poll settings are a
                // start-to-start cadence, not an extra sleep added after capture/recognition.
                var recovering = synchronization.UnrecognizedSince is { } since && sessionClock.Elapsed - since > TimeSpan.FromSeconds(3);
                var interval = synchronization.HasCandidate || turnObserver?.HasCandidate == true ? Math.Min(pollMs, 80)
                    : recovering ? Math.Max(pollMs, 120)
                    : pending != null || inputTask != null ? Math.Min(pollMs, 80) : pollMs;
                var delay = captureImmediately ? 0 : Math.Max(1, interval - (int)cadence.ElapsedMilliseconds);
                captureImmediately = false;
                if (delay > 0) await Task.Delay(delay, ct);
                cadence.Restart();
                if (inputTask is { IsCompleted: true })
                {
                    var failure = await inputTask;
                    inputTask = null;
                    if (failure is ExternalInputBlockedException blocked)
                    {
                        await RecordExternalHistoryAsync("blocked", blocked.Message +
                            (blocked.InputStarted ? " · 部分输入已发送，等待截图确认" : " · 尚未发送输入"), move: submittedMove);
                        if (!blocked.InputStarted && _externalPendingMove == submittedMove)
                        {
                            pending = _externalPendingMove = null;
                            pendingThought = _externalPendingThought = null;
                        }
                        ShowExternalRecovery(blocked.Message + (blocked.InputStarted
                            ? " · 正在核验落子结果，不重复点击。" : " · 等待目标窗口可操作后自动继续。"));
                        if (!blocked.InputStarted)
                        {
                            inputRetryAfter = DateTimeOffset.UtcNow.AddMilliseconds(750);
                            // The helper guarantees no input was sent. Reuse this result
                            // only for the same position, without another model/API request.
                            if (lastInputDecision is { } retry)
                            { decisionFen = retry.Fen; decisionTask = Task.FromResult(retry); }
                        }
                    }
                    else if (failure is ExternalPermissionException)
                    {
                        if (_externalPendingMove == submittedMove)
                        { _externalPendingMove = null; _externalPendingThought = null; }
                        throw failure;
                    }
                    else if (failure != null) throw failure;
                }
                var clock = Stopwatch.StartNew();
                var frame = await CaptureExternalWithRecoveryAsync(desktop, target, ct);
                var captureMs = clock.Elapsed.TotalMilliseconds;
                UpdateCaptureGeometry(frame);
                clock.Restart();
                var observation = await observationReader.ReadAsync(frame, ct);
                if (!_externalTurnKnown && turnObserver != null)
                {
                    synchronization.ResetCompletion();
                    var skins = _externalSessionSkin == null ? BuiltInBoardSkins.All
                        : new[] { _externalSessionSkin }.Concat(BuiltInBoardSkins.All);
                    var observedMove = await Task.Run(() => turnObserver.Observe(observation, sessionClock.Elapsed, skins), ct);
                    if (sessionClock.ElapsedMilliseconds >= nextMetricsAt)
                    {
                        ExternalMetricsText.Text = $"截图 {captureMs:F0} ms · 观察行棋方 · {_game.Ply} 手";
                        nextMetricsAt = sessionClock.ElapsedMilliseconds + 200;
                    }
                    if (observedMove == null)
                    {
                        ExternalStatusText.Text = "中途接入：正在观察下一步落子以确认行棋方；也可在下方手动指定。";
                        continue;
                    }
                    ConfirmExternalTurn(observedMove.MoverRed, "已根据外部落子确认行棋方");
                    await AcceptExternalMatchAsync(observedMove.Match, observation, model, ct);
                    captureImmediately = true;
                    continue;
                }
                var recognitionIdentity = _game.StartFen + "|" + _game.UciMoveList;
                var fullRead = await Task.Run(() => recognitionRefresh.Current(recognitionIdentity, pending, observation), ct);
                var sync = await Task.Run(() => synchronization.Observe(observation, _externalTracker!,
                    _game, pending, inputTask != null, _externalSessionSkin, _externalMoveRecovery, sessionClock.Elapsed, fullRead), ct);
                var match = sync.Match;
                var observationEvent = _game.Ply + "|" + match.Recognized + "|" + string.Join(' ', match.Moves) + "|" + match.Message + "|" + sync.ObservedFen;
                if (observationEvent != lastObservationEvent && (!sync.Confirmed || match.Moves.Count > 0))
                {
                    lastObservationEvent = observationEvent;
                    await RecordExternalHistoryAsync("observation", match.Message, candidates: match.Moves, observedFen: sync.ObservedFen);
                }
                var recognitionMs = clock.Elapsed.TotalMilliseconds;
                // Board confirmation stays at capture cadence; diagnostic text needs
                // only 5 Hz and must not trigger text layout on every captured frame.
                if (sessionClock.ElapsedMilliseconds >= nextMetricsAt || match.Moves.Count > 0)
                {
                    ExternalMetricsText.Text = $"截图 {captureMs:F0} ms · 识别 {recognitionMs:F0} ms · 误差 {match.Error:P1} · {_game.Ply} 手" +
                        (synchronization.CorrectionCount == 0 ? "" : $" · 自动校正 {synchronization.CorrectionCount} 次");
                    nextMetricsAt = sessionClock.ElapsedMilliseconds + 200;
                }
                if (!sync.Confirmed)
                {
                    inputRetry.Invalidate();
                    if (!sync.Match.Recognized && synchronization.UnrecognizedSince is { } unresolved &&
                        sessionClock.Elapsed - unresolved >= TimeSpan.FromMilliseconds(500))
                    {
                        var skin = _externalSessionSkin;
                        var customPath = (ExternalSkinBox.SelectedItem as SkinChoice)?.Path;
                        var redToMove = _game.RedToMove;
                        var recognitionGeometry = calibration;
                        recognitionRefresh.TryStart(recognitionIdentity, pending, observation, sessionClock.Elapsed,
                            token => _positionRecognizer.ReadAsync(frame, recognitionGeometry, redToMove, token, false, skin, customPath));
                    }
                    ShowExternalRecovery(recognitionRefresh.IsRunning
                        ? "正在后台重新识别棋子，连接和棋谱已保留；核验合法着法后自动继续。"
                        : sync.Status);
                    continue;
                }
                var correction = sync.Correction;
                if (DateTimeOffset.UtcNow >= inputRetryAfter) ShowExternalRecovery(null);
                ct.ThrowIfCancellationRequested();
                var priorPly = _game.Ply;
                if (correction != null) await CorrectExternalMatchAsync(correction, match, observation, model);
                else await AcceptExternalMatchAsync(match, observation, model, ct);
                ct.ThrowIfCancellationRequested();
                if (match.Moves.Count > 0 || correction != null) RefreshExternalControllerConfiguration();
                synchronization.OnCommitted(_game, priorPly);
                if (decisionTask is not null && decisionFen != _game.CurrentFen()) decisionCancellation?.Cancel();
                if (_externalPreparedDecision?.Fen != _game.CurrentFen()) _externalPreparedDecision = null;
                pending = _externalPendingMove;
                pendingThought = _externalPendingThought;
                if (_game.Ply != priorPly || correction != null)
                {
                    ExternalMetricsText.Text = $"截图 {captureMs:F0} ms · 识别 {recognitionMs:F0} ms · 误差 {match.Error:P1} · {_game.Ply} 手" +
                        (synchronization.CorrectionCount == 0 ? "" : $" · 自动校正 {synchronization.CorrectionCount} 次");
                    nextMetricsAt = sessionClock.ElapsedMilliseconds + 200;
                }
                completedResult = synchronization.CheckCompletion(_game, sessionClock.Elapsed,
                    turnKnown: _externalTurnKnown, inputPending: pending != null || inputTask != null);
                if (completedResult != null)
                {
                    _externalFrame = frame;
                    break;
                }
                if (_game.Result != GameResult.Ongoing)
                {
                    decisionCancellation?.Cancel();
                    if (decisionTask is { IsCompleted: true })
                    { await decisionTask; decisionTask = null; decisionCancellation?.Dispose(); decisionCancellation = null; }
                    ShowExternalRecovery(_game.SideInCheck
                        ? "正在核验绝杀局面，暂停落子并继续同步；确认后自动结束接管。"
                        : "当前局面暂无合法着法，暂停落子并继续同步；对局结果以目标游戏为准。");
                    continue;
                }
                if (inputTask != null)
                { ExternalStatusText.Text = $"已同步 {_game.Ply} 手 · 正在完成落子操作…"; continue; }
                if (pending != null)
                {
                    // A successfully posted event is not proof that the game handled
                    // it. Complete the same source selection only in this live session,
                    // after two fresh, independently verified unchanged observations.
                    // A move/reply/uncertain image resets the gate. Resume never
                    // blindly replays an old partial submission.
                    var retryRead = await Task.Run(() => inputVerification.Current(recognitionIdentity, null, observation), ct);
                    var verifiedUnchanged = retryRead is { Confident: true } &&
                        ExternalPositionRecovery.SamePieces(retryRead.Fen, _game.CurrentFen());
                    Square.TryParseUci(pending[..2], out var selectedSource);
                    var sourceChanged = submittedFrame != null &&
                        ExternalInputRetry.HasSelectionEvidence(submittedFrame, observation, selectedSource);
                    if (!verifiedUnchanged || !sourceChanged) inputRetry.Invalidate();
                    else if (desktop.CanCompleteSelectedMove && desktop.InputMode == ExternalInputMode.Click &&
                        _externalRunning && lastInputDecision is { } intent && intent.Move == pending &&
                        intent.Fen == _game.CurrentFen() && intent.RuleHistory == _game.UciMoveList &&
                        intent.RuleStartFen == _game.StartFen && decisionConfiguration == _externalActiveConfiguration &&
                        DateTimeOffset.UtcNow >= inputRetryAfter && inputRetry.ObserveUnchanged(sessionClock.Elapsed))
                    {
                        Square.TryParseUci(pending[..2], out var retryFrom); Square.TryParseUci(pending[2..], out var retryTo);
                        var start = calibration.Point(retryFrom); var end = calibration.Point(retryTo);
                        var a = ExternalCaptureGeometry.ToWindowPoint(start.X, start.Y, observation.Width, observation.Height, target);
                        var b = ExternalCaptureGeometry.ToWindowPoint(end.X, end.Y, observation.Width, observation.Height, target);
                        inputRetry.Submitted(sessionClock.Elapsed, retry: true);
                        inputTask = SendInputAsync(a.X, a.Y, b.X, b.Y, finishSelection: true);
                        await RecordExternalHistoryAsync("input-retry", $"确认起点仍被选中且棋盘未走动，补发终点 {inputRetry.Retries}/{ExternalInputRetry.MaximumRetries}", move: pending);
                        ExternalStatusText.Text = $"目标尚未响应，正在重试落子（{inputRetry.Retries}/{ExternalInputRetry.MaximumRetries}）…";
                        captureImmediately = true;
                        continue;
                    }
                    if (DateTimeOffset.UtcNow - moveSentAt > TimeSpan.FromSeconds(7))
                    {
                        ShowExternalRecovery(desktop.InputDelivery == ExternalInputDelivery.TargetWindow
                            ? "后台窗口未确认落子。请停止后选择“窗口事件 · 自动聚焦”并重新同步；棋谱已保留，已选中棋子的终点最多补发两次。"
                            : "目标窗口未确认落子。请检查遮挡或权限后重新同步；棋谱已保留，已选中棋子的终点最多补发两次。");
                    }
                    else ExternalStatusText.Text = "已发送落子，等待截图确认…";
                    continue;
                }
                var configured = _externalActiveConfiguration;
                if (configured == null)
                {
                    ExternalStatusText.Text = _externalConfigurationError ?? "已连接，等待配置执棋方。";
                    continue;
                }
                red = configured.Red;
                model = configured.Model;
                if (decisionTask != null && decisionConfiguration != configured) decisionCancellation?.Cancel();
                if (_game.RedToMove != red)
                {
                    decisionCancellation?.Cancel();
                    if (decisionTask is { IsCompleted: true })
                    { await decisionTask; decisionTask = null; decisionCancellation?.Dispose(); decisionCancellation = null; }
                    ExternalStatusText.Text = $"已同步 {_game.Ply} 手 · 等待{(_game.RedToMove ? "红" : "黑")}方走棋" +
                        (_externalRunning ? "" : " · 已连接，尚未允许落子");
                    continue;
                }
                if (DateTimeOffset.UtcNow < inputRetryAfter) continue;
                if (decisionTask == null && _externalPreparedDecision is { } prepared)
                {
                    if (prepared.Error != null)
                    { ExternalStatusText.Text = "预计算未完成：" + prepared.Error.Message + " · 可调整配置或点击开始重试。"; continue; }
                    if (!_externalRunning)
                    { ExternalStatusText.Text = $"已同步 {_game.Ply} 手 · {(model?.Model ?? DefaultEngine.Name)} 已预计算，点击开始接管即可落子。"; continue; }
                    decisionFen = prepared.Fen;
                    decisionConfiguration = configured;
                    decisionTask = Task.FromResult(prepared);
                }
                if (decisionTask is null)
                {
                    if (_game.AllControllerLegalMoves().Count == 0)
                    {
                        ShowExternalRecovery("当前没有合法候选，暂停自动落子并继续同步；可修正轮次或手动处理。");
                        continue;
                    }
                    decisionFen = _game.CurrentFen();
                    decisionConfiguration = configured;
                    decisionCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _externalDecisionCancellation = decisionCancellation;
                    decisionTask = CalculateExternalDecisionAsync(engine, red, model, decisionCancellation.Token);
                    continue;
                }
                if (!decisionTask.IsCompleted)
                {
                    ExternalStatusText.Text = model is null
                        ? DefaultEngine.Name + " 正在计算 · " + PlayingBudgetDescription(
                            decisionConfiguration?.EngineSettings ?? ReadPlayingEngineSettings())
                        : $"{model.Model} 思考中 · 等级 {model.ReasoningEffort ?? "自动"}";
                    continue;
                }
                var decision = await decisionTask;
                decisionTask = null;
                decisionCancellation?.Dispose(); decisionCancellation = null;
                ct.ThrowIfCancellationRequested();
                if (decision.Cancelled || decision.Fen != _game.CurrentFen() || decisionConfiguration != _externalActiveConfiguration) continue;
                if (decision.RuleHistory is not null &&
                    (decision.RuleStartFen != _game.StartFen || decision.RuleHistory != _game.UciMoveList))
                { _externalPreparedDecision = null; continue; }
                _externalPreparedDecision = decision;
                if (decision.Error is not null)
                {
                    if (_externalRunning) throw decision.Error;
                    await RecordExternalHistoryAsync("error", "预计算失败：" + decision.Error.Message);
                    ExternalStatusText.Text = "预计算未完成：" + decision.Error.Message;
                    continue;
                }
                if (!ReferenceEquals(recordedDecision, decision))
                {
                    recordedDecision = decision;
                    await RecordExternalHistoryAsync("decision", model?.Model ?? DefaultEngine.Name, move: decision.Move,
                        candidates: decision.RuleAllowedMoves ?? _game.AllControllerLegalMoves().Select(move => move.Uci).ToArray());
                }
                if (!_externalRunning) continue;
                var chosen = decision.Move!;
                pendingThought = decision.Thought;
                var legal = _game.AllControllerLegalMoves().Select(move => move.Uci).ToArray();
                if (!legal.Contains(chosen)) throw new InvalidOperationException("执棋方返回非法着法，已停止接管。");
                if (model is not null && decision.RuleAllowedMoves?.Contains(chosen) != true)
                    throw new InvalidOperationException("模型着法没有通过皮卡鱼原生规则校验，未执行落子。");
                // Search/network latency can be long. Recheck the entire board immediately before sending input.
                desktop.InputMode = ExternalInputBox.SelectedIndex == 1 ? ExternalInputMode.Drag : ExternalInputMode.Click;
                desktop.InputDelivery = ExternalDeliveryBox.SelectedIndex switch
                {
                    1 => ExternalInputDelivery.TargetWindow,
                    2 => ExternalInputDelivery.TargetWindowFocused,
                    _ => ExternalInputDelivery.SystemCursor
                };
                var beforeClick = await CaptureExternalWithRecoveryAsync(desktop, target, ct);
                if (UpdateCaptureGeometry(beforeClick)) { captureImmediately = true; continue; }
                var check = await observationReader.ReadAsync(beforeClick, ct);
                // Full inference runs independently so observation, turn updates and
                // engine completion cannot stall behind OCR. Bind the read to history
                // and reclassify changed samples before authorizing this fresh frame.
                var verificationIdentity = _game.StartFen + "|" + _game.UciMoveList;
                var verificationSkin = _externalSessionSkin;
                var verificationPath = (ExternalSkinBox.SelectedItem as SkinChoice)?.Path;
                var identityRead = await Task.Run(() => inputVerification.Current(verificationIdentity, null, check), ct);
                if (identityRead is not { Confident: true })
                {
                    var verificationGeometry = calibration;
                    var verificationTurn = _game.RedToMove;
                    inputVerification.TryStart(verificationIdentity, null, check, sessionClock.Elapsed,
                        token => _positionRecognizer.ReadAsync(beforeClick, verificationGeometry, verificationTurn,
                            token, false, verificationSkin, verificationPath));
                    var status = identityRead is { Uncertain.Count: > 0 }
                        ? $"计算已完成 · 正在核验 {identityRead.Uncertain.Count} 个不确定位置，暂未发送落子。"
                        : "计算已完成 · 正在核验最新棋盘，核验后落子…";
                    ExternalStatusText.Text = status;
                    if (lastVerificationStatus != status)
                    {
                        lastVerificationStatus = status;
                        await RecordExternalHistoryAsync("verification", status, move: chosen, observedFen: identityRead?.Fen);
                    }
                    continue;
                }
                var beforeInput = await Task.Run(() => ExternalInputVerification.Check(_externalTracker!, _game, check, identityRead), ct);
                if (beforeInput is { Recognized: true, Moves.Count: 0 })
                {
                    var latest = await CaptureExternalWithRecoveryAsync(desktop, target, ct);
                    if (UpdateCaptureGeometry(latest)) { captureImmediately = true; continue; }
                    check = await observationReader.ReadAsync(latest, ct);
                    var latestRead = await Task.Run(() => inputVerification.Current(verificationIdentity, null, check), ct);
                    if (latestRead is not { Confident: true })
                    {
                        ExternalStatusText.Text = "计算已完成 · 最新画面仍有变化，正在核验棋子…";
                        captureImmediately = true;
                        continue;
                    }
                    identityRead = latestRead;
                    beforeInput = await Task.Run(() => ExternalInputVerification.Check(_externalTracker!, _game, check, identityRead), ct);
                }
                recognitionRefresh.Seed(verificationIdentity, null, check, identityRead);
                ct.ThrowIfCancellationRequested();
                if (!_externalRunning || !_externalTurnKnown || _game.RedToMove != red ||
                    decision.Fen != _game.CurrentFen() || decision.RuleStartFen != _game.StartFen ||
                    decision.RuleHistory != _game.UciMoveList || decisionConfiguration != _externalActiveConfiguration) continue;
                if (beforeInput is not { Recognized: true, Moves.Count: 0 })
                {
                    // A confident conflict invalidates the decision. An inconclusive
                    // image only needs another verification, not another engine/API request.
                    pendingThought = null;
                    // The normal observation loop invalidates this result when the confirmed history changes.
                    inputRetryAfter = DateTimeOffset.UtcNow.AddMilliseconds(200);
                    synchronization.RequireFreshObservation(sessionClock.Elapsed);
                    ShowExternalRecovery("落子前棋子核验未通过，已拦截本次落子；正在自动核验最新局面。");
                    var blockedEvent = verificationIdentity + "|" + identityRead?.Fen + "|" + beforeInput.Message;
                    if (blockedEvent != lastBlockedEvent)
                    {
                        lastBlockedEvent = blockedEvent;
                        await RecordExternalHistoryAsync("blocked", beforeInput.Message, move: chosen,
                            candidates: beforeInput.Moves, observedFen: identityRead?.Fen);
                    }
                    continue;
                }
                lastBlockedEvent = null; lastVerificationStatus = null;
                Square.TryParseUci(chosen[..2], out var from); Square.TryParseUci(chosen[2..], out var to);
                var source = calibration.Point(from); var dest = calibration.Point(to);
                // Persist the pending move before input. A stop between the two clicks must not lead to a duplicate submission on resume.
                _externalPendingMove = chosen; _externalPendingThought = pendingThought;
                lastInputDecision = decision;
                pending = chosen; submittedMove = chosen; moveSentAt = DateTimeOffset.UtcNow;
                inputRetry.Submitted(sessionClock.Elapsed);
                submittedFrame = check;
                var sourcePoint = ExternalCaptureGeometry.ToWindowPoint(source.X, source.Y, check.Width, check.Height, target);
                var destinationPoint = ExternalCaptureGeometry.ToWindowPoint(dest.X, dest.Y, check.Width, check.Height, target);
                inputTask = SendInputAsync(sourcePoint.X, sourcePoint.Y, destinationPoint.X, destinationPoint.Y);
                await RecordExternalHistoryAsync("sent", "已提交落子输入，等待截图确认实际结果", move: chosen);
                captureImmediately = true;

            }
            ExternalStatusText.Text = completedResult is { } result
                ? $"绝杀 · {(result == GameResult.RedWins ? "红" : "黑")}方获胜，接管已自动结束；最终棋盘和棋谱已保留。"
                : "接管已停止。";
            if (completedResult is null) await RecordExternalHistoryAsync("paused", "接管已停止，棋谱与未确认着法保留", move: _externalPendingMove);
        }
        catch (OperationCanceledException)
        {
            await RecordExternalHistoryAsync("paused", "接管已暂停，继续时先核验最新局面", move: _externalPendingMove);
            ExternalStatusText.Text = "接管已暂停，保留棋盘和棋谱。点击继续会先核验最新局面。";
        }
        catch (ExternalPermissionException ex)
        {
            await RecordExternalHistoryAsync("error", "权限异常：" + ex.Message, move: _externalPendingMove);
            _externalPermissions = ex.Permission == ExternalPermission.ScreenCapture ? new(false,_externalPermissions.Accessibility) : new(_externalPermissions.ScreenCapture,false);
            ShowExternalPermissionState(ex.Message);
        }
        catch (Exception ex)
        {
            await RecordExternalHistoryAsync("error", ex.Message, move: _externalPendingMove);
            ExternalStatusText.Text = ex.Message;
        }
        finally
        {
            emergency.Stop();
            if (completedResult is { } finalResult)
                await EndExternalHistoryAsync("对局结束：" + (finalResult == GameResult.RedWins ? "红方获胜" : "黑方获胜"));
            else await FlushExternalHistoryAsync();
            if (releaseLocalEngine != null)
                try { await releaseLocalEngine; } catch (OperationCanceledException) { }
            warmupLifetime.Cancel();
            if (warmup != null) await warmup;
            decisionCancellation?.Cancel();
            inputLifetime.Cancel();
            if (decisionTask != null) await decisionTask;
            decisionCancellation?.Dispose();
            _externalDecisionCancellation = null;
            if (inputTask != null && await inputTask is ExternalInputBlockedException { InputStarted: false } &&
                _externalPendingMove == submittedMove)
            { _externalPendingMove = null; _externalPendingThought = null; }
            try { await desktop.CloseCaptureAsync(); } catch { /* Stop must still release session controls. */ }
            // SearchAsync drains a cancelled search to bestmove. Keep the idle
            // process, NNUE and hash warm for a pause/resume of this same game.
            if (completedResult != null)
            {
                // Returning to local review must not leave a second full-sized hash allocated.
                await engine.ReleaseResourcesAsync();
                _externalCompleted = true;
                _externalLinked = false; _externalTracker = null; _externalPositionReady = false;
                _externalPendingMove = null; _externalPendingThought = null; _externalMoveRecovery = null;
                _externalRequestedTurn = null;
            }
            _externalRunning = _externalObserving = false; _externalPreparedDecision = null;
            _externalRecoveryMessage = null; _enginePaused = true; if (!_closing) RefreshUi();
        }
    }
    private void QueueExternalScore()
    {
        if (ExternalScoreModeBox.SelectedIndex == 2) return;
        // Default: reuse the playing engine's score; optional light scoring only runs
        // during the opponent's turn and is drained before the next playing search.
        if (ExternalControllerBox.SelectedIndex == 0 &&
            (ExternalScoreModeBox.SelectedIndex == 0 || _game.RedToMove == (ExternalSideBox.SelectedIndex == 0))) return;
        if (_game.Result == GameResult.Ongoing)
            QueueLlmAnalysisSnapshot(_game, _analysisMode ? Math.Clamp((int)(AnalysisLinesBox.Value ?? 3), 1, 5) : 1);
    }
    private void ExternalStop_Click(object? sender, RoutedEventArgs e)
    { _externalCancellation?.Cancel(); _externalSetupCancellation?.Cancel(); }
    private async void ExternalDisconnect_Click(object? sender, RoutedEventArgs e)
    {
        if (_externalObserving)
        {
            _externalCancellation?.Cancel();
            if (_externalTask != null) await _externalTask;
        }
        // Freeze and detach the old history now; draining disk writes must not hold
        // the disconnected controls in their old state.
        var historyDrain = EndExternalHistoryAsync("用户断开连接");
        _externalPendingMove = null; _externalPendingThought = null; _externalMoveRecovery = null;
        _externalLinked = false; _externalCompleted = false; _externalTracker = null; _externalFrame = null; _externalCalibration = null;
        _externalRequestedTurn = null;
        _game.ExternalAdjudication = false;
        _externalPositionReady = false; ExternalCalibrationText.Text = "已断开，请重新标定";
        ExternalPreviewImage.Source = null; ExternalPreviewImage.IsVisible = false;
        _externalPreview?.Dispose(); _externalPreview = null;
        ExternalStatusText.Text = "已返回本地棋局；本次接管的棋谱和模型说明可继续查看、保存。";
        _enginePaused = true; RefreshGuidanceVisibility(); RefreshUi();
        var engine = _externalPlayingEngine;
        _externalPlayingEngine = null;
        if (engine is not null) await engine.DisposeAsync();
        await historyDrain;
    }
    private void RefreshExternalControls()
    {
        Board.AnimationDurationMs = (_externalObserving || _externalRunning)
            ? Math.Min(80, (int)(AnimationMsBox.Value ?? 230)) : (int)(AnimationMsBox.Value ?? 230);
        RefreshEnginePluginControls();
        Board.LiveSync = _externalLinked;
        ExternalDeliveryBox.IsEnabled = OperatingSystem.IsMacOS() && !_externalRunning && !_externalCalibrating && !_externalResyncing;
        var hasTarget = _externalFrame != null && _externalCalibration != null && _externalDesktop != null;
        var readyToStart = hasTarget && (_externalPositionReady || _externalLinked || _externalObserving || _externalCompleted);
        var syncing = _externalCalibrating || _externalResyncing;
        ExternalStartButton.IsEnabled = ExternalDesktop.Supported && _externalPermissions.Ready && readyToStart && !_externalRunning && !syncing && !_externalStarting && !_enginePluginWork;
        ExternalStartButton.Content = _externalCompleted ? "接管下一局" : _externalRunning ? "接管中"
            : _externalStarting ? "准备接管…" : _externalLinked && !_externalObserving ? "② 继续接管" : "② 开始接管";
        ExternalStartButton.Classes.Set("primary", readyToStart);
        ExternalConnectButton.Classes.Set("primary", !readyToStart);
        ExternalConnectButton.Content = syncing ? "① 同步中…" : _externalLinked || _externalObserving ? "① 已连接" : "① 连接并同步";
        ExternalFlowHint.Text = syncing ? "正在读取并同步棋盘，完成后即可开始接管。"
            : _externalCompleted ? "本局已结束。「接管下一局」会重新同步棋盘，再开始接管。"
            : _externalRunning ? "接管中 · 自动同步并落子；点击「停止」或按 Esc 暂停。"
            : _externalStarting ? "正在核验最新棋盘并准备接管…"
            : _externalObserving ? "已连接，正在实时同步和预计算。点击「② 开始接管」后自动落子。"
            : _externalLinked ? "接管已暂停，棋盘和棋谱已保留。点击「② 继续接管」恢复。"
            : readyToStart ? "棋盘已定位并同步。点击「② 开始接管」后自动落子。"
            : ExternalWindowBox.SelectedItem is ExternalWindow ? "先点击「① 连接并同步」，完成后再点击「② 开始接管」。"
            : "先选择棋盘窗口，再点击「① 连接并同步」。";
        ExternalStopButton.IsEnabled = _externalObserving || _externalRunning || _externalCalibrating;
        ExternalResyncButton.IsVisible = _externalFrame != null;
        ExternalResyncButton.IsEnabled = _externalPermissions.Ready && !_externalCalibrating && !_externalResyncing;
        ExternalStandardOpeningButton.IsEnabled = !_externalCalibrating && !_externalResyncing && !_externalStarting;
        ExternalModelPanel.IsEnabled = !_externalRunning && !_externalCalibrating;
        ExternalDisconnectButton.IsVisible = _externalLinked;
        ExternalDisconnectButton.IsEnabled = true;
        ExternalTurnPanel.IsEnabled = !_externalCalibrating && !_externalResyncing &&
            (!_externalRunning || !_externalTurnKnown || _externalRecoveryMessage != null);
        ExternalTurnOverridePanel.IsVisible = _externalLinked && !_externalTurnKnown && _externalTracker != null;
        ExternalWindowBox.IsEnabled = ExternalRefreshButton.IsEnabled = ExternalDesktop.Supported && _externalPermissions.Ready && !_externalLinked && !_externalCalibrating;
        ExternalConfigPanel.IsEnabled = ExternalDesktop.Supported && _externalPermissions.Ready && !_externalRunning && !_externalCalibrating;
        ExternalConnectButton.IsEnabled = ExternalConfigPanel.IsEnabled && !_externalResyncing && !_externalStarting && !_enginePluginWork &&
            !_externalLinked && !_externalObserving && ExternalWindowBox.SelectedItem is ExternalWindow;
        ExternalOrientationBox.IsEnabled = ExternalFenBox.IsEnabled = ExternalFenActions.IsEnabled = !_externalObserving;
        ExternalConnectionText.Text = _externalCompleted ? "已结束" : _externalCalibrating || _externalResyncing
            ? "同步中" : _externalRecoveryMessage != null ? "待恢复" : _externalRunning ? "接管中"
            : _externalObserving ? "准备就绪" : _externalLinked ? "已暂停" : "未连接";
        ExternalConnectionBadge.Classes.Set("running", _externalRunning && _externalRecoveryMessage == null);
        ExternalConnectionBadge.Classes.Set("connected", !_externalRunning && _externalObserving);
        ExternalConnectionBadge.Classes.Set("waiting", !_externalCompleted && (_externalRecoveryMessage != null ||
            _externalLinked && !_externalObserving && !_externalRunning));
        if (!_externalLinked && !_externalObserving) return;
        TurnBadge.Text = !_externalTurnKnown ? "待确认行棋方" : _game.RedToMove ? "红方走棋" : "黑方走棋";
        foreach (var control in new Control[] { NewButton, NewRecordButton, ImportFenButton, RedEngineCheck, BlackEngineCheck,
            RedLlmCheck, BlackLlmCheck, HintButton, AnalyzeButton, RedActiveModelList, BlackActiveModelList,
            RedLlmReasoningBox, BlackLlmReasoningBox }) control.IsEnabled = false;
        PauseButton.IsVisible = true; PauseButton.Content = "停止外部接管";
        var externalRed = ExternalSideBox.SelectedIndex == 0;
        var externalLabel = ExternalControllerBox.SelectedIndex == 0 ? DefaultEngine.Name + " · 外部" : LlmControllerLabel(externalRed) + " · 外部";
        ActiveEngineText.Text = $"接管{(externalRed ? "红" : "黑")}方 · {externalLabel}";
        ModeBadge.Text = _externalRunning ? _externalRecoveryMessage != null ? "外部接管 · 等待恢复" : "外部接管中" : _externalObserving ? "实时同步 · 预计算" : "外部接管已暂停";
        StatusText.Text = _externalRunning ? _externalRecoveryMessage ?? "正在同步目标窗口 · 按住 Esc 停止"
            : _externalObserving ? "持续跟踪棋盘并预计算 · 点击开始接管才会落子" : "接管已暂停，连接与棋谱已保留，可继续核验";
        if (_externalObserving || _externalRunning)
            UndoButton.IsEnabled = RedoButton.IsEnabled = FirstMoveButton.IsEnabled = PrevMoveButton.IsEnabled = NextMoveButton.IsEnabled = LastMoveButton.IsEnabled = false;
        RefreshThinkingPanelVisibility();
    }
}
