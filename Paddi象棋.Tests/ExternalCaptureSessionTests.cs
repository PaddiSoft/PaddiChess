using Avalonia.Controls;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureScaleChangesKeepConnectedObservationAndMoveHistory(bool flipped)
    {
        await WithPreflightAsync(flipped, true, async fixture =>
        {
            var window = fixture.Window;
            window.FindControl<ComboBox>("ExternalSideBox")!.SelectedIndex = 1;
            await fixture.ConnectAsync();

            fixture.Desktop.BackingScale = 2;
            fixture.Desktop.Advance("e3e4");
            await WaitPreflightAsync(() => Get<XiangqiGame>(window, "_game").Ply == 1, window);
            Assert.Equal(new BoardCalibration(80, 80, 880, 980, flipped), Get<BoardCalibration>(window, "_externalCalibration"));
            Assert.True(Get<bool>(window, "_externalObserving"));
            Assert.False(Get<bool>(window, "_externalRunning"));

            fixture.Desktop.BackingScale = 1;
            fixture.Desktop.Advance("h9g7");
            await WaitPreflightAsync(() => Get<XiangqiGame>(window, "_game").Ply == 2, window);
            var game = Get<XiangqiGame>(window, "_game");
            Assert.Equal(fixture.Desktop.Game.CurrentFen(), game.CurrentFen());
            Assert.Equal(new[] { "e3e4", "h9g7" }, game.History.Select(move => move.Uci));
            Assert.Equal(new BoardCalibration(40, 40, 440, 490, flipped), Get<BoardCalibration>(window, "_externalCalibration"));
            Assert.True(Get<bool>(window, "_externalLinked"));
            Assert.Empty(fixture.Desktop.Inputs);
            Assert.Contains(fixture.Desktop.CaptureSizes, capture => capture.PixelWidth == 960 && capture.PixelHeight == 1060);
            Assert.All(fixture.Desktop.CaptureSizes, capture =>
            {
                Assert.Equal(480, capture.Window.Width);
                Assert.Equal(530, capture.Window.Height);
            });
        });
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task CaptureScaleChangesDuringEitherPreInputCheckKeepLogicalMoveCoordinates(bool flipped, int verificationCapture)
    {
        await WithPreflightAsync(flipped, true, async fixture =>
        {
            var window = fixture.Window;
            await fixture.ConnectAsync();
            await WaitPreflightAsync(() => Get<object?>(window, "_externalPreparedDecision") is not null, window);
            var firstChoice = PreparedMove(Get<object>(window, "_externalPreparedDecision"));
            // Hold a normal observation before arming. Its next two captures are
            // the independent pre-input read and the post-recognition fresh read.
            var waiting = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int heldCapture = 0, scaleChangedAt = 0;
            fixture.Desktop.BeforeCaptureAsync = async (capture, ct) =>
            {
                if (heldCapture == 0)
                {
                    heldCapture = capture; waiting.TrySetResult(capture);
                    await release.Task.WaitAsync(ct);
                }
                else if (capture == heldCapture + verificationCapture)
                {
                    fixture.Desktop.BackingScale = 2;
                    scaleChangedAt = capture;
                }
            };
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Click(window, "ExternalStartButton");
            release.TrySetResult();
            await fixture.Desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitPreflightAsync(() => Get<XiangqiGame>(window, "_game").Ply == 1, window);
            fixture.Desktop.BeforeCaptureAsync = null;

            var first = Assert.Single(fixture.Desktop.Inputs);
            Assert.Equal(firstChoice, first.Move);
            Assert.Equal(2, first.BackingScale);
            Assert.True(scaleChangedAt > heldCapture);
            Assert.True(first.Captures >= scaleChangedAt + 3,
                "A scale change must be followed by a fresh observation and both pre-input checks.");
            AssertLogicalCaptureInput(first, flipped);

            fixture.Desktop.BackingScale = 1;
            fixture.Desktop.Advance(fixture.Desktop.Game.AllLegalMoves()[0].Uci);
            await WaitPreflightAsync(() => fixture.Desktop.InputCount == 2 && Get<XiangqiGame>(window, "_game").Ply == 3, window);
            Assert.Equal(2, fixture.Desktop.Inputs.Count);
            var second = fixture.Desktop.Inputs[1];
            Assert.Equal(1, second.BackingScale);
            AssertLogicalCaptureInput(second, flipped);
            Assert.Equal(fixture.Model!.Requests.Last().Move, second.Move);
            Assert.Equal(fixture.Desktop.Game.CurrentFen(), Get<XiangqiGame>(window, "_game").CurrentFen());
            Assert.Equal(new BoardCalibration(40, 40, 440, 490, flipped), Get<BoardCalibration>(window, "_externalCalibration"));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureLogicalWindowResizeStillBlocksPreparedInput(bool flipped)
    {
        await WithPreflightAsync(flipped, true, async fixture =>
        {
            var window = fixture.Window;
            await fixture.ConnectAsync();
            await WaitPreflightAsync(() => Get<object?>(window, "_externalPreparedDecision") is not null, window);
            fixture.Desktop.Target = fixture.Desktop.Target with { Width = 600 };
            fixture.Desktop.BackingScale = 2;
            Click(window, "ExternalStartButton");
            await Get<Task>(window, "_externalTask").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(fixture.Desktop.Inputs);
            Assert.Equal(0, fixture.Desktop.InputCount);
            Assert.Equal(0, Get<XiangqiGame>(window, "_game").Ply);
            Assert.Contains("窗口尺寸或身份变化", window.FindControl<TextBlock>("ExternalStatusText")!.Text);
            Assert.False(Get<bool>(window, "_externalRunning"));
        });
    }

    private static void AssertLogicalCaptureInput(PreflightInput input, bool flipped)
    {
        Assert.True(Square.TryParseUci(input.Move[..2], out var from));
        Assert.True(Square.TryParseUci(input.Move[2..], out var to));
        var logical = new BoardCalibration(40, 40, 440, 490, flipped);
        var expectedFrom = logical.Point(from); var expectedTo = logical.Point(to);
        Assert.InRange(Math.Abs(input.FromX - expectedFrom.X), 0, .01);
        Assert.InRange(Math.Abs(input.FromY - expectedFrom.Y), 0, .01);
        Assert.InRange(Math.Abs(input.ToX - expectedTo.X), 0, .01);
        Assert.InRange(Math.Abs(input.ToY - expectedTo.Y), 0, .01);
    }
}
