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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task IndependentVerificationFinishesDuringThinkingBeforeInputIsArmed(int delivery)
    {
        await WithPreflightAsync(false, true, async fixture =>
        {
            fixture.Model!.BlockFirst = true;
            fixture.Window.FindControl<ComboBox>("ExternalDeliveryBox")!.SelectedIndex = delivery;
            var reader = new DelayedPositionRecognizer();
            Set(fixture.Window, "_positionRecognizer", reader);
            await fixture.ConnectAsync();
            await fixture.Model.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // No move exists yet, and connection alone must never send input.
            await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Null(Get<object?>(fixture.Window, "_externalPreparedDecision"));
            Assert.Equal(0, fixture.Desktop.InputCount);
            reader.Release.TrySetResult();
            await reader.Completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.Model.ReleaseFirst.TrySetResult();
            await WaitPreflightAsync(() => Get<object?>(fixture.Window, "_externalPreparedDecision") != null, fixture.Window);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Click(fixture.Window, "ExternalStartButton");
            await fixture.Desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(1.5));
            _latencyOutput.WriteLine($"Preverified model decision to input: {clock.Elapsed.TotalMilliseconds:F1} ms");
            Assert.Equal(1, reader.Verifications);
            Assert.Equal(1, fixture.Model.Count);
        });
    }

    [Fact]
    public async Task OptionalInputWarmupCannotDelayAReadyMoveAndIsCancelledOnStop()
    {
        await WithPreflightAsync(false, true, async fixture =>
        {
            fixture.Desktop.HoldInputPreparation = true;
            await fixture.ConnectAsync();
            await fixture.Desktop.PreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitPreflightAsync(() => Get<object?>(fixture.Window, "_externalPreparedDecision") != null, fixture.Window);
            Assert.Equal(0, fixture.Desktop.InputCount);
            Click(fixture.Window, "ExternalStartButton");
            await fixture.Desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(1.5));
            Assert.False(fixture.Desktop.PreparationCancelled.Task.IsCompleted);
            Get<CancellationTokenSource>(fixture.Window, "_externalCancellation").Cancel();
            await fixture.Desktop.PreparationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        });
    }

    [Fact]
    public async Task SlowPreInputRecognitionDoesNotBlockCaptureOrRepeatEngineCalculation()
    {
        await WithPreflightAsync(false, true, async fixture =>
        {
            var reader = new DelayedPositionRecognizer();
            Set(fixture.Window, "_positionRecognizer", reader);
            await fixture.ConnectAsync();
            await WaitPreflightAsync(() => Get<object?>(fixture.Window, "_externalPreparedDecision") != null, fixture.Window);
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StalledVerificationRetriesAndSendsThePreparedMoveWithoutManualPlay(int delivery)
    {
        await WithPreflightAsync(false, true, async fixture =>
        {
            fixture.Window.FindControl<ComboBox>("ExternalDeliveryBox")!.SelectedIndex = delivery;
            var reader = new FirstVerificationStallsRecognizer();
            Set(fixture.Window, "_positionRecognizer", reader);
            await fixture.ConnectAsync();
            await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitPreflightAsync(() => Get<object?>(fixture.Window, "_externalPreparedDecision") != null, fixture.Window);
            Click(fixture.Window, "ExternalStartButton");
            await reader.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, fixture.Model!.Count);
            Assert.Equal(1, fixture.Desktop.InputCount);
            Assert.True(reader.Verifications >= 2);
        });
    }

    private sealed class FirstVerificationStallsRecognizer : IExternalPositionRecognizer
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Verifications;
        public async Task<SkinRecognition?> ReadAsync(ExternalFrame frame, BoardCalibration geometry,
            bool redToMove, CancellationToken ct, bool detectOrientation, BoardSkin? sessionSkin, string? customPath)
        {
            if (!detectOrientation && Interlocked.Increment(ref Verifications) == 1)
            {
                Started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                finally { Cancelled.TrySetResult(); }
            }
            return await new SyntheticPositionRecognizer().ReadAsync(frame, geometry, redToMove, ct,
                detectOrientation, sessionSkin, customPath);
        }
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
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Verifications { get; private set; }
        public async Task<SkinRecognition?> ReadAsync(ExternalFrame frame, BoardCalibration geometry,
            bool redToMove, CancellationToken ct, bool detectOrientation, BoardSkin? sessionSkin, string? customPath)
        {
            if (!detectOrientation)
            {
                Verifications++;
                Started.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            var result = await new SyntheticPositionRecognizer().ReadAsync(frame, geometry, redToMove, ct, detectOrientation, sessionSkin, customPath);
            if (!detectOrientation) Completed.TrySetResult();
            return result;
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
