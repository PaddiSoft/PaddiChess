using System.Diagnostics;
using Avalonia.Controls;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _latencyOutput;
    public ExternalSessionTests(Xunit.Abstractions.ITestOutputHelper output) => _latencyOutput = output;

    [Fact]
    public async Task StoppingAtConfirmedMoveKeepsTurnAndTrackerBaselineTogether()
    {
        await Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-stop-confirm-{Guid.NewGuid():N}.json");
            var oldPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            var window = new MainWindow();
            try
            {
                window.Show(); window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked = false;
                var game = Get<XiangqiGame>(window, "_game");
                var geometry = new BoardCalibration(40, 40, 440, 490, false);
                var tracker = new ExternalBoardTracker(BoardObservation.Read(ExternalBoardTests.Render(game), geometry), game);
                Set(window, "_externalTracker", tracker);
                var target = new XiangqiGame(); Assert.True(target.TryMoveUci("h2e2", out _));
                var after = BoardObservation.Read(ExternalBoardTests.Render(target), geometry);
                var match = tracker.Match(after, game, "h2e2"); Assert.True(match.Recognized);
                using var cancellation = new CancellationTokenSource();
                window.FindControl<ComboBox>("ExternalTurnBox")!.SelectionChanged += (_, _) => cancellation.Cancel();
                var accept = typeof(MainWindow).GetMethod("AcceptExternalMatchAsync", Private)!;
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    (Task)accept.Invoke(window, [match, after, null, cancellation.Token])!);
                Assert.Equal(target.CurrentFen(), game.CurrentFen());
                Assert.Equal(1, window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
                var resumed = tracker.Match(after, game);
                Assert.True(resumed.Recognized); Assert.Empty(resumed.Moves);
            }
            finally
            {
                window.Close(); await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", oldPath); if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, default);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ResyncDerivesTurnFromMissedMovesInsteadOfStaleTurnSelector(bool flipped, bool missedPair)
    {
        await Session.Dispatch(async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), $"paddi-turn-sync-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            var oldPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", Path.Combine(folder, "settings.json"));
            var window = new MainWindow();
            try
            {
                window.Show(); window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked = false;
                var game = Get<XiangqiGame>(window, "_game");
                if (!missedPair) Assert.True(game.TryMoveUci("h2e2", out _));
                var geometry = new BoardCalibration(40, 40, 440, 490, flipped);
                var before = BoardObservation.Read(ExternalBoardTests.Render(game, flipped), geometry);
                Set(window, "_externalTracker", new ExternalBoardTracker(before, game));
                Set(window, "_externalLinked", true);
                if (missedPair) Set(window, "_externalPendingMove", "h2e2");
                window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex = 1; // The reported stale value.
                window.FindControl<ComboBox>("ExternalOrientationBox")!.SelectedIndex = flipped ? 1 : 0;
                var target = new XiangqiGame();
                Assert.True(target.TryMoveUci("h2e2", out _));
                Assert.True(target.TryMoveUci("h9g7", out _));
                var frame = new ExternalFrame(new(99, 99, "turn test", 0, 0, 480, 530), ExternalBoardTests.Render(target, flipped));
                Set(window, "_externalFrame", frame); Set(window, "_externalCalibration", geometry);
                var recognize = typeof(MainWindow).GetMethod("RecognizeExternalFrameAsync", Private)!;
                Assert.True(await (Task<bool>)recognize.Invoke(window, [frame])!);
                Assert.Equal(target.CurrentFen(), game.CurrentFen());
                Assert.Equal(2, game.TotalPly); // No history reset or lost opponent move.
                Assert.True(game.RedToMove);
                Assert.Equal(0, window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
                Assert.Equal("红方走棋", window.FindControl<TextBlock>("TurnBadge")!.Text);
                Assert.Equal(game.CurrentFen(), window.FindControl<TextBox>("ExternalFenBox")!.Text);
                Assert.Null(Get<string?>(window, "_externalPendingMove"));
                Assert.False(Directory.Exists(Path.Combine(folder, "Recovery")));
            }
            finally
            {
                window.Close(); await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", oldPath); Directory.Delete(folder, true);
            }
            return true;
        }, default);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ConfirmsEachMoveWhileNativeInputIsStillCompleting(bool flipped, bool raw)
    {
        await Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-stream-{Guid.NewGuid():N}.json");
            var oldPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            var window = new MainWindow(); var desktop = new StreamingReplyDesktop(flipped, raw);
            try
            {
                window.Show(); window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked = false;
                window.FindControl<Slider>("LevelSlider")!.Value = 20;
                window.FindControl<NumericUpDown>("ThreadsBox")!.Value = 1;
                window.FindControl<CheckBox>("DepthLimitCheck")!.IsChecked = true;
                window.FindControl<NumericUpDown>("DepthBox")!.Value = 2;
                window.FindControl<NumericUpDown>("ThinkBox")!.Value = 1;
                window.FindControl<ComboBox>("ExternalSpeedBox")!.SelectedIndex = 0;
                window.FindControl<ComboBox>("ExternalOrientationBox")!.SelectedIndex = flipped ? 1 : 0;
                // This case deliberately controls red even when red is on top; override
                // the new lower-side default so the input timing remains the subject.
                window.FindControl<CheckBox>("ExternalAutoSideCheck")!.IsChecked = false;
                window.FindControl<ComboBox>("ExternalSideBox")!.SelectedIndex = 0;
                Set(window, "_externalDesktop", desktop); Set(window, "_positionRecognizer", new SyntheticPositionRecognizer());
                Set(window, "_externalFrame", await desktop.CaptureAsync(desktop.Target, default));
                Set(window, "_externalCalibration", new BoardCalibration(40, 40, 440, 490, flipped));
                Set(window, "_externalPositionReady", true);
                Click(window, "ExternalStartButton");
                var game = Get<XiangqiGame>(window, "_game");
                await desktop.OurMoveApplied.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await WaitPreflightAsync(() => game.Ply >= 1, window);
                _latencyOutput.WriteLine($"GUI confirmed own move: {Stopwatch.GetElapsedTime(desktop.OurMoveTimestamp).TotalMilliseconds:F1} ms; flipped={flipped}; raw={raw}");
                Assert.Equal(1, game.Ply); // Visible before the opponent's reply, not after MoveAsync returns.
                Assert.False(desktop.ReplyApplied.Task.IsCompleted);
                Assert.Equal(1, window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
                desktop.AllowReply.TrySetResult();
                await desktop.ReplyApplied.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await WaitPreflightAsync(() => game.Ply >= 2, window);
                _latencyOutput.WriteLine($"GUI confirmed reply: {Stopwatch.GetElapsedTime(desktop.ReplyTimestamp).TotalMilliseconds:F1} ms; flipped={flipped}; raw={raw}");
                Assert.Equal(2, game.Ply);
                Assert.False(desktop.InputCompleted);
                Assert.True(game.RedToMove);
                Assert.Equal(0, window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
                Assert.Equal(desktop.Game.CurrentFen(), game.CurrentFen());
                Assert.Equal(1, desktop.InputCount);
                Click(window, "ExternalStopButton"); await Get<Task>(window, "_externalTask");
                Assert.Null(Get<string?>(window, "_externalPendingMove"));
                Assert.Equal(1, desktop.InputCount);
            }
            finally
            {
                Get<CancellationTokenSource?>(window, "_externalCancellation")?.Cancel();
                window.Close(); await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", oldPath); if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, default);
    }

    private sealed class StreamingReplyDesktop(bool flipped, bool raw) : IExternalDesktop
    {
        public ExternalWindow Target { get; } = new(77, 77, "streaming board", 0, 0, 480, 530);
        public XiangqiGame Game { get; } = new();
        public TaskCompletionSource OurMoveApplied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReplyApplied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource AllowInputReturn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long OurMoveTimestamp { get; private set; }
        public long ReplyTimestamp { get; private set; }
        public bool InputCompleted { get; private set; }
        public int InputCount { get; private set; }
        public bool EscapePressed() => false;
        public Task<IReadOnlyList<ExternalWindow>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ExternalWindow>>([Target]);
        public Task<ExternalFrame> CaptureAsync(ExternalWindow target, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var png = ExternalBoardTests.Render(Game, flipped);
            return Task.FromResult(raw ? RawCaptureTests.RawFrame(png, Target) : new ExternalFrame(Target, png));
        }
        public async Task MoveAsync(ExternalWindow target, double fx, double fy, double tx, double ty, CancellationToken ct)
        {
            InputCount++;
            Square Point(double x, double y)
            {
                var f = (int)Math.Round((x - 40) / 50); var r = (int)Math.Round((y - 40) / 50);
                return flipped ? new(8 - f, 9 - r) : new(f, r);
            }
            Assert.True(Game.TryMove(Point(fx, fy), Point(tx, ty), out _));
            OurMoveTimestamp = Stopwatch.GetTimestamp();
            OurMoveApplied.TrySetResult();
            // Assert observation advances while native input is pending without
            // relying on a hosted runner delivering timers within 160 ms.
            await AllowReply.Task.WaitAsync(ct);
            Assert.True(Game.TryMoveUci(Game.AllLegalMoves()[0].Uci, out _));
            ReplyTimestamp = Stopwatch.GetTimestamp();
            ReplyApplied.TrySetResult();
            await AllowInputReturn.Task.WaitAsync(ct); // Only cancellation releases this pending native call.
            InputCompleted = true;
        }
    }
}
