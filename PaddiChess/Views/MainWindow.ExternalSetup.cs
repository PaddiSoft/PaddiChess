using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private Bitmap? _externalPreview;
    private BoardSkin? _externalSessionSkin;
    private sealed record SkinChoice(string Path, string Name) { public override string ToString() => Name; }
    private static string SkinDirectory => Path.Combine(AppDataPaths.Current.DataDirectory, "BoardSkins");
    private void RefreshSkins()
    {
        var choices = Directory.Exists(SkinDirectory) ? Directory.GetFiles(SkinDirectory, "*.json")
            .Select(path => { try { return new SkinChoice(path, BoardSkin.Load(path).Name); } catch { return null; } })
            .OfType<SkinChoice>().ToArray() : [];
        ExternalSkinBox.ItemsSource = choices;
        ExternalSkinBox.SelectedIndex = choices.Length > 0 ? 0 : -1;
    }
    private void SetExternalPreview(ExternalFrame frame)
    {
        var bitmap = new Bitmap(new MemoryStream(frame.Png));
        ExternalPreviewImage.Source = bitmap; ExternalPreviewImage.IsVisible = true;
        _externalPreview?.Dispose(); _externalPreview = bitmap;
    }
    private async void ExternalLearnSkin_Click(object? sender, RoutedEventArgs e)
    {
        if (_externalRunning || _externalCalibrating || _externalFrame == null || _externalCalibration == null) return;
        var wasObserving = _externalObserving;
        _externalCalibrating = true;
        RefreshExternalControls();
        try
        {
            await PauseExternalObservationForSetupAsync();
            var game = new XiangqiGame(); game.LoadFen(ExternalFenBox.Text ?? "");
            var geometry = _externalCalibration with { RedAtTop = ExternalOrientationBox.SelectedIndex == 1 };
            var name = (ExternalSkinNameBox.Text ?? "默认棋盘").Trim();
            if (name.Length == 0) throw new InvalidOperationException("请输入样式名称");
            var frame = _externalFrame;
            var skin = await Task.Run(() => BoardSkin.Learn(name, BoardObservation.Read(frame, geometry), game));
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..20];
            var path = Path.Combine(SkinDirectory, hash + ".json");
            await Task.Run(() => skin.Save(path)); RefreshSkins();
            ExternalSkinBox.SelectedItem = ((IEnumerable<SkinChoice>)ExternalSkinBox.ItemsSource!).First(c => c.Path == path);
            ExternalStatusText.Text = $"已保存「{name}」的红黑七种棋子样式，可识别同款棋盘中局。";
        }
        catch (Exception ex) { ExternalStatusText.Text = ex.Message; }
        finally { _externalCalibrating = false; ResumeExternalObservationAfterSetup(wasObserving); RefreshExternalControls(); }
    }
    private async void ExternalRecognize_Click(object? sender, RoutedEventArgs e)
    {
        if (_externalRunning || _externalCalibrating) return;
        if (_externalFrame == null || _externalCalibration == null || _externalDesktop == null)
        { ExternalStatusText.Text = "请先定位棋盘。"; return; }
        var wasObserving = _externalObserving;
        _externalCalibrating = true;
        using var setupCancellation = new CancellationTokenSource(); _externalSetupCancellation = setupCancellation;
        RefreshExternalControls();
        try
        {
            await PauseExternalObservationForSetupAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(setupCancellation.Token); timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var frame = await _externalDesktop.CaptureAsync(_externalFrame.Window, timeout.Token);
            if (!ExternalCaptureGeometry.TryRebaseCalibration(_externalFrame, frame, _externalCalibration, out var geometry))
                throw new InvalidOperationException("窗口尺寸变化，请先重新定位棋盘。");
            _externalFrame = frame; _externalCalibration = geometry; SetExternalPreview(frame); _externalPositionReady = false;
            await RecognizeExternalFrameAsync(frame);
        }
        catch (Exception ex) { ExternalStatusText.Text = ex.Message; }
        finally { _externalSetupCancellation = null; _externalCalibrating = false; ResumeExternalObservationAfterSetup(wasObserving); RefreshExternalControls(); }
    }
    private async Task<bool> RecognizeExternalFrameAsync(ExternalFrame frame)
    {
        using var recognitionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_externalPermissionLifetime.Token,
            _externalSetupCancellation?.Token ?? CancellationToken.None);
        var ct = recognitionCancellation.Token;
        _externalPositionReady = false;
        var geometry = _externalCalibration! with { RedAtTop = ExternalOrientationBox.SelectedIndex == 1 };
        // Unknown is a display/authority state, not a request to flip the FEN.
        // Keep its last imported placeholder until position/clock/move evidence confirms a side.
        var fenTurn = (ExternalFenBox.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
        bool redToMove = ExternalTurnBox.SelectedIndex switch { 0 => true, 1 => false, _ => fenTurn != "b" };
        ExternalStatusText.Text = "正在识别当前棋子…";
        (frame, geometry) = await ReadSettledExternalFrameAsync(frame, geometry, ct);
        ct.ThrowIfCancellationRequested();
        _externalFrame = frame; _externalCalibration = geometry;
        if (_externalLinked && _externalTracker != null)
        {
            _game.GoToPly(_game.TotalPly);
            var observation = await Task.Run(() => BoardObservation.Read(frame, geometry), ct);
            var match = await Task.Run(() => MatchExternalObservation(observation, _externalPendingMove), ct);
            ct.ThrowIfCancellationRequested();
            if (match.Recognized)
            {
                await AcceptExternalMatchAsync(match, observation, null, ct);
                if (match.Moves.Count == 0)
                    await ApplyRequestedExternalTurnAsync(observation, ct);
                _externalRequestedTurn = null;
                ExternalFenBox.Text = _game.CurrentFen();
                SetExternalTurn(_externalTurnKnown ? _game.RedToMove : null);
                _externalPositionReady = true;
                Board.Flipped = geometry.RedAtTop; Board.Refresh(); RefreshUi();
                ExternalStatusText.Text = _externalTurnKnown
                    ? $"已同步 {_game.Ply} 手 · 当前轮到{(_game.RedToMove ? "红" : "黑")}方"
                    : "棋盘已核对；行棋方仍待确认，将观察下一步落子，也可手动指定。";
                return true;
            }
            if (_externalPendingMove == null && _externalMoveRecovery is { } checkpoint)
            {
                var skins = _externalSessionSkin == null ? BuiltInBoardSkins.All : new[]{_externalSessionSkin}.Concat(BuiltInBoardSkins.All);
                var corrected = await Task.Run(() => checkpoint.Match(_game, observation, skins), ct);
                if (corrected.Recognized)
                {
                    ct.ThrowIfCancellationRequested();
                    await CorrectExternalMatchAsync(checkpoint, corrected, observation, null);
                    _externalPositionReady = true;
                    ExternalStatusText.Text = "已自动校正对方的最终落点，原棋谱与轮次已保留。";
                    return true;
                }
            }
        }
        var result = await ReadExternalSkinAsync(frame,geometry,redToMove,ct,detectOrientation: !_externalLinked);
        ct.ThrowIfCancellationRequested();
        if (result?.DetectedRedAtTop is { } direction)
        {
            geometry = geometry with { RedAtTop = direction }; _externalCalibration = geometry;
            ExternalOrientationBox.SelectedIndex = direction ? 1 : 0;
        }
        if (result is { Confident: true })
        {
            // Re-reading the same position must not erase move history or an outstanding input.
            if (_externalLinked && _externalTracker != null &&
                ExternalPositionRecovery.SamePieces(result.Fen, _game.CurrentFen()))
            {
                var observation = await Task.Run(() => BoardObservation.Read(frame, geometry),ct);
                ct.ThrowIfCancellationRequested();
                await ApplyRequestedExternalTurnAsync(observation, ct);
                _externalTracker.Accept(observation, _game);
                ExternalFenBox.Text = _game.CurrentFen();
                SetExternalTurn(_externalTurnKnown ? _game.RedToMove : null);
                _externalPositionReady = true;
            }
            else if (_externalLinked)
            {
                // A disconnected/new game has no reliable turn information in the board image.
                // Never silently reuse the old turn after replacing an unrelated position.
                await EditExternalPositionAsync(result.Fen,
                    "棋子已识别，但与原棋谱无法连上。请在“当前轮到”选择行棋方后采用局面。");
            }
            else
            {
                InferExternalTurn(result.Fen);
                var fields = result.Fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (_externalTurnKnown) fields[1] = ExternalTurnBox.SelectedIndex == 0 ? "w" : "b";
                await ApplyExternalPositionAsync(string.Join(' ', fields), ct);
            }
            if (!_externalLinked) ExternalStatusText.Text = _externalTurnKnown
                ? $"局面已同步 · 当前轮到{(_game.RedToMove ? "红" : "黑")}方 · 默认接管棋盘下方。"
                : "局面已同步；正在识别计时器。若仍无法判断，将观察下一步落子后自动确认轮次。";
        }
        else await EditExternalPositionAsync(result?.Fen ?? ExternalFenBox.Text ?? XiangqiGame.InitialFen,
            result == null ? "未能确认完整局面，请检查网格或在此修正棋子。" : $"有 {result.Uncertain.Count} 个位置需要核对。{result.Problem} 修正后即可同步。", result?.Uncertain);
        ct.ThrowIfCancellationRequested();
        return _externalPositionReady;
    }
    private async Task ApplyRequestedExternalTurnAsync(BoardObservation observation, CancellationToken ct)
    {
        if (_externalRequestedTurn is not { } chosen) return;
        if (chosen != _game.RedToMove)
        {
            var fields = _game.CurrentFen().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            fields[1] = chosen ? "w" : "b";
            await ApplyExternalPositionAsync(string.Join(' ', fields), ct);
            _externalTracker = new ExternalBoardTracker(observation, _game);
            _externalLinked = true;
        }
        _externalRequestedTurn = null;
        _externalTurnKnown = true;
    }
    private async Task ApplyExternalPositionAsync(string fen, CancellationToken ct)
    {
        var validated = new XiangqiGame(); validated.LoadFen(fen); PositionSetup.Validate(validated.Board);
        var frame = _externalFrame;
        var geometry = _externalCalibration is { } calibration
            ? calibration with { RedAtTop = ExternalOrientationBox.SelectedIndex == 1 } : null;
        var observation = frame != null && geometry != null
            ? await Task.Run(() => BoardObservation.Read(frame, geometry), ct) : null;
        ct.ThrowIfCancellationRequested();
        if (_externalLinked && _externalTracker != null && _externalFrame != null && _externalCalibration != null &&
            validated.RedToMove == _game.RedToMove && ExternalPositionRecovery.SamePieces(fen,_game.CurrentFen()))
        {
            // Correcting geometry or appearance is not starting a new game. Keep the actual
            // history and any outstanding move; only replace the visual reference.
            _externalTracker.Accept(observation!, _game);
            ExternalFenBox.Text = _game.CurrentFen(); _externalPositionReady = true;
            Board.Flipped = ExternalOrientationBox.SelectedIndex == 1; Board.Refresh(); RefreshUi();
            return;
        }
        // Keep a recoverable record before replacing a session with a newly recognized position.
        if (_game.TotalPly > 0 || _game.StartFen != XiangqiGame.InitialFen)
        {
            SaveCurrentAnnotation(_game.Ply);
            var record = new GameRecord { Title = RecordTitleBox.Text ?? "重新同步前棋谱", StartFen = _game.StartFen,
                Moves = _game.History.Select(m => m.Uci).ToList(), Notes = new(_notes), Scores = new(_scores), ScoreLabels = new(_scoreLabels),
                LlmThoughts = ExportLlmThoughts(), CurrentPly = _game.Ply, AgreedDrawPly = _game.AgreedDrawPly,
                ExternalAdjudication = _game.ExternalAdjudication };
            await GameRecordStorage.ArchiveRecoveryAsync(record,
                Path.Combine(Path.GetDirectoryName(SkinDirectory)!, "Recovery"), ct);
        }
        BoardSkin? learned = null;
        if (observation != null)
            try { learned = await Task.Run(() => BoardSkin.Learn("已核对当前棋盘", observation, validated, requireAllPieces: false), ct); }
            catch (InvalidOperationException) { }
        ct.ThrowIfCancellationRequested();
        await EndExternalHistoryAsync("重新同步：采用新的局面，已保存此前棋谱与识别记录。");
        CancelSearch(); StopLlmInsightWorker(); _enginePaused = true;
        _externalLinked = false; _externalTracker = null; _externalPendingMove = null; _externalPendingThought = null; _externalMoveRecovery = null;
        _game.ExternalAdjudication = true;
        _game.LoadFen(fen); _notes.Clear(); _scores.Clear(); _scoreLabels.Clear();
        ResetLlmInsights(); ResetLlmThinking(); _pendingLlmDrawOffer = null; _lastLlmDrawOfferPly = -8;
        _moveListDirty = true; ClearSelection(); ResetAnalysisDisplay(); UpdateAnnotationEditor();
        ExternalFenBox.Text = fen; SetExternalTurn(_externalTurnKnown ? _game.RedToMove : null); _externalPositionReady = true;
        Board.Flipped = ExternalOrientationBox.SelectedIndex == 1; Board.Refresh(); RefreshUi();
        _externalSessionSkin = learned;
    }
    private async void ExternalEditPosition_Click(object? sender, RoutedEventArgs e)
    {
        if (_externalRunning || _externalCalibrating) return;
        var wasObserving = _externalObserving;
        _externalCalibrating = true; RefreshExternalControls();
        try
        {
            await PauseExternalObservationForSetupAsync();
            await EditExternalPositionAsync(ExternalFenBox.Text ?? XiangqiGame.InitialFen, "选择下方棋子，再点击棋盘摆放；橡皮擦用于清除。");
        }
        finally { _externalCalibrating = false; ResumeExternalObservationAfterSetup(wasObserving); RefreshExternalControls(); }
    }

    private async void ExternalStandardOpening_Click(object? sender, RoutedEventArgs e)
    {
        if (_externalCalibrating || _externalResyncing || _externalStarting || _closing) return;
        var wasObserving = _externalObserving;
        _externalRunning = false;
        _externalCalibrating = true;
        RefreshExternalControls();
        try
        {
            await PauseExternalObservationForSetupAsync();
            await ShowExternalPositionEditorAsync(XiangqiGame.InitialFen,
                "已按当前朝向生成标准开局 32 子，红方先行。可对照原图手动调整，完成后采用局面。",
                uncertain: null, standardOpening: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ExternalStatusText.Text = ex.Message; }
        finally
        {
            _externalCalibrating = false;
            ResumeExternalObservationAfterSetup(wasObserving);
            RefreshExternalControls();
        }
    }
}
