using Avalonia.Controls;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using PaddiXiangqi.Sessions;
using SkiaSharp;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task ReviewedCorrectionStillSendsMateAfterEighteenObservedPlies(bool flipped, int delivery)
    {
        const string fen = "9/4c1P2/3kb4/2C6/3P5/9/9/4BA3/4pnppc/2BKnrrpp w - - 0 1";
        await WithPreflightAsync(flipped, true, async fixture =>
        {
            var window = fixture.Window;
            window.FindControl<CheckBox>("ExternalAutoTurnCheck")!.IsChecked = false;
            window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex = 0;
            window.FindControl<ComboBox>("ExternalDeliveryBox")!.SelectedIndex = delivery;
            var editing = (Task)typeof(MainWindow).GetMethod("ShowExternalPositionEditorAsync", Private)!
                .Invoke(window, [fen.Replace("rrpp", "rrpr"), "模拟模型误读", new[] { new Square(8, 9) }, false])!;
            var editor = window.OwnedWindows.OfType<ExternalPositionEditorWindow>().Single();
            editor.Editor.SelectPiece('p'); editor.Editor.Click(new(8, 9));
            editor.FindControl<Button>("ExternalEditorAccept")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await editing.WaitAsync(TimeSpan.FromSeconds(3));
            Set(window, "_positionRecognizer", new WrongPawnPositionRecognizer());
            fixture.Model!.PreferredMove = "g8f8";
            fixture.Desktop.TransformFrame = frame => ManualPositionEvidenceTests.Tint(frame, 3);
            typeof(MainWindow).GetMethod("BeginExternalObservation", Private)!.Invoke(window, [false]);
            string[] moves = ["d5d6", "d7d8", "d6d7", "d8d9", "d7d8", "d9e9", "d8e8", "e9e8",
                "c6e6", "e7c9", "e6e1", "c9e7", "e2g4", "e7c9", "c0e2", "c9e7", "g4i2", "e8e9"];
            for (var i = 0; i < moves.Length; i++)
            {
                fixture.Desktop.Advance(moves[i]);
                await WaitPreflightAsync(() => Get<XiangqiGame>(window, "_game").Ply == i + 1, window);
            }
            Assert.Equal(moves, Get<XiangqiGame>(window, "_game").History.Select(move => move.Uci));
            await WaitPreflightAsync(() => Get<object?>(window, "_externalPreparedDecision") != null, window);
            Assert.Equal("g8f8", PreparedMove(Get<object>(window, "_externalPreparedDecision")));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Click(window, "ExternalStartButton");
            await fixture.Desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(1.5));
            _latencyOutput.WriteLine($"18 plies / flipped={flipped} / delivery={delivery}: {clock.Elapsed.TotalMilliseconds:F1} ms to mate input");
            Assert.Equal("g8f8", fixture.Desktop.LastInput);
            Assert.Equal(1, fixture.Desktop.InputCount);
        }, initialFen: fen);
    }

    [Fact]
    public async Task AdoptedEditorCorrectionSurvivesRepeatedModelErrorAndAllowsInput()
    {
        const string fen = "9/4c1P2/3kb4/2C6/3P5/9/9/4BA3/4pnppc/2BKnrrpp w - - 0 1";
        await WithPreflightAsync(false, true, async fixture =>
        {
            fixture.Window.FindControl<CheckBox>("ExternalAutoTurnCheck")!.IsChecked = false;
            fixture.Window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex = 0;
            var editing = (Task)typeof(MainWindow).GetMethod("ShowExternalPositionEditorAsync", Private)!
                .Invoke(fixture.Window, [fen.Replace("rrpp", "rrpr"), "模拟独立模型误读", new[] { new Square(8, 9) }, false])!;
            var editor = fixture.Window.OwnedWindows.OfType<ExternalPositionEditorWindow>().Single();
            editor.Editor.SelectPiece('p'); editor.Editor.Click(new(8, 9));
            editor.FindControl<Button>("ExternalEditorAccept")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await editing.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotNull(Get<ExternalManualPosition?>(fixture.Window, "_externalManualPosition"));
            Set(fixture.Window, "_positionRecognizer", new WrongPawnPositionRecognizer());
            typeof(MainWindow).GetMethod("BeginExternalObservation", Private)!.Invoke(fixture.Window, [false]);
            await WaitPreflightAsync(() => Get<object?>(fixture.Window, "_externalPreparedDecision") != null, fixture.Window);
            Click(fixture.Window, "ExternalStartButton");
            await fixture.Desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(1.5));
            Assert.Equal(1, fixture.Model!.Count); Assert.Equal(1, fixture.Desktop.InputCount);
        }, initialFen: fen);
    }

    private sealed class WrongPawnPositionRecognizer : IExternalPositionRecognizer
    {
        public async Task<SkinRecognition?> ReadAsync(ExternalFrame frame, BoardCalibration geometry,
            bool redToMove, CancellationToken ct, bool detectOrientation, BoardSkin? sessionSkin, string? customPath)
        {
            var read = await new SyntheticPositionRecognizer().ReadAsync(frame, geometry, redToMove, ct, false, null, null);
            return read! with { Fen = read.Fen.Replace("rrpp", "rrpr"),
                Uncertain = [new(5, 9), new(6, 9), new(8, 9)], Problem = "黑车超过允许数量 2。" };
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task SparsePositionWithChangingEmptyPixelsSendsPreparedMovePromptly(bool flipped, int delivery)
    {
        const string fen = "4k4/3PaP3/9/9/9/9/9/5K3/9/9 w - - 0 1";
        await WithPreflightAsync(flipped, true, async fixture =>
        {
            fixture.Window.FindControl<ComboBox>("ExternalDeliveryBox")!.SelectedIndex = delivery;
            // Import a manually confirmed puzzle: its orientation/turn cannot
            // be inferred from the distribution of five remaining pieces.
            fixture.Window.FindControl<CheckBox>("ExternalAutoTurnCheck")!.IsChecked = false;
            fixture.Window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex = 0;
            await (Task)typeof(MainWindow).GetMethod("ApplyExternalPositionAsync", Private)!
                .Invoke(fixture.Window, [fen, CancellationToken.None])!;
            typeof(MainWindow).GetMethod("BeginExternalObservation", Private)!.Invoke(fixture.Window, [false]);
            await WaitPreflightAsync(() => Get<object?>(fixture.Window, "_externalPreparedDecision") != null, fixture.Window);
            // None of this position's piece types can legally occupy b1. The
            // old prefilter therefore left only "empty", which failed the
            // runner-up check on every fresh frame despite a correct full read.
            fixture.Desktop.PulseSquare = new Square(1, 8);
            fixture.Desktop.PulseEveryCapture = true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Click(fixture.Window, "ExternalStartButton");
            await fixture.Desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(1.5));
            _latencyOutput.WriteLine($"Sparse board / flipped={flipped} / delivery={delivery}: {clock.Elapsed.TotalMilliseconds:F1} ms to input");
            Assert.Equal(1, fixture.Model!.Count);
            Assert.Equal(1, fixture.Desktop.InputCount);
        }, initialFen: fen);
    }

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
