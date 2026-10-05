using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using PaddiXiangqi.Services;
using PaddiXiangqi.ViewModels;

namespace PaddiXiangqi.Tests;

public class PositionEditorStateTests
{
    [Theory]
    [InlineData('R', 2)] [InlineData('N', 2)] [InlineData('B', 2)] [InlineData('A', 2)]
    [InlineData('K', 1)] [InlineData('C', 2)] [InlineData('P', 5)]
    [InlineData('r', 2)] [InlineData('n', 2)] [InlineData('b', 2)] [InlineData('a', 2)]
    [InlineData('k', 1)] [InlineData('c', 2)] [InlineData('p', 5)]
    public void PalettePlacesContinuouslyUntilExactLimitThenReturnsToMove(char piece, int limit)
    {
        var editor = new PositionEditorState(new char[10, 9]);
        Assert.True(editor.SelectPiece(piece));
        for (var i = 0; i < limit; i++)
        {
            Assert.Equal(PositionEditorTool.Place, editor.Tool);
            editor.Click(new(i, 4));
            Assert.Equal(i + 1, PositionSetup.CountPieces(editor.Board, piece));
        }
        Assert.Equal(PositionEditorTool.Move, editor.Tool);
        Assert.False(editor.CanAdd(piece)); Assert.False(editor.SelectPiece(piece));
        editor.Click(new(8, 5));
        Assert.Equal(limit, PositionSetup.CountPieces(editor.Board, piece));
    }

    [Fact]
    public void SelectingKeepsPieceVisibleAndMoveReplacementReleasesTheCapturedPaletteSlot()
    {
        var editor = new PositionEditorState((char[,])new XiangqiGame().Board.Clone());
        editor.Click(new(0, 9));
        Assert.Equal('R', editor.Board[9, 0]);
        Assert.Equal(new Square(0, 9), editor.SelectedSquare);
        Assert.False(editor.CanAdd('R'));
        editor.Click(new(1, 9));
        Assert.Equal('\0', editor.Board[9, 0]); Assert.Equal('R', editor.Board[9, 1]);
        Assert.Null(editor.SelectedSquare); Assert.True(editor.CanAdd('N')); Assert.False(editor.CanAdd('R'));
        editor.Click(new(1, 9)); editor.SelectMove();
        Assert.Equal('R', editor.Board[9, 1]); // Cancelling selection never removes a piece.
    }

    [Fact]
    public void RecognizedExcessCanBeMovedOrErasedButCannotBeIncreased()
    {
        var board = new char[10, 9]; board[0, 0] = board[0, 1] = board[0, 2] = 'r';
        var editor = new PositionEditorState(board);
        Assert.False(editor.SelectPiece('r'));
        editor.Click(new(0, 0)); editor.Click(new(0, 2));
        Assert.Equal(3, PositionSetup.CountPieces(board, 'r'));
        editor.SelectErase(); editor.Click(new(1, 0));
        Assert.Equal(2, PositionSetup.CountPieces(board, 'r')); Assert.False(editor.CanAdd('r'));
        editor.Click(new(2, 0)); Assert.True(editor.CanAdd('r'));
        editor.Clear(); Assert.Equal(0, PositionSetup.CountPieces(board, 'r'));
    }

    [Fact]
    public void ClickingExistingPieceWhilePlacingSelectsItInsteadOfReplacingOrErasingIt()
    {
        var board = new char[10, 9]; board[4, 4] = 'n';
        var editor = new PositionEditorState(board);
        editor.SelectPiece('P'); editor.Click(new(4, 4));
        Assert.Equal('n', board[4, 4]); Assert.Equal(0, PositionSetup.CountPieces(board, 'P'));
        Assert.Equal(PositionEditorTool.Move, editor.Tool);
        editor.Click(new(4, 4)); Assert.Null(editor.SelectedSquare); Assert.Equal('n', board[4, 4]);
    }

    [Fact]
    public void UndoRestoresClearEraseReplacementAndPaletteCountsWithoutRecordingSelectionOnly()
    {
        var board = (char[,])new XiangqiGame().Board.Clone();
        var editor = new PositionEditorState(board);
        editor.Click(new(0, 9)); Assert.False(editor.CanUndo);
        editor.Click(new(1, 9)); Assert.True(editor.CanUndo); Assert.True(editor.CanAdd('N'));
        editor.Undo(); Assert.False(editor.CanUndo); Assert.False(editor.CanAdd('N'));
        Assert.Equal('R', board[9, 0]); Assert.Equal('N', board[9, 1]);
        editor.SelectErase(); editor.Click(new(0, 9)); editor.Undo(); Assert.Equal('R', board[9, 0]);
        editor.Clear(); Assert.Equal(0, PositionSetup.CountPieces(board, 'P'));
        editor.Undo(); Assert.Equal(5, PositionSetup.CountPieces(board, 'P'));
        Assert.Equal(XiangqiGame.InitialFen, PositionSetup.BuildFen(board, true));
        Assert.Null(editor.SelectedSquare);
    }

