using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PaddiXiangqi.Core;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

[Collection("Desktop integration")]
public class CustomSetupOrientationTests
{
    // Reported custom endgame: black pieces at the top, despite an inherited flipped view.
    internal const string ReportedPieces = "2raka3/9/9/4C1NRp/9/9/9/5p3/3p5/4K4";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    public async Task CustomEndgameUsesPlacedKingsAndPreservesVisibleSquares(
        bool inheritedFlip, bool redAtTop, bool redToMove)
    {
        await ExternalSessionTests.Session.Dispatch(async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "paddi-setup-orientation-" + Guid.NewGuid().ToString("N"));
            var previous = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", Path.Combine(folder, "settings.json"));
            new AppPreferences { AutoAnalyze = false, Flipped = inheritedFlip }.Save();
            MainWindow? window = null;
            try
            {
                window = new MainWindow(); window.Show();
                Click(window, "NewRecordButton"); Click(window, "SetupClearButton");
                window.UpdateLayout();
                var board = window.FindControl<BoardView>("Board")!;
                var editor = window.FindControl<PiecePaletteView>("SetupPalette")!.Editor!;
                var expected = new XiangqiGame();
                var fen = ReportedPieces + (redToMove ? " w" : " b") + " - - 0 1";
                expected.LoadFen(fen);
                for (var rank = 0; rank < 10; rank++)
                for (var file = 0; file < 9; file++)
                {
                    var piece = expected.Board[rank, file];
                    if (piece == '\0') continue;
                    if (piece is 'K' or 'k' && XiangqiGame.FindKing(editor.Board, piece == 'K') is { } king)
                    {
                        editor.SelectMove();
                        var currentFile = board.Flipped ? 8 - king.File : king.File;
                        var currentRank = board.Flipped ? 9 - king.Rank : king.Rank;
                        var kingPoint = board.TranslatePoint(new Point(50 + 55 * currentFile, 52 + 55 * currentRank), window)!.Value;
                        window.MouseDown(kingPoint, MouseButton.Left); window.MouseUp(kingPoint, MouseButton.Left);
                    }
                    else Assert.True(editor.SelectPiece(piece));
                    var screenFile = redAtTop ? 8 - file : file;
                    var screenRank = redAtTop ? 9 - rank : rank;
                    var point = board.TranslatePoint(new Point(50 + 55 * screenFile, 52 + 55 * screenRank), window)!.Value;
                    window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                    var logicalFile = board.Flipped ? 8 - screenFile : screenFile;
                    var logicalRank = board.Flipped ? 9 - screenRank : screenRank;
                    Assert.Equal(piece, board.SetupBoard![logicalRank, logicalFile]);
                }
                window.FindControl<ComboBox>("SetupSideCombo")!.SelectedIndex = redToMove ? 0 : 1;
                var confirm = window.GetVisualDescendants().OfType<Button>()
                    .Single(button => Equals(button.Content, "确定局面，开始记录"));
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(window.FindControl<Border>("SetupPanel")!.IsVisible,
                    window.FindControl<TextBlock>("SetupErrorText")!.Text);
                Assert.Null(board.SetupBoard);
                Assert.Equal(fen, board.Game!.StartFen);
                Assert.Equal(fen, board.Game.CurrentFen());
                Assert.Equal(redAtTop, board.Flipped);
                Assert.Equal(GameResult.Ongoing, board.Game.Result);
                Assert.NotEmpty(board.Game.AllLegalMoves());
                window.Close();
                await (Task)typeof(MainWindow).GetField("_cleanupTask", Private)!.GetValue(window)!;
                window = null;
                Assert.Equal(redAtTop, AppPreferences.Load().Flipped);
            }
            finally
            {
                if (window != null)
                {
                    window.Close();
                    await (Task)typeof(MainWindow).GetField("_cleanupTask", Private)!.GetValue(window)!;
                }
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", previous);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            return true;
        }, default);
    }

    private static void Click(MainWindow window, string name) =>
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
