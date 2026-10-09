using Avalonia.Controls;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using PaddiXiangqi.Sessions;
using SkiaSharp;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedMoveExecutesDespiteContinuouslyChangingSelectionColours(bool flipped)
    {
        await WithPreflightAsync(flipped, true, async fixture =>
        {
            await fixture.ConnectAsync();
            await WaitPreflightAsync(() => Get<object?>(fixture.Window, "_externalPreparedDecision") != null, fixture.Window);
            fixture.Desktop.PulseEveryCapture = true;
            Click(fixture.Window, "ExternalStartButton");
            await fixture.Desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, fixture.Model!.Count);
            Assert.Equal(1, fixture.Desktop.InputCount);
        });
    }

    [Fact]
    public async Task SlowPreInputRecognitionDoesNotBlockCaptureOrRepeatEngineCalculation()
    {
        await WithPreflightAsync(false, true, async fixture =>
        {
            await fixture.ConnectAsync();
            await WaitPreflightAsync(() => Get<object?>(fixture.Window, "_externalPreparedDecision") != null, fixture.Window);
            var reader = new DelayedPositionRecognizer();
            Set(fixture.Window, "_positionRecognizer", reader);
            Click(fixture.Window, "ExternalStartButton");
            await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var captures = fixture.Desktop.Captures;
            await Task.Delay(250);
            Assert.True(fixture.Desktop.Captures >= captures + 3, "Full inference must not block observation.");
            Assert.Contains("计算已完成", fixture.Window.FindControl<TextBlock>("ExternalStatusText")!.Text);
            reader.Release.TrySetResult();
            await fixture.Desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, fixture.Model!.Count);
        });
    }

    [Fact]
    public async Task VisibleSourceSelectionAllowsDestinationRetryWithoutReselectingSource()
    {
        await WithPreflightAsync(false, true, async fixture =>
        {
            await fixture.ConnectAsync();
            fixture.Desktop.IgnoreInputs = 1;
            fixture.Desktop.SelectOnIgnoredInput = true;
            Click(fixture.Window, "ExternalStartButton");
            await fixture.Desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(1, fixture.Desktop.CompletionAttempts);
            Assert.Equal(1, fixture.Desktop.InputCount);
            Assert.Equal(1, fixture.Model!.Count);
        });
    }

    [Fact]
    public async Task NoSelectionResponseCannotBeMistakenForPermissionToReplayInput()
    {
        await WithPreflightAsync(false, true, async fixture =>
        {
            await fixture.ConnectAsync();
            fixture.Desktop.IgnoreInputs = 1;
            Click(fixture.Window, "ExternalStartButton");
            await WaitPreflightAsync(() => fixture.Desktop.Deliveries.Count > 0, fixture.Window);
            await Task.Delay(1900);
            Assert.Equal(0, fixture.Desktop.CompletionAttempts);
            Assert.Single(fixture.Desktop.Deliveries);
            Assert.NotNull(Get<string?>(fixture.Window, "_externalPendingMove"));
        });
    }

    private sealed class DelayedPositionRecognizer : IExternalPositionRecognizer
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SkinRecognition?> ReadAsync(ExternalFrame frame, BoardCalibration geometry,
            bool redToMove, CancellationToken ct, bool detectOrientation, BoardSkin? sessionSkin, string? customPath)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return await new SyntheticPositionRecognizer().ReadAsync(frame, geometry, redToMove, ct, detectOrientation, sessionSkin, customPath);
        }
    }

    private static byte[] PulsePiece(byte[] png, Square square, bool flipped, int brightness)
    {
        using var image = SKBitmap.Decode(png);
        var (x, y) = new BoardCalibration(40, 40, 440, 490, flipped).Point(square);
        for (int cy = (int)y - 17; cy <= (int)y + 17; cy++)
        for (int cx = (int)x - 17; cx <= (int)x + 17; cx++)
        {
            var c = image.GetPixel(cx, cy);
            image.SetPixel(cx, cy, new SKColor((byte)Math.Min(255, c.Red + brightness),
                (byte)Math.Min(255, c.Green + brightness), (byte)Math.Min(255, c.Blue + brightness)));
        }
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}

public class ExternalClickSequenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBetweenDownAndUpAlwaysReleases(bool cancelDuringPress)
    {
        var events = new List<string>(); using var ct = new CancellationTokenSource();
        async Task Delay(int ms, CancellationToken token)
        {
            events.Add($"wait:{ms}");
            if (cancelDuringPress && events.Contains("down")) ct.Cancel();
            token.ThrowIfCancellationRequested(); await Task.Yield();
        }
        var task = ExternalClickSequence.ClickAsync(() => events.Add("move"), () => events.Add("verify"),
            () => events.Add("down"), () => events.Add("up"), ct.Token, Delay);
        if (cancelDuringPress) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else await task;
        Assert.Equal(new[] { "verify", "move", "wait:50", "verify", "down", "wait:60", "up" }, events);
    }

    [Fact]
    public void RetryBudgetRequiresTimeAndConsecutiveEvidenceAndIsBounded()
    {
        var retry = new ExternalInputRetry(); retry.Submitted(TimeSpan.Zero);
        Assert.False(retry.ObserveUnchanged(TimeSpan.FromSeconds(1.5)));
        retry.Invalidate();
        Assert.False(retry.ObserveUnchanged(TimeSpan.FromSeconds(1.7)));
        Assert.True(retry.ObserveUnchanged(TimeSpan.FromSeconds(1.9)));
        retry.Submitted(TimeSpan.FromSeconds(1.9), true);
        Assert.False(retry.ObserveUnchanged(TimeSpan.FromSeconds(3.4)));
        Assert.True(retry.ObserveUnchanged(TimeSpan.FromSeconds(3.6)));
        retry.Submitted(TimeSpan.FromSeconds(3.6), true);
        Assert.False(retry.ObserveUnchanged(TimeSpan.FromSeconds(10)));
        Assert.False(retry.ObserveUnchanged(TimeSpan.FromSeconds(20)));
    }
}