    [Fact]
    public void UndoHistoryIsBoundedAndReRecognitionStartsASeparateEditHistory()
    {
        var board = new char[10, 9]; board[0, 0] = 'r';
        var editor = new PositionEditorState(board);
        for (var i = 0; i < 70; i++)
        { editor.Click(new(i % 2, 0)); editor.Click(new(1 - i % 2, 0)); }
        var undos = 0;
        while (editor.CanUndo) { editor.Undo(); undos++; }
        Assert.Equal(64, undos);
        editor.Clear(); Assert.True(editor.CanUndo);
        editor.ReplaceBoard(new char[10, 9]); Assert.False(editor.CanUndo);
    }
}

[Collection("Desktop integration")]
public class PositionEditorInteractionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(860, 700, false)]
    [InlineData(1160, 880, true)]
    [InlineData(1160, 880, false)]
    public async Task ExternalEditorHasVisibleGraphicalToolsAndClicksMoveWithoutDeleting(int width, int height, bool flipped)
    {
        await ExternalSessionTests.Session.Dispatch(async () =>
        {
            var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "playxiangqi-opening.png"));
            var sourceFrame = new ExternalFrame(new(500, 500, "棋盘截图", 0, 0, 910, 1010), png);
            var context = new ExternalPositionEditorContext(XiangqiGame.InitialFen, "校对局面", null, sourceFrame,
                new(55, 55, 855, 955, flipped), flipped, 0);
            var window = new ExternalPositionEditorWindow(context, null, (_, _, _, _, _) => Task.FromResult<SkinRecognition?>(null), default)
                { Width = width, Height = height };
            try
            {
                window.Show(); window.UpdateLayout();
                var palette = window.FindControl<PiecePaletteView>("ExternalEditorPalette")!;
                var buttons = PaletteButtons(palette);
                Assert.Equal(14, buttons.Length);
                Assert.All(buttons, button =>
                {
                    Assert.False(button.IsEnabled); Assert.Single(button.GetVisualDescendants().OfType<PieceIcon>());
                    Contained(button, window);
                });
                foreach (var name in new[] { "ExternalEditorMove", "ExternalEditorErase", "ExternalEditorUndo", "ExternalEditorClear", "ExternalEditorAccept", "ExternalEditorTurn" })
                    Contained(window.FindControl<Control>(name)!, window);
                var accept = window.FindControl<Button>("ExternalEditorAccept")!;
                Assert.True(accept.IsEnabled);
                if (!flipped) SaveEditorScreenshot(window, $"position-editor-opening-{width}");
                var board = window.FindControl<BoardView>("ExternalEditorBoard")!;
                ClickSquare(window, board, new(0, 9));
                Assert.Equal('R', board.SetupBoard![9, 0]); Assert.Equal(new Square(0, 9), board.Selected);
                ClickSquare(window, board, new(0, 8));
                Assert.Equal('\0', board.SetupBoard[9, 0]); Assert.Equal('R', board.SetupBoard[8, 0]);
                window.FindControl<Button>("ExternalEditorClear")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.All(buttons, button => Assert.True(button.IsEnabled));
                Assert.False(accept.IsEnabled);
                Assert.Contains("红帅和黑将", window.FindControl<TextBlock>("ExternalEditorValidation")!.Text);
                Assert.StartsWith("校对局面", window.FindControl<TextBlock>("ExternalEditorStatus")!.Text);
                window.FindControl<Button>("ExternalEditorUndo")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(accept.IsEnabled);
                window.FindControl<Button>("ExternalEditorClear")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(accept.IsEnabled);
                var pawn = buttons.Single(b => ((PiecePaletteItemViewModel)b.DataContext!).Piece == 'P');
                pawn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ClickSquare(window, board, new(0, 6)); ClickSquare(window, board, new(2, 6));
                Assert.Equal(2, PositionSetup.CountPieces(board.SetupBoard, 'P'));
                Assert.False(accept.IsEnabled);
                Assert.Equal(PositionEditorTool.Place, window.Editor.Tool);
                window.FindControl<Button>("ExternalEditorErase")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ClickSquare(window, board, new(0, 6)); Assert.Equal(1, PositionSetup.CountPieces(board.SetupBoard, 'P'));
                window.FindControl<Button>("ExternalEditorUndo")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2, PositionSetup.CountPieces(board.SetupBoard, 'P'));
                SaveEditorScreenshot(window, $"position-editor-{width}");
            }
            finally { window.Close(); }
            await Task.CompletedTask;
            return true;
        }, default);
    }

    [Fact]
    public async Task ValidationChangesDoNotEnableAcceptWhileRecognitionIsBusy()
    {
        await ExternalSessionTests.Session.Dispatch(async () =>
        {
            var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "playxiangqi-opening.png"));
            var frame = new ExternalFrame(new(500, 500, "棋盘截图", 0, 0, 910, 1010), png);
            var response = new TaskCompletionSource<SkinRecognition?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var window = new ExternalPositionEditorWindow(new(XiangqiGame.InitialFen, "校对局面", null, frame,
                new(55, 55, 855, 955, false), false, 0), null, (_, _, _, _, _) => response.Task, default);
            try
            {
                window.Show();
                var accept = window.FindControl<Button>("ExternalEditorAccept")!;
                Assert.True(accept.IsEnabled);
                window.FindControl<Button>("ExternalEditorRefresh")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(accept.IsEnabled);
                window.Editor.Clear(); window.Editor.Undo(); Assert.False(accept.IsEnabled);
                response.SetResult(null);
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (!accept.IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(15);
                Assert.True(accept.IsEnabled);
                var excess = (char[,])window.Editor.Board.Clone(); excess[8, 0] = 'R';
                window.Editor.ReplaceBoard(excess);
                Assert.False(accept.IsEnabled);
                Assert.Contains("超过允许数量", window.FindControl<TextBlock>("ExternalEditorValidation")!.Text);
                window.Editor.SelectErase(); window.Editor.Click(new(0, 8)); Assert.True(accept.IsEnabled);
            }
            finally { response.TrySetResult(null); window.Close(); }
            return true;
        }, default);
    }

    [Fact]
    public async Task CustomSetupUsesSameQuotaAndSelectionRulesAsExternalEditor()
    {
        await ExternalSessionTests.Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-editor-palette-{Guid.NewGuid():N}.json");
            var previous = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            new AppPreferences { AutoAnalyze = false }.Save();
            MainWindow? window = null;
            try
            {
                window = new MainWindow(); window.Show();
                window.FindControl<Button>("NewRecordButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                var palette = window.FindControl<PiecePaletteView>("SetupPalette")!;
                Assert.NotNull(palette.Editor);
                Assert.All(PaletteButtons(palette), button => Assert.False(button.IsEnabled));
                var board = window.FindControl<BoardView>("Board")!;
                ClickSquare(window, board, new(0, 9)); Assert.Equal('R', board.SetupBoard![9, 0]);
                ClickSquare(window, board, new(0, 8)); Assert.Equal('R', board.SetupBoard[8, 0]);
                window.FindControl<Button>("SetupClearButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.All(PaletteButtons(palette), button => Assert.True(button.IsEnabled));
                Assert.True(palette.Editor!.SelectPiece('K'));
                ClickSquare(window, board, new(4, 9));
                Assert.False(palette.Editor.CanAdd('K'));
                Assert.Equal(PositionEditorTool.Move, palette.Editor.Tool);
            }
            finally
            {
                if (window != null) { window.Close(); await (Task)typeof(MainWindow).GetField("_cleanupTask", Private)!.GetValue(window)!; }
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", previous);
                if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, default);
    }

    private static Button[] PaletteButtons(PiecePaletteView palette) => palette.GetVisualDescendants().OfType<Button>()
        .Where(b => b.DataContext is PiecePaletteItemViewModel).ToArray();

    private static void SaveEditorScreenshot(Window window, string name)
    {
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/ui"));
        Directory.CreateDirectory(folder); window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(folder, name + ".png"));
    }

    private static void ClickSquare(Window window, BoardView board, Square square)
    {
        var file = board.Flipped ? 8 - square.File : square.File;
        var rank = board.Flipped ? 9 - square.Rank : square.Rank;
        var point = board.TranslatePoint(new Point(50 + 55 * file, 52 + 55 * rank), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
    }

    private static void Contained(Control control, Window window)
    {
        var point = control.TranslatePoint(default, window)!.Value;
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0);
        Assert.True(point.X >= 0 && point.Y >= 0 && point.X + control.Bounds.Width <= window.ClientSize.Width + 1
            && point.Y + control.Bounds.Height <= window.ClientSize.Height + 1, $"{control.Name} 超出窗口");
    }
}
