using System.Diagnostics;
using System.Text.Json;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using SkiaSharp;

namespace PaddiXiangqi.Tests;

public class ObservationSamplingTests
{
    private static readonly BoardCalibration Geometry = new(40, 40, 440, 490, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DirectPixelSamplingMatchesNativeGetPixelForEverySample(bool flipped, bool transparent)
    {
        var bytes = ExternalBoardTests.Render(new XiangqiGame(), flipped);
        if (transparent)
        {
            using var source = SKBitmap.Decode(bytes);
            using var image = new SKBitmap(source.Width, source.Height);
            using var canvas = new SKCanvas(image);
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha(127) };
            canvas.DrawBitmap(source, 0, 0, paint);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            bytes = png.ToArray();
        }
        var geometry = Geometry with { RedAtTop = flipped };
        var old = ReadWithNativePixelCalls(bytes, geometry);
        var current = BoardObservation.Read(bytes, geometry);
        for (int cell = 0; cell < 90; cell++) Assert.Equal(old[cell], current.Cells[cell]);
    }

    [Fact]
    public void ReusedCellsDoNotHideOpponentReplyOrMutateConfirmedPosition()
    {
        var game = new XiangqiGame();
        var before = BoardObservation.Read(ExternalBoardTests.Render(game), Geometry);
        var tracker = new ExternalBoardTracker(before, game);
        Assert.True(game.TryMoveUci("h2e2", out _));
        var ours = BoardObservation.Read(ExternalBoardTests.Render(game), Geometry, before);
        Assert.Same(before.Cells[0], ours.Cells[0]);
        Assert.NotSame(before.Cells[7 * 9 + 7], ours.Cells[7 * 9 + 7]);
        Assert.True(game.TryMoveUci("h9g7", out _));
        var reply = BoardObservation.Read(ExternalBoardTests.Render(game), Geometry, ours);
        Assert.NotSame(ours.Cells[7], reply.Cells[7]);
        Assert.Same(before.Cells[7], ours.Cells[7]);
        game.GoToPly(0);
        var match = tracker.Match(reply, game, "h2e2", recoverMissedPair: true);
        Assert.True(match.Recognized, match.Message);
        Assert.Equal(new[] { "h2e2", "h9g7" }, match.Moves);
    }

    [Fact]
    public void RealBoardSamplingBenchmarkAndExactCellReuseAllocation()
    {
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets/BoardSkins/jj-classic.png"));
        var geometry = BuiltInBoardSkins.JjReferenceGeometry;
        var before = BoardObservation.Read(png, geometry);
        const int count = 50;
        (double Ms, long Bytes) Measure(Action work)
        {
            for (int i = 0; i < 4; i++) work();
            long allocation = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < count; i++) work();
            timer.Stop();
            return (timer.Elapsed.TotalMilliseconds / count,
                (GC.GetAllocatedBytesForCurrentThread() - allocation) / count);
        }
        var original = Measure(() => ReadWithNativePixelCalls(png, geometry));
        var direct = Measure(() => BoardObservation.Read(png, geometry));
        var reuse = Measure(() => BoardObservation.Read(png, geometry, before));
        Assert.True(reuse.Bytes < direct.Bytes / 3, $"复用 {reuse.Bytes} B；完整采样 {direct.Bytes} B");
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/performance"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "observation-sampling-2026-10-04.json"), JsonSerializer.Serialize(new
        {
            fixture = "jj-classic.png", before.Width, before.Height, iterations = count,
            originalMs = original.Ms, directMs = direct.Ms, reuseMs = reuse.Ms,
            originalAllocatedBytes = original.Bytes, directAllocatedBytes = direct.Bytes,
            reuseAllocatedBytes = reuse.Bytes, pixelParityVerified = true,
            scope = "PNG decode and 90-cell sampling; excludes OS capture, search and input"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static float[][] ReadWithNativePixelCalls(byte[] png, BoardCalibration geometry)
    {
        using var image = SKBitmap.Decode(png);
        double dx = (geometry.Right - geometry.Left) / 8, dy = (geometry.Bottom - geometry.Top) / 9;
        var cells = new float[90][];
        for (int r = 0; r < 10; r++) for (int f = 0; f < 9; f++)
        {
            var (cx, cy) = geometry.Point(new Square(f, r));
            var values = cells[r * 9 + f] = new float[24 * 24 * 3];
            for (int y = 0; y < 24; y++) for (int x = 0; x < 24; x++)
            {
                if ((x - 11.5) * (x - 11.5) + (y - 11.5) * (y - 11.5) > 121) continue;
                var color = image.GetPixel((int)Math.Round(cx + (x - 11.5) / 24 * dx * .66),
                    Math.Clamp((int)Math.Round(cy + (y - 11.5) / 24 * dy * .66), 0, image.Height - 1));
                int i = (y * 24 + x) * 3;
                values[i] = color.Red / 255f; values[i + 1] = color.Green / 255f; values[i + 2] = color.Blue / 255f;
            }
        }
        return cells;
    }
}
