using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;
using PaddiXiangqi.External;
using PaddiXiangqi.Services;
using SkiaSharp;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectedBoardObservesMovesAndTurnBeforeInputIsArmed(bool flipped)
    {
        await WithPreflightAsync(flipped, false, async fixture =>
        {
            var window = fixture.Window;
            window.FindControl<ComboBox>("ExternalSideBox")!.SelectedIndex = 1;
            await fixture.ConnectAsync();
            Assert.True(Get<bool>(window, "_externalObserving"));
            Assert.True(Get<bool>(window, "_externalLinked"));
            Assert.False(Get<bool>(window, "_externalRunning"));

            fixture.Desktop.Advance("h2e2");
            await WaitPreflightAsync(() => Get<XiangqiGame>(window, "_game").Ply == 1, window);
            Assert.False(Get<XiangqiGame>(window, "_game").RedToMove);
            Assert.Equal(1, window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
            await WaitPreflightAsync(() => Get<object?>(window, "_externalPreparedDecision") is not null, window);
            Assert.Equal(0, fixture.Desktop.InputCount);

            fixture.Desktop.Advance("h9g7");
            await WaitPreflightAsync(() => Get<XiangqiGame>(window, "_game").Ply == 2, window);
            Assert.Equal(fixture.Desktop.Game.CurrentFen(), Get<XiangqiGame>(window, "_game").CurrentFen());
            Assert.True(Get<XiangqiGame>(window, "_game").RedToMove);
            Assert.Equal(0, window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
            Assert.Equal(new[] { "h2e2", "h9g7" }, Get<XiangqiGame>(window, "_game").History.Select(move => move.Uci));
            var currentFen = Get<XiangqiGame>(window, "_game").CurrentFen();
            typeof(MainWindow).GetMethod("NavigateToPly", Private)!.Invoke(window, [0]);
            Assert.Equal(2, Get<XiangqiGame>(window, "_game").Ply);
            Assert.Equal(currentFen, Get<XiangqiGame>(window, "_game").CurrentFen());
            Assert.Equal(0, fixture.Desktop.InputCount);
            Assert.False(Get<bool>(window, "_externalRunning"));
        });
    }

    [Theory]
    [InlineData(.2)]
    [InlineData(.8)]
    public async Task PreparedEngineMoveIsReusedImmediatelyAfterArmingWithoutRecalculating(double seconds)
    {
        await WithPreflightAsync(false, false, async fixture =>
        {
            var window = fixture.Window;
            window.FindControl<NumericUpDown>("OpeningTimeBox")!.Value = (decimal)seconds;
            await fixture.ConnectAsync();
            await WaitPreflightAsync(() => Get<object?>(window, "_externalPreparedDecision") is not null, window);
            var prepared = Get<object>(window, "_externalPreparedDecision");
            Assert.Equal(Get<XiangqiGame>(window, "_game").CurrentFen(), PreparedFen(prepared));
            Assert.Equal(0, fixture.Desktop.InputCount);
            Assert.False(Get<bool>(window, "_externalRunning"));
            var playing = Get<PikafishClient>(window, "_externalPlayingEngine");
            Assert.Equal(1, playing.ProcessStartCount);

            var captures = fixture.Desktop.Captures;
            var clock = Stopwatch.StartNew();
            Click(window, "ExternalStartButton");
            await fixture.Desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(1.5));
            _latencyOutput.WriteLine($"Prepared {seconds:F1} s engine move to first input: {clock.Elapsed.TotalMilliseconds:F1} ms");
            Assert.Equal(PreparedMove(prepared), fixture.Desktop.LastInput);
            Assert.True(fixture.Desktop.CapturesAtInput > captures,
                "A prepared move still requires a fresh target-board check before sending input.");
            Assert.Equal(1, playing.ProcessStartCount);
        });
    }

    [Fact]
    public async Task ModelStartsOnConnectionAndItsPreparedChoiceIsNotRequestedAgainOnStart()
    {
        await WithPreflightAsync(false, true, async fixture =>
        {
            var window = fixture.Window;
            await fixture.ConnectAsync();
            await WaitPreflightAsync(() => Get<object?>(window, "_externalPreparedDecision") is not null, window);
            Assert.Equal(1, fixture.Model!.Count);
            Assert.Equal(0, fixture.Desktop.InputCount);
            var prepared = Get<object>(window, "_externalPreparedDecision");
            var captures = fixture.Desktop.Captures;

            Click(window, "ExternalStartButton");
            await fixture.Desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(1.5));
            Assert.Equal(PreparedMove(prepared), fixture.Desktop.LastInput);
            Assert.Equal(1, fixture.Model.Count);
            Assert.True(fixture.Desktop.CapturesAtInput > captures);
        });
    }

    [Theory]
    [InlineData("model")]
    [InlineData("effort")]
    public async Task PreparedModelChoiceIsInvalidatedWhenUserChangesModelOrReasoning(string change)
    {
        await WithPreflightAsync(false, true, async fixture =>
        {
            var window = fixture.Window;
            await fixture.ConnectAsync();
            await WaitPreflightAsync(() => Get<object?>(window, "_externalPreparedDecision") is not null, window);
            var old = Get<object>(window, "_externalPreparedDecision");
            Assert.Equal(1, fixture.Model!.Count);

            if (change == "model") window.FindControl<ComboBox>("RedActiveModelList")!.SelectedIndex = 1;
            else window.FindControl<ComboBox>("RedLlmReasoningBox")!.SelectedIndex = 3;
            await WaitPreflightAsync(() => Get<object?>(window, "_externalPreparedDecision") is { } current &&
                !ReferenceEquals(current, old), window);
            Assert.Equal(2, fixture.Model.Count);
            var latest = fixture.Model.Requests.Last();
            Assert.Equal(change == "model" ? "test-second" : "test-first", latest.Model);
            Assert.Equal(change == "effort" ? "high" : "low", latest.Effort);
            Assert.Equal(0, fixture.Desktop.InputCount);

            Click(window, "ExternalStartButton");
            await fixture.Desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(1.5));
            Assert.Equal(latest.Move, fixture.Desktop.LastInput);
            Assert.Equal(2, fixture.Model.Count);
        });
    }

    [Fact]
    public async Task ConnectedObservationCancelsStaleModelRequestAndPreparesTheNewPosition()
    {
        await WithPreflightAsync(false, true, async fixture =>
        {
            var window = fixture.Window;
            fixture.Model!.BlockFirst = true;
            await fixture.ConnectAsync();
            await fixture.Model.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Desktop.Advance("e3e4");
            await WaitPreflightAsync(() => Get<XiangqiGame>(window, "_game").Ply == 1, window);
            await fixture.Model.FirstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Null(Get<object?>(window, "_externalPreparedDecision"));
            Assert.Equal(0, fixture.Desktop.InputCount);

            fixture.Desktop.Advance("h9g7");
            await WaitPreflightAsync(() => Get<XiangqiGame>(window, "_game").Ply == 2 &&
                Get<object?>(window, "_externalPreparedDecision") is not null, window);
            var prepared = Get<object>(window, "_externalPreparedDecision");
            Assert.Equal(fixture.Desktop.Game.CurrentFen(), PreparedFen(prepared));
            Assert.Equal(2, fixture.Model.Count);
            Click(window, "ExternalStartButton");
            await fixture.Desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(1.5));
            Assert.Equal(PreparedMove(prepared), fixture.Desktop.LastInput);
            Assert.Equal(2, fixture.Model.Count);
        });
    }

    [Fact]
    public async Task ChangingTheSelectedSideCancelsUnarmedModelWorkAndUsesTheNewSide()
    {
        await WithPreflightAsync(false, true, async fixture =>
        {
            var window = fixture.Window;
            fixture.Model!.BlockFirst = true;
            await fixture.ConnectAsync();
            await fixture.Model.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.FindControl<ComboBox>("ExternalSideBox")!.SelectedIndex = 1;
            await fixture.Model.FirstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, fixture.Desktop.InputCount);
            fixture.Desktop.Advance("e3e4");
            await WaitPreflightAsync(() => Get<XiangqiGame>(window, "_game").Ply == 1 &&
                Get<object?>(window, "_externalPreparedDecision") is not null, window);
            Assert.Equal(2, fixture.Model.Count);
            Assert.Equal("test-second", fixture.Model.Requests.Last().Model);
            Assert.Equal("medium", fixture.Model.Requests.Last().Effort);
            Click(window, "ExternalStartButton");
            await fixture.Desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(1.5));
            Assert.Equal(fixture.Model.Requests.Last().Move, fixture.Desktop.LastInput);
            Assert.Equal(2, fixture.Model.Count);
        });
    }

    [Fact]
    public async Task EditingConnectedBoardCancelsModelAndObservationThenResumesUnarmedOnTheFreshPosition()
    {
        await WithPreflightAsync(false, true, async fixture =>
        {
            var window = fixture.Window;
            fixture.Model!.BlockFirst = true;
            await fixture.ConnectAsync();
            await fixture.Model.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var wasObserving = Get<bool>(window, "_externalObserving");
            Assert.True(wasObserving);
            Set(window, "_externalCalibrating", true);
            await (Task)typeof(MainWindow).GetMethod("PauseExternalObservationForSetupAsync", Private)!.Invoke(window, null)!;
            await fixture.Model.FirstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(Get<bool>(window, "_externalObserving"));
            Assert.False(Get<bool>(window, "_externalRunning"));
            Assert.Null(Get<object?>(window, "_externalPreparedDecision"));

            // The setup owns the image and game while the editor is open. Background
            // observations and the cancelled model must not mutate either one.
            var captures = fixture.Desktop.Captures;
            fixture.Desktop.Advance("e3e4");
            fixture.Desktop.Advance("h9g7");
            await Task.Delay(150);
            Assert.Equal(captures, fixture.Desktop.Captures);
            Assert.Equal(0, Get<XiangqiGame>(window, "_game").Ply);
            Assert.Equal(0, fixture.Desktop.InputCount);

            var fresh = await fixture.Desktop.CaptureAsync(fixture.Desktop.Target, default);
            Set(window, "_externalFrame", fresh);
            var recognize = typeof(MainWindow).GetMethod("RecognizeExternalFrameAsync", Private)!;
            Assert.True(await (Task<bool>)recognize.Invoke(window, [fresh])!);
            Set(window, "_externalCalibrating", false);
            typeof(MainWindow).GetMethod("ResumeExternalObservationAfterSetup", Private)!.Invoke(window, [wasObserving]);
            await WaitPreflightAsync(() => Get<object?>(window, "_externalPreparedDecision") is not null, window);
            Assert.True(Get<bool>(window, "_externalObserving"));
            Assert.False(Get<bool>(window, "_externalRunning"));
            Assert.Equal(0, fixture.Desktop.InputCount);
            Assert.Equal(2, fixture.Model.Count);
            var prepared = Get<object>(window, "_externalPreparedDecision");
            Assert.Equal(fixture.Desktop.Game.CurrentFen(), PreparedFen(prepared));
            Assert.Equal(2, Get<XiangqiGame>(window, "_game").Ply);

            Click(window, "ExternalStartButton");
            await fixture.Desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(1.5));
            Assert.Equal(PreparedMove(prepared), fixture.Desktop.LastInput);
            Assert.Equal(2, fixture.Model.Count);
        });
    }

    [Fact]
    public async Task ManualTurnSelectionBeforeStartStillSynchronizesTheOpponentsFreshMove()
    {
        await WithPreflightAsync(false, true, async fixture =>
        {
            var window = fixture.Window;
            window.FindControl<ComboBox>("ExternalSideBox")!.SelectedIndex = 1;
            await fixture.ConnectAsync();
            Assert.True(Get<XiangqiGame>(window, "_game").RedToMove);
            Assert.Equal(0, fixture.Model!.Count);

            // The user changes the turn while the displayed board is still one move
            // behind. Starting must read the real board and preserve its first move.
            window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex = 1;
            fixture.Desktop.Advance("h2e2");
            Click(window, "ExternalStartButton");
            try { await fixture.Desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException)
            {
                Assert.Fail($"No first input: {window.FindControl<TextBlock>("ExternalStatusText")!.Text}; " +
                    $"observing={Get<bool>(window, "_externalObserving")}, running={Get<bool>(window, "_externalRunning")}, " +
                    $"calibrating={Get<bool>(window, "_externalCalibrating")}, prepared={Get<object?>(window, "_externalPreparedDecision")}, " +
                    $"fen={Get<XiangqiGame>(window, "_game").CurrentFen()}, model calls={fixture.Model.Count}");
            }
            await WaitPreflightAsync(() => Get<XiangqiGame>(window, "_game").Ply == 2, window);
            var game = Get<XiangqiGame>(window, "_game");
            Assert.Equal("h2e2", game.History[0].Uci);
            Assert.Equal(fixture.Desktop.LastInput, game.History[1].Uci);
            Assert.Equal(fixture.Desktop.Game.CurrentFen(), game.CurrentFen());
            Assert.True(game.RedToMove);
            Assert.Equal(0, window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
            Assert.Equal(1, fixture.Model.Count);
            Assert.Equal("test-second", fixture.Model.Requests.Last().Model);
            Assert.Equal("medium", fixture.Model.Requests.Last().Effort);
        });
    }

    private static string PreparedFen(object decision) =>
        (string)decision.GetType().GetProperty("Fen")!.GetValue(decision)!;
    private static string PreparedMove(object decision) =>
        (string)decision.GetType().GetProperty("Move")!.GetValue(decision)!;

    private static async Task WaitPreflightAsync(Func<bool> condition, MainWindow window)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
        Assert.True(condition(), window.FindControl<TextBlock>("ExternalStatusText")!.Text);
    }

    private static async Task WithPreflightAsync(bool flipped, bool model, Func<PreflightFixture, Task> test)
    {
        await Session.Dispatch(async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), $"paddi-preflight-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            var oldPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", Path.Combine(folder, "settings.json"));
            var profile = new LlmServiceProfile
            {
                Id = "isolated-preflight-service", Name = "isolated fixture", BaseUrl = "https://fixture.invalid/v1",
                Models = ["test-first", "test-second"], EnabledModels = ["test-first", "test-second"]
            };
            new AppPreferences
            {
                AutoAnalyze = false, Level = 20, Threads = 1, HashMb = 16, ExternalScoreMode = 2,
                EnginePhases = new() { Enabled = true, OpeningTimeMs = 800 },
                LlmProfilesMigrated = true, LlmServiceDirectoryMigrated = true, LlmServices = [profile],
                RedLlmServiceId = profile.Id, RedLlmModel = "test-first", RedLlmReasoning = "low",
                BlackLlmServiceId = profile.Id, BlackLlmModel = "test-second", BlackLlmReasoning = "medium"
            }.Save();
            var window = new MainWindow();
            var desktop = new PreflightDesktop(flipped);
            var handler = model ? new PreflightModelHandler() : null;
            using var http = handler is null ? null : new HttpClient(handler);
            try
            {
                window.Show();
                window.FindControl<CheckBox>("ExternalAutoSideCheck")!.IsChecked = false;
                window.FindControl<ComboBox>("ExternalSideBox")!.SelectedIndex = 0;
                window.FindControl<ComboBox>("ExternalOrientationBox")!.SelectedIndex = flipped ? 1 : 0;
                window.FindControl<ComboBox>("ExternalSpeedBox")!.SelectedIndex = 0;
                Set(window, "_externalDesktop", desktop); Set(window, "_positionRecognizer", new SyntheticPositionRecognizer());
                var first = await desktop.CaptureAsync(desktop.Target, default);
                Set(window, "_externalFrame", first);
                var geometry = new BoardCalibration(40, 40, 440, 490, flipped);
                Set(window, "_externalCalibration", geometry);
                Set(window, "_externalSessionSkin", BoardSkin.Learn("preflight fixture",
                    BoardObservation.Read(first.Png, geometry), desktop.Game));
                if (model)
                {
                    Set(window, "_llmClient", new LlmChessClient(http!));
                    // Only a dummy in-memory credential is used. The handler never opens a socket.
                    window.FindControl<TextBox>("LlmProfileApiKeyBox")!.Text = "isolated-dummy-key";
                    Get<Dictionary<string, string>>(window, "_llmProfileKeys")[profile.Id] = "isolated-dummy-key";
                    window.FindControl<ComboBox>("ExternalControllerBox")!.SelectedIndex = 1;
                }
                await test(new(window, desktop, handler));
            }
            finally
            {
                Get<CancellationTokenSource?>(window, "_externalCancellation")?.Cancel();
                window.Close();
                await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", oldPath);
                Directory.Delete(folder, true);
            }
            return true;
        }, default);
    }

    private sealed record PreflightFixture(MainWindow Window, PreflightDesktop Desktop, PreflightModelHandler? Model)
    {
        public async Task ConnectAsync()
        {
            var connect = typeof(MainWindow).GetMethod("ConnectExternalAsync", Private)!;
            Assert.True(await (Task<bool>)connect.Invoke(Window, [true])!,
                Window.FindControl<TextBlock>("ExternalStatusText")!.Text);
        }
    }

    private sealed class PreflightDesktop(bool flipped) : IExternalDesktop
    {
        private byte[] _png = ExternalBoardTests.Render(new XiangqiGame(), flipped);
        private byte[]? _scaledSource;
        private byte[]? _scaledPng;
        private int _scaledBackingScale;
        public ExternalWindow Target { get; set; } = new(786, 786, "preflight fixture", 0, 0, 480, 530);
        public int BackingScale { get; set; } = 1;
        public Func<int, CancellationToken, Task>? BeforeCaptureAsync { get; set; }
        public List<(int PixelWidth, int PixelHeight, ExternalWindow Window)> CaptureSizes { get; } = [];
        public List<PreflightInput> Inputs { get; } = [];
        public XiangqiGame Game { get; } = new();
        public int Captures { get; private set; }
        public int InputCount { get; private set; }
        public bool HoldInputs { get; set; }
        public int IgnoreInputs { get; set; }
        public bool SelectOnIgnoredInput { get; set; }
        public bool PulseEveryCapture { get; set; }
        public int CompletionAttempts { get; private set; }
        public bool CanCompleteSelectedMove => true;
        private Square? _selected;
        public async Task CompleteSelectedMoveAsync(ExternalWindow target, double fx, double fy, double tx, double ty, CancellationToken ct)
        {
            CompletionAttempts++;
            Assert.NotNull(_selected);
            _selected = null;
            await MoveAsync(target, fx, fy, tx, ty, ct);
        }
        public bool RejectFirstInput { get; set; }
        public ExternalInputDelivery InputDelivery { get; set; }
        public List<ExternalInputDelivery> Deliveries { get; } = [];
        public int CapturesAtInput { get; private set; }
        public string? LastInput { get; private set; }
        public TaskCompletionSource FirstInput { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool EscapePressed() => false;
        public Task<IReadOnlyList<ExternalWindow>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExternalWindow>>([Target]);
        public async Task<ExternalFrame> CaptureAsync(ExternalWindow target, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Captures++;
            if (BeforeCaptureAsync is { } before) await before(Captures, ct);
            var png = _png;
            if (BackingScale != 1)
            {
                if (!ReferenceEquals(_scaledSource, _png) || _scaledBackingScale != BackingScale)
                {
                    using var original = SKBitmap.Decode(_png);
                    using var scaled = original.Resize(new SKImageInfo(original.Width * BackingScale, original.Height * BackingScale),
                        new SKSamplingOptions(SKFilterMode.Nearest));
                    using var encoded = scaled.Encode(SKEncodedImageFormat.Png, 100);
                    _scaledPng = encoded.ToArray(); _scaledSource = _png; _scaledBackingScale = BackingScale;
                }
                png = _scaledPng!;
            }
            if (_selected is { } selection)
                png = PulsePiece(png, selection, flipped, 20);
            else if (PulseEveryCapture)
                png = PulsePiece(png, new Square(0, 0), flipped, Captures % 2 == 0 ? 7 : 18);
            CaptureSizes.Add((480 * BackingScale, 530 * BackingScale, Target));
            return new ExternalFrame(Target, png);
        }
        public void Advance(string move)
        {
            Assert.True(Game.TryMoveUci(move, out _));
            _png = ExternalBoardTests.Render(Game, flipped);
        }
        public async Task MoveAsync(ExternalWindow target, double fx, double fy, double tx, double ty, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Deliveries.Add(InputDelivery);
            if (RejectFirstInput && Deliveries.Count == 1)
                throw new ExternalInputBlockedException(false, "目标还未激活");
            if (HoldInputs) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            Square Point(double x, double y)
            {
                var file = (int)Math.Round((x - 40) / 50);
                var rank = (int)Math.Round((y - 40) / 50);
                return new Square(flipped ? 8 - file : file, flipped ? 9 - rank : rank);
            }
            if (IgnoreInputs > 0)
            {
                IgnoreInputs--;
                if (SelectOnIgnoredInput) _selected = Point(fx, fy);
                return;
            }
            Assert.True(Game.TryMove(Point(fx, fy), Point(tx, ty), out var move));
            LastInput = move.Uci;
            Inputs.Add(new(move.Uci, fx, fy, tx, ty, BackingScale, Captures));
            _png = ExternalBoardTests.Render(Game, flipped);
            CapturesAtInput = Captures; InputCount++;
            FirstInput.TrySetResult();
        }
    }

    private sealed record PreflightInput(string Move, double FromX, double FromY, double ToX, double ToY,
        int BackingScale, int Captures);

    private sealed record PreflightModelRequest(string Model, string? Effort, string Move);
    private sealed class PreflightModelHandler : HttpMessageHandler
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public bool BlockFirst { get; set; }
        public ConcurrentQueue<PreflightModelRequest> Requests { get; } = new();
        public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("fixture.invalid", request.RequestUri!.Host);
            Assert.Equal("/v1/chat/completions", request.RequestUri.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            var move = root.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters")
                .GetProperty("properties").GetProperty("move").GetProperty("enum")[0].GetString()!;
            var model = root.GetProperty("model").GetString()!;
            var effort = root.TryGetProperty("reasoning_effort", out var reasoning) ? reasoning.GetString() : null;
            Requests.Enqueue(new(model, effort, move));
            var count = Interlocked.Increment(ref _count);
            FirstRequest.TrySetResult();
            if (count == 1 && BlockFirst)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { FirstCancelled.TrySetResult(); throw; }
            }
            var json = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = JsonSerializer.Serialize(new
                    { move, explanation = $"fixture request {count}" }) } } }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
