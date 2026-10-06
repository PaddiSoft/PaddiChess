using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public class ExternalTurnInferenceTests
{
    [Fact]
    public void InitialAndCheckedPositionsHaveDeterministicTurns()
    {
        Assert.True(ExternalTurnInference.FromPosition(XiangqiGame.InitialFen));
        Assert.False(ExternalTurnInference.FromPosition("4k4/4R4/9/9/9/9/9/9/9/4K4 b - - 0 1"));
        Assert.True(ExternalTurnInference.FromPosition("4k4/9/9/9/9/9/9/9/4r4/4K4 w - - 0 1"));
        var game = new XiangqiGame();
        Assert.True(game.TryMoveUci("h2e2", out _));
        Assert.False(ExternalTurnInference.FromPosition(game.CurrentFen()));
        Assert.True(game.TryMoveUci("h9g7", out _));
        Assert.True(ExternalTurnInference.FromPosition(game.CurrentFen()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RunningClockNeedsExactlyOneClockToCountDown(bool topRunning)
    {
        var first = new ExternalClockReading(789, 790);
        var second = topRunning ? new ExternalClockReading(788, 790) : new ExternalClockReading(789, 789);
        Assert.Equal(topRunning, ExternalTurnInference.RunningClock(first, second));
        Assert.Null(ExternalTurnInference.RunningClock(first, new(788, 789)));
        Assert.Null(ExternalTurnInference.RunningClock(first, new(null, 789)));
    }

    [Fact]
    public async Task MacClockReaderFindsBothTimersInRealJjScreenshot()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jj-wechat-midgame-1.png"));
        var geometry = BoardLocator.Locate(png)!;
        var clocks = await ExternalTurnInference.ReadClocksAsync(png, geometry, CancellationToken.None);
        Assert.Equal(new ExternalClockReading(789, 790), clocks);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ObservesUnknownMiddleGameTurnFromTheNextConfirmedMove(bool flipped, bool redMoves)
    {
        var game = new XiangqiGame();
        Assert.True(game.TryMoveUci("h2e2", out _));
        if (redMoves) Assert.True(game.TryMoveUci("h9g7", out _));
        var geometry = new BoardCalibration(40, 40, 440, 490, flipped);
        var baseline = BoardObservation.Read(ExternalBoardTests.Render(game, flipped), geometry);
        var observer = new ExternalTurnObserver(baseline, game.CurrentFen());
        var uci = redMoves ? "e2e6" : "h9g7";
        Assert.True(game.TryMoveUci(uci, out _));
        var after = BoardObservation.Read(ExternalBoardTests.Render(game, flipped), geometry);
        Assert.Null(observer.Observe(after, TimeSpan.Zero, []));
        var decided = observer.Observe(after, TimeSpan.FromMilliseconds(80), []);
        Assert.NotNull(decided);
        Assert.Equal(redMoves, decided.MoverRed);
        Assert.Equal(uci, decided.Uci);
    }

    [Fact]
    public void FastReplyCanIdentifyTheStartingSideFromTwoDependentMoves()
    {
        var game = new XiangqiGame();
        game.LoadFen("3k5/4r4/4p4/9/9/9/4R4/9/9/4K4 w - - 0 1");
        var geometry = new BoardCalibration(40, 40, 440, 490, false);
        var baseline = BoardObservation.Read(ExternalBoardTests.Render(game), geometry);
        var observer = new ExternalTurnObserver(baseline, game.CurrentFen());
        Assert.True(game.TryMoveUci("e3e7", out _));
        Assert.True(game.TryMoveUci("e8e7", out _));
        var after = BoardObservation.Read(ExternalBoardTests.Render(game), geometry);
        Assert.Null(observer.Observe(after, TimeSpan.Zero, []));
        var decided = observer.Observe(after, TimeSpan.FromMilliseconds(80), []);
        Assert.NotNull(decided);
        Assert.True(decided.MoverRed);
        Assert.Equal(new[] { "e3e7", "e8e7" }, decided.Match.Moves);
    }
}

[Collection("Desktop integration")]
public class ExternalAutoSideUiTests
{
    private static HeadlessUnitTestSession Session => ExternalSessionTests.Session;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;

    [Fact]
    public async Task OrientationSelectsTheLowerSideUntilUserOverridesIt()
    {
        await Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-auto-side-{Guid.NewGuid():N}.json");
            var old = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            var window = new MainWindow();
            try
            {
                window.Show();
                var orientation = window.FindControl<ComboBox>("ExternalOrientationBox")!;
                var side = window.FindControl<ComboBox>("ExternalSideBox")!;
                var automatic = window.FindControl<CheckBox>("ExternalAutoSideCheck")!;
                Assert.True(automatic.IsChecked);
                Assert.Equal(0, side.SelectedIndex);
                orientation.SelectedIndex = 1;
                Assert.Equal(1, side.SelectedIndex);
                side.SelectedIndex = 0;
                Assert.False(automatic.IsChecked);
                orientation.SelectedIndex = 0;
                orientation.SelectedIndex = 1;
                Assert.Equal(0, side.SelectedIndex);
                automatic.IsChecked = true;
                Assert.Equal(1, side.SelectedIndex);
            }
            finally
            {
                window.Close();
                await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", old);
                if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LiveTakeoverWaitsForObservedBlackMoveAndSetsRedToMove()
    {
        await Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-auto-turn-{Guid.NewGuid():N}.json");
            var old = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            var window = new MainWindow();
            var desktop = new TurnDesktop();
            try
            {
                window.Show();
                window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked = false;
                window.FindControl<ComboBox>("ExternalOrientationBox")!.SelectedIndex = 1;
                Assert.Equal(1, window.FindControl<ComboBox>("ExternalSideBox")!.SelectedIndex);
                Set(window, "_externalDesktop", desktop); Set(window, "_positionRecognizer", new SyntheticPositionRecognizer());
                Set(window, "_externalFrame", await desktop.CaptureAsync(desktop.Target, CancellationToken.None));
                Set(window, "_externalCalibration", new BoardCalibration(40, 40, 440, 490, true));
                window.FindControl<TextBox>("ExternalFenBox")!.Text = desktop.Game.CurrentFen();
                Set(window, "_externalPositionReady", true);
                Set(window, "_externalTurnKnown", false);
                window.FindControl<Button>("ExternalStartButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (Get<XiangqiGame>(window, "_game").Ply == 0 && DateTime.UtcNow < deadline)
                    await Task.Delay(40);
                var game = Get<XiangqiGame>(window, "_game");
                Assert.Equal("h9g7", Assert.Single(game.History).Uci);
                Assert.True(game.RedToMove);
                Assert.True(Get<bool>(window, "_externalTurnKnown"));
                Assert.Equal(0, window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
                Assert.Equal(0, desktop.MovesSent);
                window.FindControl<Button>("ExternalStopButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Get<Task>(window, "_externalTask");
            }
            finally
            {
                Get<CancellationTokenSource?>(window, "_externalCancellation")?.Cancel();
                window.Close(); await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", old);
                if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task UnknownTurnCanBeChosenImmediatelyWithoutDisconnecting()
    {
        await Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-turn-override-{Guid.NewGuid():N}.json");
            var old = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            var window = new MainWindow();
            try
            {
                window.Show();
                var current = new XiangqiGame();
                Assert.True(current.TryMoveUci("h2e2", out _));
                var game = Get<XiangqiGame>(window, "_game");
                game.LoadFen(current.CurrentFen());
                Set(window, "_externalTracker", new ExternalBoardTracker(
                    BoardObservation.Read(ExternalBoardTests.Render(game), new(40, 40, 440, 490, false)), game));
                Set(window, "_externalLinked", true);
                Set(window, "_externalTurnKnown", false);
                typeof(MainWindow).GetMethod("RefreshExternalControls", Private)!.Invoke(window, null);
                var panel = window.FindControl<StackPanel>("ExternalTurnOverridePanel")!;
                Assert.True(panel.IsVisible);
                var chooseRed = panel.Children.OfType<Button>().Single(button => (string?)button.Tag == "red");
                chooseRed.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(game.RedToMove);
                Assert.True(Get<bool>(window, "_externalTurnKnown"));
                Assert.True(window.FindControl<CheckBox>("ExternalAutoTurnCheck")!.IsChecked);
                Assert.False(panel.IsVisible);
            }
            finally
            {
                window.Close(); await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", old);
                if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, CancellationToken.None);
    }

    private sealed class TurnDesktop : IExternalDesktop
    {
        public ExternalWindow Target { get; } = new(704, 704, "turn test", 0, 0, 480, 530);
        public XiangqiGame Game { get; } = new();
        public int Captures { get; private set; }
        public int MovesSent { get; private set; }
        public TurnDesktop() => Assert.True(Game.TryMoveUci("h2e2", out _));
        public Task<IReadOnlyList<ExternalWindow>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ExternalWindow>>([Target]);
        public Task<ExternalFrame> CaptureAsync(ExternalWindow window, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (++Captures == 4) Assert.True(Game.TryMoveUci("h9g7", out _));
            return Task.FromResult(new ExternalFrame(Target, ExternalBoardTests.Render(Game, flipped: true)));
        }
        public Task MoveAsync(ExternalWindow window, double fromX, double fromY, double toX, double toY, CancellationToken ct)
        { MovesSent++; throw new InvalidOperationException("行棋方未确认前不应发送落子"); }
        public bool EscapePressed() => false;
    }
}
