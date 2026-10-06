using System.Diagnostics;
using Avalonia.Controls;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using SkiaSharp;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImmediateReplyWithContinuousGlowUpdatesTurnWithoutManualResync(bool flipped)
    {
        await Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-pulsing-reply-{Guid.NewGuid():N}.json");
            var old = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            var window = new MainWindow();
            var desktop = new PulsingReplyDesktop(flipped);
            try
            {
                window.Show();
                window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked = false;
                window.FindControl<Slider>("LevelSlider")!.Value = 20;
                window.FindControl<CheckBox>("DepthLimitCheck")!.IsChecked = true;
                window.FindControl<NumericUpDown>("DepthBox")!.Value = 2;
                window.FindControl<NumericUpDown>("ThreadsBox")!.Value = 1;
                window.FindControl<NumericUpDown>("ThinkBox")!.Value = 1;
                window.FindControl<CheckBox>("ExternalAutoSideCheck")!.IsChecked = false;
                window.FindControl<ComboBox>("ExternalSideBox")!.SelectedIndex = 0;
                window.FindControl<ComboBox>("ExternalOrientationBox")!.SelectedIndex = flipped ? 1 : 0;
                Set(window, "_externalDesktop", desktop); Set(window, "_positionRecognizer", new SyntheticPositionRecognizer());
                Set(window, "_externalFrame", await desktop.CaptureAsync(desktop.Target, default));
                Set(window, "_externalCalibration", new BoardCalibration(40, 40, 440, 490, flipped));
                Set(window, "_externalPositionReady", true);
                Click(window, "ExternalStartButton");
                await desktop.ReplyApplied.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var game = Get<XiangqiGame>(window, "_game");
                var clock = Stopwatch.StartNew();
                while (game.Ply < 2 && clock.ElapsedMilliseconds < 1000) await Task.Delay(10);
                _latencyOutput.WriteLine($"Immediate glowing reply confirmed: {Stopwatch.GetElapsedTime(desktop.ReplyTimestamp).TotalMilliseconds:F1} ms; flipped={flipped}");
                Assert.Equal(2, game.Ply);
                Assert.Equal(desktop.Game.CurrentFen(), game.CurrentFen());
                Assert.True(game.RedToMove);
                Assert.Equal(0, window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
                Assert.Null(Get<string?>(window, "_externalPendingMove"));
                Assert.Equal(1, desktop.InputCount);
                Assert.Empty(window.OwnedWindows);
                Click(window, "ExternalStopButton");
                await Get<Task>(window, "_externalTask");
                Assert.True(Get<bool>(window, "_externalLinked"));
            }
            finally
            {
                Get<CancellationTokenSource?>(window, "_externalCancellation")?.Cancel();
                window.Close(); await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", old);
                if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, default);
    }

    private sealed class PulsingReplyDesktop(bool flipped) : IExternalDesktop
    {
        public ExternalWindow Target { get; } = new(79, 79, "immediate pulsing opponent", 0, 0, 480, 530);
        public XiangqiGame Game { get; } = new();
        public TaskCompletionSource ReplyApplied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long ReplyTimestamp { get; private set; }
        public int InputCount { get; private set; }
        private Square? _glowingSquare;
        private int _captures;
        public bool EscapePressed() => false;
        public Task<IReadOnlyList<ExternalWindow>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ExternalWindow>>([Target]);
        public async Task<ExternalFrame> CaptureAsync(ExternalWindow target, CancellationToken ct)
        {
            await Task.Delay(35, ct); // Capture costs time, while the remote game continues.
            var png = ExternalBoardTests.Render(Game, flipped);
            if (_glowingSquare is { } square && ++_captures % 2 == 0)
            {
                using var image = SKBitmap.Decode(png);
                var (x, y) = new BoardCalibration(40, 40, 440, 490, flipped).Point(square);
                for (int cy = (int)y - 17; cy <= (int)y + 17; cy++)
                for (int cx = (int)x - 17; cx <= (int)x + 17; cx++)
                {
                    var c = image.GetPixel(cx, cy);
                    image.SetPixel(cx, cy, new SKColor((byte)Math.Min(255, c.Red + 14),
                        (byte)Math.Min(255, c.Green + 14), (byte)Math.Min(255, c.Blue + 14)));
                }
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                png = encoded.ToArray();
            }
            return new(Target, png);
        }
        public async Task MoveAsync(ExternalWindow target, double fx, double fy, double tx, double ty, CancellationToken ct)
        {
            InputCount++;
            Square Point(double x, double y)
            {
                int f = (int)Math.Round((x - 40) / 50), r = (int)Math.Round((y - 40) / 50);
                return flipped ? new(8 - f, 9 - r) : new(f, r);
            }
            Assert.True(Game.TryMove(Point(fx, fy), Point(tx, ty), out _));
            await Task.Delay(20, ct);
            Assert.True(Game.TryMoveUci(Game.AllLegalMoves()[0].Uci, out var reply));
            _glowingSquare = reply.To;
            ReplyTimestamp = Stopwatch.GetTimestamp(); ReplyApplied.TrySetResult();
            await Task.Delay(1500, ct);
        }
    }
}
