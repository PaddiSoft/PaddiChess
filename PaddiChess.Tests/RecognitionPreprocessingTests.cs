using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public class RecognitionPreprocessingTests
{
    // Numerical oracle retained from the released scalar implementation. Performance
    // changes must preserve every model input bit, including interpolation and borders.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OptimizedWarpPreservesModelInputExactly(bool flipped, bool nearBorder)
    {
        const int width = 593, height = 647, stride = width * 4 + 28;
        var data = new byte[stride * height];
        new Random(20261021).NextBytes(data);
        var pixels = new CapturedPixels(width, height, stride, data);
        var grid = nearBorder ? new BoardCalibration(1.7, 2.3, 589.2, 643.1, flipped)
            : new BoardCalibration(43.3, 39.8, 542.6, 590.2, flipped);
        var expected = ReferencePreprocess(pixels, grid, default);
        var actual = LocalBoardClassifier.Preprocess(pixels, grid, default);
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(expected.AsSpan().SequenceEqual(actual), "Preprocessing changed the model input");
    }

    private static float[] ReferencePreprocess(CapturedPixels pixels, BoardCalibration geometry, CancellationToken ct)
    {
        var crop = new byte[400 * 450 * 3];
        var sx = (geometry.Right - geometry.Left) / 350;
        var sy = (geometry.Bottom - geometry.Top) / 400;
        double originX = geometry.Left, originY = geometry.Top;
        if (geometry.RedAtTop) { originX = geometry.Right; originY = geometry.Bottom; sx = -sx; sy = -sy; }
        for (var y = 0; y < 450; y++)
        {
            ct.ThrowIfCancellationRequested();
            var py = Math.Round((originY + (y - 25) * sy) * 32) / 32;
            var iy = (int)Math.Floor(py); var fy = py - iy;
            for (var x = 0; x < 400; x++)
            {
                var px = Math.Round((originX + (x - 25) * sx) * 32) / 32;
                var ix = (int)Math.Floor(px); var fx = px - ix;
                for (var c = 0; c < 3; c++)
                {
                    byte Pixel(int xx, int yy) => xx < 0 || yy < 0 || xx >= pixels.Width || yy >= pixels.Height
                        ? (byte)0 : pixels.Bgra[yy * pixels.RowBytes + xx * 4 + 2 - c];
                    var top = Pixel(ix, iy) * (1 - fx) + Pixel(ix + 1, iy) * fx;
                    var bottom = Pixel(ix, iy + 1) * (1 - fx) + Pixel(ix + 1, iy + 1) * fx;
                    crop[(y * 400 + x) * 3 + c] = (byte)Math.Clamp(Math.Floor(top * (1 - fy) + bottom * fy + .5), 0, 255);
                }
            }
        }
        var input = new float[3 * 315 * 280];
        double[] mean = [123.675, 116.28, 103.53], std = [58.395, 57.12, 57.375];
        for (var y = 0; y < 315; y++)
        {
            ct.ThrowIfCancellationRequested();
            var py = (y + .5) * 450 / 315 - .5; var iy = (int)py; var fy = py - iy;
            for (var x = 0; x < 280; x++)
            {
                var px = (x + .5) * 400 / 280 - .5; var ix = (int)px; var fx = px - ix;
                for (var c = 0; c < 3; c++)
                {
                    var top = crop[(iy * 400 + ix) * 3 + c] * (1 - fx) + crop[(iy * 400 + ix + 1) * 3 + c] * fx;
                    var bottom = crop[((iy + 1) * 400 + ix) * 3 + c] * (1 - fx) + crop[((iy + 1) * 400 + ix + 1) * 3 + c] * fx;
                    var value = Math.Floor(top * (1 - fy) + bottom * fy + .5);
                    input[c * 315 * 280 + y * 280 + x] = (float)((value - mean[c]) / std[c]);
                }
            }
        }
        return input;
    }
}
