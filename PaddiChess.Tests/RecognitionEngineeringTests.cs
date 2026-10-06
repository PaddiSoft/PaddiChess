using System.Text.Json;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

[Collection("Local recognition")]
public class RecognitionEngineeringTests
{
    [Fact]
    public async Task UnknownSkinUsesRawCaptureAndExactOcrInputReuse()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ocr-unseen-ivory.png");
        var pixels = CapturedPixels.DecodePng(File.ReadAllBytes(path));
        var geometry = new BoardCalibration(50, 50, 570, 635, false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var read = await LocalGlyphOcr.ReadDetailedAsync(pixels, geometry, timeout.Token);
        Assert.True(read.ReusedCells > 0, "完全相同的 OCR 输入应复用识字结果");
        Assert.InRange(read.InferenceCells, 1, 359);
        Assert.InRange(read.InferenceCells + read.ReusedCells, 90, 360);
        var position = BoardGlyphRecognizer.BuildPosition(pixels, geometry, true, read.Glyphs);
        Assert.True(position.Confident, $"{position.Problem}: {string.Join(',', position.Uncertain)}");
        Assert.Equal(XiangqiGame.InitialFen, position.Fen);
    }

    [Fact]
    public async Task UnknownFlippedPaletteRecognitionDoesNotEncodeRawFrameAsPng()
    {
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ocr-unseen-blue-green.png"));
        var pixels = CapturedPixels.DecodePng(png);
        // A Windows capture row may contain padding. All sampling and OCR must
        // respect its stride, and preserve BGRA channel order.
        int stride = pixels.RowBytes + 32;
        var padded = new byte[stride * pixels.Height];
        for (int y = 0; y < pixels.Height; y++)
            pixels.Bgra.AsSpan(y * pixels.RowBytes, pixels.Width * 4).CopyTo(padded.AsSpan(y * stride));
        var frame = new ExternalFrame(new(1, 2, "fixture", 0, 0, pixels.Width, pixels.Height),
            new CapturedPixels(pixels.Width, pixels.Height, stride, padded));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await BoardGlyphRecognizer.RecognizeAsync(frame, new(50, 50, 570, 635, false), true,
            timeout.Token, detectOrientation: true);
        Assert.Equal(true, result.DetectedRedAtTop);
        Assert.True(result.Confident, $"{result.Problem}: {string.Join(',', result.Uncertain)}");
        Assert.Equal(XiangqiGame.InitialFen, result.Fen);
        Assert.False(frame.PngEncoded);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"Name\":\"broken\",\"Samples\":null}")]
    [InlineData("{\"Name\":\"broken\",\"Samples\":{}}")]
    [InlineData("{invalid}")]
    public void InvalidSkinDataReturnsActionableFormatError(string data)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".skin.json");
        try
        {
            File.WriteAllText(path, data);
            Assert.Throws<FormatException>(() => BoardSkin.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SkinValidationRejectsNullPatchBeforeRecognition()
    {
        var samples = "rnbakcpRNBAKCP\0".ToDictionary(piece => piece, _ => new List<float[]> { new float[1728] });
        samples['r'][0] = null!;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".skin.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new BoardSkin("broken", samples)));
            Assert.Throws<FormatException>(() => BoardSkin.Load(path));
        }
        finally { File.Delete(path); }
    }
}
