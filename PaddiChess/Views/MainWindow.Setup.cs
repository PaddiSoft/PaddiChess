using Avalonia.Controls;
using Avalonia.Interactivity;
using PaddiXiangqi.Core;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private PositionEditorState? _setupEditor;

    private void NewCustomRecord_Click(object? sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 0;
        SaveCurrentAnnotation(_game.Ply);
        CancelSearch();
        ClearSelection();
        if (_setupEditor != null) _setupEditor.Changed -= SetupEditorChanged;
        _setupEditor = new((char[,])new XiangqiGame().Board.Clone());
        _setupEditor.Changed += SetupEditorChanged;
        SetupPalette.Attach(_setupEditor);
        SetupTitleBox.Text = "";
        SetupSideCombo.SelectedIndex = 0;
        SetupErrorText.Text = "";
        BoardSubtitle.Text = "摆棋模式 · 选中棋子后点击目标位置移动";
        SetupEditorChanged(this, EventArgs.Empty);
        ResetAnalysisDisplay();
        RefreshGuidanceVisibility();
        RefreshUi();
    }

    private void SetupMoveTool_Click(object? sender, RoutedEventArgs e) => _setupEditor?.SelectMove();
    private void SetupEraseTool_Click(object? sender, RoutedEventArgs e) => _setupEditor?.SelectErase();
    private void SetupUndo_Click(object? sender, RoutedEventArgs e) => _setupEditor?.Undo();
    private void RestorePickedPiece() => _setupEditor?.SelectMove();
    private void HandleSetupClick(Square square) => _setupEditor?.Click(square);

    private void SetupEditorChanged(object? sender, EventArgs e)
    {
        if (_setupEditor is null) return;
        _setupBoard = _setupEditor.Board;
        Board.SetupBoard = _setupBoard;
        Board.Selected = _setupEditor.SelectedSquare;
        SetupSelectedText.Text = PositionEditorText.Hint(_setupEditor);
        SetupMoveButton.Classes.Set("primary", _setupEditor.Tool == PositionEditorTool.Move);
        SetupEraseButton.Classes.Set("primary", _setupEditor.Tool == PositionEditorTool.Erase);
        SetupUndoButton.IsEnabled = _setupEditor.CanUndo;
        SetupErrorText.Text = "";
        Board.Refresh();
    }

    private void SetupStandard_Click(object? sender, RoutedEventArgs e)
        => _setupEditor?.ReplaceBoard((char[,])new XiangqiGame().Board.Clone());

    private void SetupClear_Click(object? sender, RoutedEventArgs e)
    {
        _setupEditor?.Clear();
        SetupErrorText.Text = "请摆入红帅与黑将，再确定局面。";
    }

    private void SetupConfirm_Click(object? sender, RoutedEventArgs e)
    {
        if (_setupBoard is null) return;
        RestorePickedPiece();
        string fen;
        try { fen = PositionSetup.BuildFen(_setupBoard, SetupSideCombo.SelectedIndex != 1); }
        catch (FormatException ex) { SetupErrorText.Text = ex.Message; return; }

        CancelSearch();
        _game.ExternalAdjudication = false;
        _game.LoadFen(fen);
        _engine.RequestNewGame();
        _notes.Clear();
        _scores.Clear(); _scoreLabels.Clear();
        _scores[0] = 0;
        ResetLlmInsights();
        _pendingLlmDrawOffer = null;
        _lastLlmDrawOfferPly = -8;
        _moveListDirty = true;
        RecordTitleBox.Text = string.IsNullOrWhiteSpace(SetupTitleBox.Text) ? "自定义棋谱" : SetupTitleBox.Text.Trim();
        _ready = false;
        RedEngineCheck.IsChecked = BlackEngineCheck.IsChecked = AnalysisModeCheck.IsChecked = false;
        RedLlmCheck.IsChecked = BlackLlmCheck.IsChecked = false;
        _redEngine = _blackEngine = _redLlm = _blackLlm = _analysisMode = false;
        _ready = true;
        _enginePaused = false;
        ExitSetupMode();
        ResetAnalysisDisplay();
        ClearSelection();
        UpdateAnnotationEditor();
        SaveSettings();
        RefreshUi();
        BoardFooter.Text = "自定义初始局面已建立，可以开始记录着法";
        MaybeStartSearch();
    }

    private void SetupCancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_setupBoard is null) return;
        ExitSetupMode();
        RefreshUi();
        MaybeStartSearch();
    }

    private void ExitSetupMode()
    {
        _setupBoard = null;
        if (_setupEditor != null) _setupEditor.Changed -= SetupEditorChanged;
        _setupEditor = null;
        SetupPalette.Attach(null);
        Board.Selected = null;
        Board.SetupBoard = null;
        BoardSubtitle.Text = "点击棋子，再点击目标位置走棋";
        RefreshGuidanceVisibility();
        Board.Refresh();
    }
}
