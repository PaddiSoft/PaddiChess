using Avalonia.Controls;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    [Fact]
    public async Task UncertainNewPositionShowsUnknownTurnRatherThanThePreviousSide()
    {
        await WithPreflightAsync(false, false, async fixture =>
        {
            var window = fixture.Window;
            const string middle = "4k4/9/9/9/4P4/9/9/9/9/4K4 w - - 0 1";
            typeof(MainWindow).GetMethod("InferExternalTurn", Private)!.Invoke(window, [middle]);
            await (Task)typeof(MainWindow).GetMethod("ApplyExternalPositionAsync", Private)!.Invoke(window, [middle, CancellationToken.None])!;
            Assert.False(Get<bool>(window, "_externalTurnKnown"));
            Assert.Equal(-1, window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
            Assert.True(window.FindControl<CheckBox>("ExternalAutoTurnCheck")!.IsChecked);
            Assert.Equal(0, fixture.Desktop.InputCount);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NextGameReinfersTurnRatherThanKeepingThePreviousManualCorrection(bool flipped, bool redAlreadyMoved)
    {
        await WithPreflightAsync(flipped, false, async fixture =>
        {
            var window = fixture.Window;
            if (redAlreadyMoved) fixture.Desktop.Advance("h2e2");
            fixture.Desktop.HoldInputs = true;
            var game = Get<XiangqiGame>(window, "_game");
            game.LoadFen("3k5/4R4/3R5/9/9/9/9/9/9/4K4 b - - 0 1");
            Set(window, "_externalCompleted", true);
            Set(window, "_externalTurnKnown", true);
            Set(window, "_externalRequestedTurn", redAlreadyMoved);
            window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex = redAlreadyMoved ? 0 : 1;
            window.FindControl<ComboBox>("ExternalOrientationBox")!.SelectedIndex = flipped ? 0 : 1;
            window.FindControl<CheckBox>("ExternalAutoSideCheck")!.IsChecked = true;
            Assert.True(window.FindControl<CheckBox>("ExternalAutoTurnCheck")!.IsChecked);
            var targets = window.FindControl<ComboBox>("ExternalWindowBox")!;
            targets.ItemsSource = new[] { fixture.Desktop.Target }; targets.SelectedIndex = 0;
            targets.ItemsSource = Array.Empty<ExternalWindow>();
            targets.ItemsSource = new[] { fixture.Desktop.Target with { Title = "same target after refresh" } };
            targets.SelectedIndex = 0;
            Assert.NotNull(Get<ExternalFrame?>(window, "_externalFrame"));
            Assert.NotNull(Get<BoardCalibration?>(window, "_externalCalibration"));
            Assert.True(Get<bool>(window, "_externalCompleted"));

            Click(window, "ExternalStartButton");
            await WaitPreflightAsync(() => Get<bool>(window, "_externalObserving") &&
                !Get<bool>(window, "_externalStarting"), window);
            Assert.False(Get<bool>(window, "_externalCompleted"));
            Assert.True(Get<bool>(window, "_externalRunning"));
            Assert.Equal(!redAlreadyMoved, game.RedToMove);
            Assert.Equal(redAlreadyMoved ? 1 : 0, window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
            Assert.Equal(flipped ? 1 : 0, window.FindControl<ComboBox>("ExternalOrientationBox")!.SelectedIndex);
            Assert.Equal(flipped ? 1 : 0, window.FindControl<ComboBox>("ExternalSideBox")!.SelectedIndex);
            Assert.Equal(fixture.Desktop.Game.CurrentFen().Split(' ')[0], game.CurrentFen().Split(' ')[0]);
            Assert.Empty(game.History);
            Assert.True(window.FindControl<CheckBox>("ExternalAutoTurnCheck")!.IsChecked);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundModeNeverChangesToFocusedInputEvenOnItsFirstAttempt(bool rejectFirst)
    {
        await WithPreflightAsync(false, true, async fixture =>
        {
            var window = fixture.Window;
            fixture.Desktop.RejectFirstInput = rejectFirst;
            window.FindControl<ComboBox>("ExternalDeliveryBox")!.SelectedIndex = 1;
            await fixture.ConnectAsync();
            await WaitPreflightAsync(() => Get<object?>(window, "_externalPreparedDecision") != null, window);
            Assert.Empty(fixture.Desktop.Deliveries); // Connecting alone must never focus/click.
            Click(window, "ExternalStartButton");
            await WaitPreflightAsync(() => Get<XiangqiGame>(window, "_game").Ply == 1, window);
            var firstMode = ExternalInputDelivery.TargetWindow;
            Assert.All(fixture.Desktop.Deliveries, actual => Assert.Equal(firstMode, actual));
            Assert.Equal(rejectFirst ? 2 : 1, fixture.Desktop.Deliveries.Count);
            Assert.Equal(1, fixture.Model!.Count);

            fixture.Desktop.Advance(fixture.Desktop.Game.AllLegalMoves().First().Uci);
            await WaitPreflightAsync(() => Get<XiangqiGame>(window, "_game").Ply == 3, window);
            Assert.Equal(ExternalInputDelivery.TargetWindow, fixture.Desktop.Deliveries.Last());
            Assert.Equal(1, window.FindControl<ComboBox>("ExternalDeliveryBox")!.SelectedIndex);
            Assert.Equal(2, fixture.Model.Count);
        });
    }
}

public class ExternalObservationReaderTests
{
    [Fact]
    public async Task IdenticalValidatedFramesReuseFeaturesButChangedPixelsAndCancellationDoNot()
    {
        var game = new XiangqiGame();
        var target = new ExternalWindow(1, 1, "cache fixture", 0, 0, 480, 530);
        var frame = new ExternalFrame(target, ExternalBoardTests.Render(game)) { Sequence = 1 };
        var reader = new ExternalObservationReader(new(40, 40, 440, 490, false));
        var first = await reader.ReadAsync(frame, default);
        var identical = await reader.ReadAsync(frame with { Png = frame.Png.ToArray(), Sequence = 2 }, default);
        Assert.Same(first, identical);
        Assert.True(game.TryMoveUci("e3e4", out _));
        var changed = await reader.ReadAsync(frame with { Png = ExternalBoardTests.Render(game), Sequence = 1 }, default);
        Assert.NotSame(first, changed); // A restarted producer may reuse sequence numbers.
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(frame, cancelled.Token));
    }
}
