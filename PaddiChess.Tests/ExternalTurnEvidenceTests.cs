using Avalonia.Controls;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;
using PaddiXiangqi.External;
using PaddiXiangqi.Services;
using System.Text.Json;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UnchangedBoardConfirmationPreservesWhetherTheTurnIsKnown(bool turnKnown, bool rerecognize)
    {
        await WithTurnEvidenceAsync(async (window, game, frame, observed) =>
        {
            Set(window, "_externalTurnKnown", turnKnown);
            var before = game.CurrentFen();
            if (rerecognize)
                Assert.True(await (Task<bool>)typeof(MainWindow).GetMethod("RecognizeExternalFrameAsync", Private)!.Invoke(window, [frame])!);
            else
                await (Task)typeof(MainWindow).GetMethod("AcceptExternalMatchAsync", Private)!.Invoke(window,
                    [new BoardMatch(true, [], 0, "等待落子"), observed, null, CancellationToken.None])!;
            Assert.Equal(turnKnown, Get<bool>(window, "_externalTurnKnown"));
            Assert.Equal(turnKnown ? 1 : -1, window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
            Assert.Equal(before, game.CurrentFen());
            Assert.Equal(0, game.Ply);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitTurnChoiceDuringRerecognitionStillConfirmsTheSelectedSide(bool red)
    {
        await WithTurnEvidenceAsync(async (window, game, frame, _) =>
        {
            Set(window, "_externalRequestedTurn", red);
            Assert.True(await (Task<bool>)typeof(MainWindow).GetMethod("RecognizeExternalFrameAsync", Private)!.Invoke(window, [frame])!);
            Assert.True(Get<bool>(window, "_externalTurnKnown"));
            Assert.Equal(red, game.RedToMove);
            Assert.Equal(red ? 0 : 1, window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
            Assert.Equal(0, game.Ply);
        });
    }

    [Fact]
    public async Task ConfirmedAndCorrectedCaptureHistoryPreservesBothBeforeAndAfterPositions()
    {
        await WithTurnEvidenceAsync(async (window, game, _, _) =>
        {
            var beforeFen = game.CurrentFen();
            await (Task)typeof(MainWindow).GetMethod("BeginExternalHistoryAsync", Private)!.Invoke(window, ["isolated history"] )!;
            var history = Get<ExternalHistoryStore>(window, "_externalHistory");
            var wrong = JjRookCaptureTests.BeforeGame();
            Assert.True(wrong.TryMoveUci("a6a5", out _));
            var wrongFrame = BoardObservation.Read(ExternalBoardTests.Render(wrong), new(40, 40, 440, 490, false));
            Set(window, "_externalPendingMove", "a6a5");
            Set(window, "_externalPendingThought", new LlmMoveResult("a6a5", "fixture model explanation"));
            await (Task)typeof(MainWindow).GetMethod("AcceptExternalMatchAsync", Private)!.Invoke(window,
                [new BoardMatch(true, ["a6a5"], 0, "synthetic animation waypoint"), wrongFrame, null, CancellationToken.None])!;
            await history.FlushAsync();
            var firstSnapshot = JsonSerializer.Deserialize<GameRecord>(await File.ReadAllTextAsync(history.RecordPath))!;
            Assert.Equal("fixture model explanation", Assert.Single(firstSnapshot.LlmThoughts[1]).Explanation);
            var wrongFen = game.CurrentFen();
            var recovery = Get<ExternalMoveRecovery>(window, "_externalMoveRecovery");
            var correct = JjRookCaptureTests.BeforeGame();
            Assert.True(correct.TryMoveUci("a6b6", out _));
            var correctFrame = BoardObservation.Read(ExternalBoardTests.Render(correct), new(40, 40, 440, 490, false));
            await (Task)typeof(MainWindow).GetMethod("CorrectExternalMatchAsync", Private)!.Invoke(window,
                [recovery, new BoardMatch(true, ["a6b6"], 0, "correct final capture"), correctFrame, null])!;
            await history.FlushAsync();
            var events = await ExternalHistoryStore.ReadEventsAsync(history.EventsPath);
            Assert.Equal(new[] { "started", "confirmed", "correction", "confirmed", "corrected" }, events.Select(entry => entry.Kind));
            Assert.Equal(beforeFen, events[1].BeforeFen);
            Assert.Equal("a6a5", events[1].Move);
            Assert.Equal(wrongFen, events[1].Fen);
            Assert.Equal(wrongFen, events[2].BeforeFen);
            Assert.Equal(wrongFen, events[2].Fen);
            Assert.Equal(beforeFen, events[3].BeforeFen);
            Assert.Equal("a6b6", events[3].Move);
            Assert.Equal(game.CurrentFen(), events[3].Fen);
            Assert.Equal(wrongFen, events[4].BeforeFen);
            Assert.Equal(correct.CurrentFen(), events[4].Fen);
            Assert.Equal(new[] { "a6b6" }, game.History.Select(move => move.Uci));
        });
    }

    [Fact]
    public async Task ReplacingPositionClosesOldHistoryWhileAnUnchangedRefreshKeepsIt()
    {
        await WithTurnEvidenceAsync(async (window, game, frame, _) =>
        {
            var beforeFen = game.CurrentFen();
            var begin = typeof(MainWindow).GetMethod("BeginExternalHistoryAsync", Private)!;
            var apply = typeof(MainWindow).GetMethod("ApplyExternalPositionAsync", Private)!;
            await (Task)begin.Invoke(window, ["before resync"] )!;
            var history = Get<ExternalHistoryStore>(window, "_externalHistory");
            await (Task)apply.Invoke(window, [beforeFen, CancellationToken.None])!;
            Assert.Same(history, Get<ExternalHistoryStore>(window, "_externalHistory"));
            var replacement = JjRookCaptureTests.BeforeGame();
            Assert.True(replacement.TryMoveUci("a6b6", out var capture));
            Set(window, "_externalFrame", new ExternalFrame(frame.Window, ExternalBoardTests.Render(replacement)));
            await (Task)apply.Invoke(window, [replacement.CurrentFen(), CancellationToken.None])!;
            Assert.Null(Get<ExternalHistoryStore?>(window, "_externalHistory"));
            var events = await ExternalHistoryStore.ReadEventsAsync(history.EventsPath);
            Assert.Equal(new[] { "started", "ended" }, events.Select(entry => entry.Kind));
            Assert.Equal(beforeFen, events[^1].Fen);
            Assert.Equal(replacement.CurrentFen(), game.CurrentFen());
            await (Task)begin.Invoke(window, ["after resync"] )!;
            var restarted = Get<ExternalHistoryStore>(window, "_externalHistory");
            Assert.NotEqual(history.SessionDirectory, restarted.SessionDirectory);
            await restarted.FlushAsync();
            Assert.Equal(replacement.CurrentFen(), Assert.Single(await ExternalHistoryStore.ReadEventsAsync(restarted.EventsPath)).Fen);
        });
    }

    private static async Task WithTurnEvidenceAsync(Func<MainWindow, XiangqiGame, ExternalFrame, BoardObservation, Task> test)
    {
        await Session.Dispatch(async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), $"paddi-turn-evidence-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            var oldPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", Path.Combine(folder, "settings.json"));
            new AppPreferences { AutoAnalyze = false, ExternalScoreMode = 2 }.Save();
            var window = new MainWindow();
            try
            {
                window.Show();
                window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked = false;
                window.FindControl<ComboBox>("ExternalOrientationBox")!.SelectedIndex = 0;
                var game = Get<XiangqiGame>(window, "_game");
                game.LoadFen(JjRookCaptureTests.BeforeFen);
                var geometry = new BoardCalibration(40, 40, 440, 490, false);
                var frame = new ExternalFrame(new(951, 951, "turn evidence fixture", 0, 0, 480, 530), ExternalBoardTests.Render(game));
                var observed = BoardObservation.Read(frame, geometry);
                Set(window, "_externalDesktop", new TurnEvidenceDesktop(frame)); Set(window, "_positionRecognizer", new SyntheticPositionRecognizer());
                Set(window, "_externalFrame", frame);
                Set(window, "_externalCalibration", geometry);
                Set(window, "_externalTracker", new ExternalBoardTracker(observed, game));
                Set(window, "_externalSessionSkin", BoardSkin.Learn("turn evidence fixture", observed, game));
                Set(window, "_externalLinked", true);
                Set(window, "_externalTurnKnown", false);
                Set(window, "_externalRequestedTurn", null!);
                typeof(MainWindow).GetMethod("SetExternalTurn", Private)!.Invoke(window, [null]);
                await test(window, game, frame, observed);
            }
            finally
            {
                await (Task)typeof(MainWindow).GetMethod("EndExternalHistoryAsync", Private)!.Invoke(window, ["test completed"] )!;
                window.Close();
                await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", oldPath);
                Directory.Delete(folder, true);
            }
            return true;
        }, CancellationToken.None);
    }

    private sealed class TurnEvidenceDesktop(ExternalFrame frame) : IExternalDesktop
    {
        public bool EscapePressed() => false;
        public Task<IReadOnlyList<ExternalWindow>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExternalWindow>>([frame.Window]);
        public Task<ExternalFrame> CaptureAsync(ExternalWindow target, CancellationToken ct) => Task.FromResult(frame);
        public Task MoveAsync(ExternalWindow target, double fx, double fy, double tx, double ty, CancellationToken ct) =>
            throw new InvalidOperationException("Turn evidence tests must not send input.");
    }
}
