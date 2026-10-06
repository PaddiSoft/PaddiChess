using System.Collections;
using System.Diagnostics;
using Avalonia.Controls;
using PaddiXiangqi.Core;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    [Fact]
    public async Task ImmediateReplyKeepsBothConfirmedBoardsForPlayback()
    {
        await Session.Dispatch(() =>
        {
            var game = new XiangqiGame();
            var board = new BoardView { Game = game, AnimationDurationMs = 1000 };
            Assert.True(game.TryMoveUci("h2e2", out var red));
            var afterRed = game.Board;
            board.Animate(red);
            Assert.True(game.TryMoveUci("h9g7", out var black));
            var afterBlack = game.Board;
            board.Animate(black);

            // Game logic already knows the reply; presentation still retains red's turn.
            Assert.Equal(2, game.Ply);
            Assert.Same(afterRed, AnimationBoard(board));
            Assert.Equal('n', AnimationBoard(board)![0, 7]);
            Assert.Equal('\0', game.Board[0, 7]);
            var queue = (ICollection)typeof(BoardView).GetField("_animationQueue", Private)!.GetValue(board)!;
            Assert.Single(queue);
            var slice = (double)typeof(BoardView).GetField("_queuedAnimationMs", Private)!.GetValue(board)!;
            Assert.InRange(slice * 2, 1, 350);

            FinishAnimationFrame(board);
            Assert.Same(afterBlack, AnimationBoard(board));
            Assert.Equal('n', AnimationBoard(board)![2, 6]);
            FinishAnimationFrame(board);
            Assert.Null(AnimationBoard(board));
            return true;
        }, default);
    }

    [Fact]
    public async Task NavigatingOrDisablingAnimationsCancelsQueuedMoves()
    {
        await Session.Dispatch(() =>
        {
            var game = new XiangqiGame();
            var board = new BoardView { Game = game };
            Assert.True(game.TryMoveUci("h2e2", out var move)); board.Animate(move);
            Assert.True(game.TryMoveUci("h9g7", out move)); board.Animate(move);
            game.GoToPly(0);
            typeof(BoardView).GetMethod("AdvanceAnimation", Private)!.Invoke(board, null);
            Assert.Null(AnimationBoard(board));
            game.GoToPly(1); board.Animate(game.LastMove!.Value);
            board.AnimationDurationMs = 0;
            Assert.Null(AnimationBoard(board));
            game.GoToPly(2); board.Animate(game.LastMove!.Value);
            Assert.Null(AnimationBoard(board));
            board.AnimationDurationMs = 230;
            board.Animate(game.LastMove!.Value);
            game.NewGame();
            typeof(BoardView).GetMethod("AdvanceAnimation", Private)!.Invoke(board, null);
            Assert.Null(AnimationBoard(board));
            return true;
        }, default);
    }

    [Fact]
    public async Task AppendingAndReviewingMovesReusesExistingHistoryControls()
    {
        await Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-history-reuse-{Guid.NewGuid():N}.json");
            var previousPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            var window = new MainWindow();
            try
            {
                window.Show(); window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked = false;
                var game = Get<XiangqiGame>(window, "_game");
                var model = (PaddiXiangqi.ViewModels.MoveHistoryViewModel)window.FindControl<MoveHistoryView>("MoveScroll")!.DataContext!;
                var start = model.Rows[0];
                Assert.True(game.TryMoveUci("h2e2", out var red));
                typeof(MainWindow).GetMethod("CommitMove", Private)!.Invoke(window, [red]);
                var row = model.Rows[1]; var redEntry = row.Red;
                Assert.True(game.TryMoveUci("h9g7", out var black));
                typeof(MainWindow).GetMethod("CommitMove", Private)!.Invoke(window, [black]);
                Assert.Same(start, model.Rows[0]); Assert.Same(row, model.Rows[1]);
                Assert.Same(redEntry, row.Red); Assert.NotNull(row.Black);

                redEntry!.Select.Execute(null);
                Assert.Same(row, model.Rows[1]); Assert.True(redEntry.IsSelected);
                Assert.Equal(1, game.Ply);
                var notes = Get<Dictionary<int, string>>(window, "_notes"); notes[1] = "炮走中路";
                Set(window, "_moveListDirty", true);
                typeof(MainWindow).GetMethod("RefreshUi", Private)!.Invoke(window, [false]);
                Assert.Same(redEntry, model.Rows[1].Red); Assert.Contains("✎", redEntry.Label);
                Assert.True(game.TryMoveUci("b9c7", out var branch));
                typeof(MainWindow).GetMethod("CommitMove", Private)!.Invoke(window, [branch]);
                Assert.Contains(branch.Notation, model.Rows[1].Black!.Label);
            }
            finally
            {
                window.Close(); await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", previousPath);
                if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, default);
    }

    private static char[,]? AnimationBoard(BoardView board)
    {
        var frame = typeof(BoardView).GetField("_animation", Private)!.GetValue(board);
        return (char[,]?)frame?.GetType().GetProperty("Board")!.GetValue(frame);
    }

    private static void FinishAnimationFrame(BoardView board)
    {
        typeof(BoardView).GetField("_animationStart", Private)!.SetValue(board,
            Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2);
        typeof(BoardView).GetMethod("AdvanceAnimation", Private)!.Invoke(board, null);
    }
}
