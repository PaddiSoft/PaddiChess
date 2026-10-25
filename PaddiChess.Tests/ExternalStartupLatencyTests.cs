using System.Diagnostics;
using Avalonia.Controls;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;
using PaddiXiangqi.External;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    [Theory]
    [InlineData("analysis")]
    [InlineData("model")]
    public async Task StandardOpeningTakeoverUsesOpeningBudgetWithoutWaitingForCancelledLocalWork(string priorWork)
    {
        await Session.Dispatch(async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), $"paddi-first-move-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            var oldPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", Path.Combine(folder, "settings.json"));
            new AppPreferences { AutoAnalyze = false, Level = 20, Threads = 1, HashMb = 16 }.Save();
            var predecessor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var window = new MainWindow();
            var desktop = new FirstMoveDesktop();
            var recognizer = new StartupPositionRecognizer(window);
            try
            {
                window.Show();
                window.FindControl<CheckBox>("PhaseTimeCheck")!.IsChecked = true;
                window.FindControl<NumericUpDown>("OpeningTimeBox")!.Value = .8m;
                window.FindControl<NumericUpDown>("ThinkBox")!.Value = 12;
                window.FindControl<CheckBox>("DepthLimitCheck")!.IsChecked = false;
                window.FindControl<ComboBox>("ExternalSpeedBox")!.SelectedIndex = 0;
                var geometry = new BoardCalibration(40, 40, 440, 490, false);
                var first = await desktop.CaptureAsync(desktop.Target, default);
                Set(window, "_externalDesktop", desktop); Set(window, "_positionRecognizer", recognizer);
                Set(window, "_externalFrame", first);
                Set(window, "_externalCalibration", geometry);
                Set(window, "_externalSessionSkin", BoardSkin.Learn("first-move fixture",
                    BoardObservation.Read(first.Png, geometry), desktop.Game));

                var connect = typeof(MainWindow).GetMethod("ConnectExternalAsync", Private)!;
                var clock = Stopwatch.StartNew();
                Assert.True(await (Task<bool>)connect.Invoke(window, [true])!);
                _latencyOutput.WriteLine($"Standard setup connected: {clock.Elapsed.TotalMilliseconds:F1} ms");
                Assert.True(Get<bool>(window, "_externalTurnKnown"));
                Assert.True(Get<XiangqiGame>(window, "_game").RedToMove);
                var settings = (EngineSettings)typeof(MainWindow).GetMethod("ReadPlayingEngineSettings", Private)!.Invoke(window, null)!;
                Assert.Equal(800, settings.MoveTimeMs);
                Assert.Null(settings.SearchDepthLimit);

                // Cancelled work has its own generation and engine/model request. It must
                // finish safely without holding the new target game's first move hostage.
                Set(window, priorWork == "analysis" ? "_searchTask" : "_llmTask", predecessor.Task);
                desktop.CapturesAtStart = desktop.Captures;
                clock.Restart();
                Click(window, "ExternalStartButton");
                await desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(5));
                _latencyOutput.WriteLine($"First input with cancelled {priorWork}: {clock.Elapsed.TotalMilliseconds:F1} ms");
                Assert.False(predecessor.Task.IsCompleted);
                Assert.Equal(1, desktop.InputCount);
                Assert.Equal(1, recognizer.Calls); // Connection evidence also serves the unchanged pre-input image.
                Assert.Equal(1, Get<PikafishClient>(window, "_externalPlayingEngine").ReadyHandshakeCount);
                Assert.True(desktop.CapturesAtInput - desktop.CapturesAtStart >= 4,
                    "Initial capture, two live stable frames and fresh pre-input validation must be retained.");
                Click(window, "ExternalStopButton");
                await Get<Task>(window, "_externalTask");
            }
            finally
            {
                predecessor.TrySetResult();
                Get<CancellationTokenSource?>(window, "_externalCancellation")?.Cancel();
                window.Close(); await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", oldPath);
                Directory.Delete(folder, true);
            }
            return true;
        }, default);
    }

    private sealed class StartupPositionRecognizer(MainWindow window) : IExternalPositionRecognizer
    {
        public int Calls { get; private set; }
        public async Task<SkinRecognition?> ReadAsync(ExternalFrame frame, BoardCalibration geometry,
            bool redToMove, CancellationToken ct, bool detectOrientation, BoardSkin? sessionSkin, string? customPath)
        {
            Calls++;
            // Engine preparation must already be running while initial recognition is
            // unresolved, and must not depend on it completing to release a semaphore.
            var preparation = Get<Task?>(window, "_externalConnectionWarmup");
            Assert.NotNull(preparation);
            await preparation.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Equal(1, Get<PikafishClient>(window, "_externalPlayingEngine").ProcessStartCount);
            return await new SyntheticPositionRecognizer().ReadAsync(frame, geometry, redToMove, ct,
                detectOrientation, sessionSkin, customPath);
        }
    }

    private sealed class FirstMoveDesktop : IExternalDesktop
    {
        private readonly byte[] _initial = ExternalBoardTests.Render(new XiangqiGame());
        public ExternalWindow Target { get; } = new(781, 781, "first move fixture", 0, 0, 480, 530);
        public XiangqiGame Game { get; } = new();
        public TaskCompletionSource FirstInput { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Captures { get; private set; }
        public int CapturesAtStart { get; set; }
        public int CapturesAtInput { get; private set; }
        public int InputCount { get; private set; }
        public bool EscapePressed() => false;
        public Task<IReadOnlyList<ExternalWindow>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExternalWindow>>([Target]);
        public Task<ExternalFrame> CaptureAsync(ExternalWindow target, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Captures++;
            return Task.FromResult(new ExternalFrame(Target, Game.Ply == 0 ? _initial : ExternalBoardTests.Render(Game)));
        }
        public Task MoveAsync(ExternalWindow target, double fromX, double fromY, double toX, double toY, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var from = new Square((int)Math.Round((fromX - 40) / 50), (int)Math.Round((fromY - 40) / 50));
            var to = new Square((int)Math.Round((toX - 40) / 50), (int)Math.Round((toY - 40) / 50));
            Assert.True(Game.TryMove(from, to, out _));
            CapturesAtInput = Captures; InputCount++;
            FirstInput.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
