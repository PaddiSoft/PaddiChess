using System.Text.Json;
using Avalonia.Controls;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    [Fact]
    public async Task RepeatedRemoteGameStillConfirmsOurMoveAndImmediateReply()
    {
        await Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-repetition-session-{Guid.NewGuid():N}.json");
            var old = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            var window = new MainWindow();
            try
            {
                window.Show(); window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked = false;
                window.FindControl<Slider>("LevelSlider")!.Value = 20;
                window.FindControl<NumericUpDown>("ThreadsBox")!.Value = 1;
                window.FindControl<CheckBox>("DepthLimitCheck")!.IsChecked = true;
                window.FindControl<NumericUpDown>("DepthBox")!.Value = 2;
                var game = Get<XiangqiGame>(window, "_game");
                string[] cycle = ["b0c2", "b9c7", "c2b0", "c7b9"];
                foreach (var uci in cycle.Concat(cycle)) Assert.True(game.TryMoveUci(uci, out _));
                Assert.Equal(GameResult.Ongoing, game.Result); // The old session declared a friendly draw here.
                var geometry = new BoardCalibration(40, 40, 440, 490, false);
                var baseline = ExternalBoardTests.Render(game);
                Set(window, "_externalTracker", new ExternalBoardTracker(BoardObservation.Read(baseline, geometry), game));
                var remote = new XiangqiGame { ExternalAdjudication = true };
                foreach (var uci in cycle.Concat(cycle).Concat(cycle.Take(2))) Assert.True(remote.TryMoveUci(uci, out _));
                var desktop = new ObserveOnlyDesktop(ExternalBoardTests.Render(remote));
                Set(window, "_externalDesktop", desktop);
                Set(window, "_externalFrame", new ExternalFrame(desktop.Target, baseline));
                Set(window, "_externalCalibration", geometry);
                Set(window, "_externalLinked", true);
                Set(window, "_externalPendingMove", "b0c2");
                Click(window, "ExternalStartButton");
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (game.Ply < 10 && Get<bool>(window, "_externalRunning") && DateTime.UtcNow < deadline) await Task.Delay(20);
                Assert.Equal(10, game.Ply);
                Assert.True(Get<bool>(window, "_externalRunning"), window.FindControl<TextBlock>("ExternalStatusText")!.Text);
                Assert.Equal(remote.CurrentFen(), game.CurrentFen());
                Assert.Equal(GameResult.Ongoing, game.Result);
                Assert.Null(Get<string?>(window, "_externalPendingMove"));
                Assert.True(window.FindControl<CheckBox>("ExternalAutoTurnCheck")!.IsChecked);
                Assert.Equal(0, window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
                // The saved history must remain replayable beyond a local threefold draw.
                var record = JsonSerializer.Deserialize<GameRecord>(JsonSerializer.Serialize(new GameRecord
                { StartFen = game.StartFen, Moves = game.History.Select(m => m.Uci).ToList(), ExternalAdjudication = game.ExternalAdjudication }))!;
                var replay = new XiangqiGame { ExternalAdjudication = record.ExternalAdjudication };
                replay.LoadFen(record.StartFen);
                foreach (var uci in record.Moves) Assert.True(replay.TryMoveUci(uci, out _));
                Assert.Equal(game.CurrentFen(), replay.CurrentFen());
                Click(window, "ExternalStopButton"); await Get<Task>(window, "_externalTask");
                Assert.True(Get<bool>(window, "_externalLinked"));
            }
            finally
            {
                Get<CancellationTokenSource?>(window, "_externalCancellation")?.Cancel();
                window.Close(); await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", old); if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, default);
    }

    [Fact]
    public async Task NoLegalMovePausesOutputAndKeepsExternalObservationConnected()
    {
        await Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-observe-ended-position-{Guid.NewGuid():N}.json");
            var old = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            var window = new MainWindow();
            try
            {
                window.Show(); window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked = false;
                var game = Get<XiangqiGame>(window, "_game");
                game.LoadFen("3k5/4R4/3R5/9/9/9/9/9/9/4K4 b - - 0 1");
                Assert.Empty(game.AllLegalMoves());
                var png = ExternalBoardTests.Render(game);
                var geometry = new BoardCalibration(40, 40, 440, 490, false);
                var desktop = new ObserveOnlyDesktop(png);
                Set(window, "_externalDesktop", desktop);
                Set(window, "_externalFrame", new ExternalFrame(desktop.Target, png));
                Set(window, "_externalCalibration", geometry);
                Set(window, "_externalTracker", new ExternalBoardTracker(BoardObservation.Read(png, geometry), game));
                Set(window, "_externalLinked", true);
                Click(window, "ExternalStartButton");
                await WaitPreflightAsync(() => desktop.Captures >= 2 &&
                    window.FindControl<TextBlock>("ExternalStatusText")!.Text!.Contains("继续同步"), window);
                Assert.True(Get<bool>(window, "_externalRunning"), window.FindControl<TextBlock>("ExternalStatusText")!.Text);
                Assert.True(desktop.Captures >= 2);
                Assert.Equal(0, desktop.Inputs);
                Assert.Contains("继续同步", window.FindControl<TextBlock>("ExternalStatusText")!.Text);
                Click(window, "ExternalStopButton"); await Get<Task>(window, "_externalTask");
            }
            finally
            {
                Get<CancellationTokenSource?>(window, "_externalCancellation")?.Cancel();
                window.Close(); await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", old); if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, default);
    }

    [Fact]
    public async Task ManualTurnCorrectionOnPausedBoardPreservesAutoPreferenceAndConnection()
    {
        await Session.Dispatch(async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), $"paddi-manual-turn-{Guid.NewGuid():N}"); Directory.CreateDirectory(folder);
            var old = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", Path.Combine(folder, "settings.json"));
            var window = new MainWindow();
            try
            {
                window.Show(); window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked = false;
                var game = Get<XiangqiGame>(window, "_game");
                Assert.True(game.TryMoveUci("h2e2", out _));
                var png = ExternalBoardTests.Render(game); var geometry = new BoardCalibration(40, 40, 440, 490, false);
                var frame = new ExternalFrame(new(73, 73, "turn correction", 0, 0, 480, 530), png);
                Set(window, "_externalFrame", frame); Set(window, "_externalCalibration", geometry);
                Set(window, "_externalTracker", new ExternalBoardTracker(BoardObservation.Read(png, geometry), game));
                Set(window, "_externalLinked", true);
                typeof(MainWindow).GetMethod("SetExternalTurn", Private)!.Invoke(window, [false]);
                typeof(MainWindow).GetMethod("RefreshExternalControls", Private)!.Invoke(window, null);
                Assert.True(window.FindControl<Grid>("ExternalTurnPanel")!.IsEnabled);
                window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex = 0;
                Assert.True(window.FindControl<CheckBox>("ExternalAutoTurnCheck")!.IsChecked);
                Assert.True(await (Task<bool>)typeof(MainWindow).GetMethod("RecognizeExternalFrameAsync", Private)!.Invoke(window, [frame])!);
                Assert.True(game.RedToMove);
                Assert.True(Get<bool>(window, "_externalLinked"));
                Assert.True(window.FindControl<CheckBox>("ExternalAutoTurnCheck")!.IsChecked);
                Assert.Equal(0, window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
                Assert.Single(Directory.GetFiles(Path.Combine(folder, "Recovery"), "*.paddi.json"));
            }
            finally
            {
                window.Close(); await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", old); Directory.Delete(folder, true);
            }
            return true;
        }, default);
    }

    private sealed class ObserveOnlyDesktop(byte[] png) : IExternalDesktop
    {
        public ExternalWindow Target { get; } = new(71, 71, "observation fixture", 0, 0, 480, 530);
        public int Captures { get; private set; }
        public int Inputs { get; private set; }
        public bool EscapePressed() => false;
        public Task<IReadOnlyList<ExternalWindow>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ExternalWindow>>([Target]);
        public Task<ExternalFrame> CaptureAsync(ExternalWindow target, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Captures++; return Task.FromResult(new ExternalFrame(Target, png)); }
        public async Task MoveAsync(ExternalWindow target, double fx, double fy, double tx, double ty, CancellationToken ct)
        { Inputs++; await Task.Delay(Timeout.Infinite, ct); }
    }
}
