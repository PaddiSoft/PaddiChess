using System.Diagnostics;
using Avalonia.Controls;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveObservationContinuesDuringLongSearchAndDiscardsOutdatedDecision(bool flipped)
    {
        await Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-live-search-{Guid.NewGuid():N}.json");
            var oldPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            var window = new MainWindow(); var desktop = new ChangingDuringSearchDesktop(flipped);
            try
            {
                window.Show();
                window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked = false;
                window.FindControl<Slider>("LevelSlider")!.Value = 20;
                window.FindControl<NumericUpDown>("ThreadsBox")!.Value = 1;
                window.FindControl<NumericUpDown>("ThinkBox")!.Value = 30;
                window.FindControl<CheckBox>("DepthLimitCheck")!.IsChecked = true;
                window.FindControl<NumericUpDown>("DepthBox")!.Value = 80;
                window.FindControl<ComboBox>("ExternalOrientationBox")!.SelectedIndex = flipped ? 1 : 0;
                window.FindControl<CheckBox>("ExternalAutoSideCheck")!.IsChecked = false;
                window.FindControl<ComboBox>("ExternalSideBox")!.SelectedIndex = 0;
                Set(window, "_externalDesktop", desktop); Set(window, "_positionRecognizer", new SyntheticPositionRecognizer());
                Set(window, "_externalFrame", await desktop.CaptureAsync(desktop.Target, default));
                Set(window, "_externalCalibration", new BoardCalibration(40, 40, 440, 490, flipped));
                Set(window, "_externalPositionReady", true);
                Click(window, "ExternalStartButton");
                var deadline = Stopwatch.StartNew();
                while (Get<EngineInfo?>(window, "_lastEngineInfo") is not { Depth: >= 4 } && deadline.Elapsed < TimeSpan.FromSeconds(20))
                    await Task.Delay(20);
                Assert.NotNull(Get<EngineInfo?>(window, "_lastEngineInfo"));
                var captureCount = desktop.Captures;
                await Task.Delay(100);
                Assert.True(desktop.Captures > captureCount); // Search is still active, not awaited by capture.

                var game = Get<XiangqiGame>(window, "_game");
                Assert.True(desktop.Game.TryMoveUci("e3e4", out _));
                var clock = Stopwatch.StartNew();
                // Assert progress well before the 30-second search completes.
                // A shared CI runner can suspend this test for over 400 ms;
                // report latency separately from the concurrency assertion.
                var observationBudget = TimeSpan.FromSeconds(5);
                while (game.Ply < 1 && clock.Elapsed < observationBudget) await Task.Delay(10);
                _latencyOutput.WriteLine($"Move during active search confirmed: {clock.Elapsed.TotalMilliseconds:F1} ms; flipped={flipped}");
                Assert.Equal(1, game.Ply);
                Assert.False(game.RedToMove);
                Assert.Equal(1, window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
                Assert.Equal(0, desktop.InputCount);
                Assert.True(desktop.Game.TryMoveUci("h9g7", out _));
                clock.Restart();
                while (game.Ply < 2 && clock.Elapsed < observationBudget) await Task.Delay(10);
                _latencyOutput.WriteLine($"Reply after stale search cancelled: {clock.Elapsed.TotalMilliseconds:F1} ms; flipped={flipped}");
                Assert.Equal(2, game.Ply);
                Assert.Equal(desktop.Game.CurrentFen(), game.CurrentFen());
                Assert.True(game.RedToMove);
                Assert.Equal(0, window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
                Click(window, "ExternalStopButton"); await Get<Task>(window, "_externalTask");
                Assert.Equal(0, desktop.InputCount); // The old search cannot click after external moves.
            }
            finally
            {
                Get<CancellationTokenSource?>(window, "_externalCancellation")?.Cancel();
                window.Close(); await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", oldPath);
                if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, default);
    }

    [Theory]
    [InlineData(0, 0, false)] // Engine: reuse its own score.
    [InlineData(0, 1, true)]  // Opt-in opponent-turn scoring.
    [InlineData(1, 0, true)]  // Hosted model: local engine scores independently.
    [InlineData(1, 2, false)] // Explicitly disabled.
    public async Task ExternalScorePolicyDoesNotStartAnUnrequestedCompetingEngine(int controller, int mode, bool scores)
    {
        await Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-score-policy-{Guid.NewGuid():N}.json");
            var oldPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            var window = new MainWindow();
            try
            {
                window.Show();
                window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked = false;
                var game = Get<XiangqiGame>(window, "_game");
                Assert.True(game.TryMoveUci("e3e4", out _)); // Opponent's turn.
                window.FindControl<ComboBox>("ExternalControllerBox")!.SelectedIndex = controller;
                window.FindControl<ComboBox>("ExternalScoreModeBox")!.SelectedIndex = mode;
                Set(window, "_externalLinked", true); Set(window, "_externalRunning", true);
                typeof(MainWindow).GetMethod("QueueExternalScore", Private)!.Invoke(window, null);
                Assert.Equal(scores, Get<Task?>(window, "_llmAnalysisTask") is not null);
                if (controller == 0 && mode == 1)
                {
                    Assert.True(game.TryMoveUci("h9g7", out _));
                    var engine = new PikafishClient();
                    using var cancellation = new CancellationTokenSource();
                    cancellation.Cancel();
                    var calculate = typeof(MainWindow).GetMethod("CalculateExternalDecisionAsync", Private)!;
                    await (Task)calculate.Invoke(window, [engine, true, null, cancellation.Token])!;
                    Assert.Null(Get<Task?>(window, "_llmAnalysisTask"));
                    await engine.DisposeAsync();
                }
            }
            finally
            {
                Set(window, "_externalRunning", false);
                window.Close(); await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", oldPath);
                if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, default);
    }

    private sealed class ChangingDuringSearchDesktop(bool flipped) : IExternalDesktop
    {
        public ExternalWindow Target { get; } = new(80, 80, "changing during search", 0, 0, 480, 530);
        public XiangqiGame Game { get; } = new();
        public int Captures { get; private set; }
        public int InputCount { get; private set; }
        public bool EscapePressed() => false;
        public Task<IReadOnlyList<ExternalWindow>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ExternalWindow>>([Target]);
        public Task<ExternalFrame> CaptureAsync(ExternalWindow window, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Captures++;
            return Task.FromResult(new ExternalFrame(Target, ExternalBoardTests.Render(Game, flipped)));
        }
        public Task MoveAsync(ExternalWindow window, double fx, double fy, double tx, double ty, CancellationToken ct)
        { InputCount++; return Task.CompletedTask; }
    }
}

public sealed class PieceLetteringTests
{
    [Theory]
    [InlineData('R', '俥')]
    [InlineData('N', '傌')]
    [InlineData('C', '炮')]
    [InlineData('r', '車')]
    [InlineData('n', '馬')]
    [InlineData('c', '砲')]
    public void BoardLetteringDistinguishesRedAndBlack(char piece, char glyph)
    { Assert.Equal(glyph, XiangqiGame.DisplayBoardPiece(piece)); }

    [Fact]
    public void BoardLetteringDoesNotAlterGameNotation()
    {
        var game = new XiangqiGame();
        Assert.True(game.TryMoveUci("h0g2", out var move));
        Assert.Equal("馬二进三", move.Notation);
        Assert.Equal('馬', XiangqiGame.DisplayPiece('N'));
    }
}
