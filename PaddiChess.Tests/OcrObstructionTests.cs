using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using SkiaSharp;

namespace PaddiXiangqi.Tests;

public class OcrObstructionTests
{
    [Theory]
    [InlineData(200, 200, 200)]
    [InlineData(230, 193, 126)]
    public async Task OpaqueOcclusionCannotTurnAnUnreadableHorseIntoAConfirmedEmptySquare(byte red, byte green, byte blue)
    {
        // Synthetic obstruction on a real public-site board: only b9 is covered.
        // The second colour resembles the board, so rejection cannot depend on
        // recognizing a special grey mask. This invokes the complete OCR model.
        using var image = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, "Fixtures", "playxiangqi-opening.png"));
        using (var canvas = new SKCanvas(image))
        using (var paint = new SKPaint { Color = new SKColor(red, green, blue) })
            canvas.DrawRect(SKRect.Create(105, 5, 100, 100), paint);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var read = await BoardGlyphRecognizer.RecognizeAsync(encoded.ToArray(), new(55, 55, 855, 955, false), true, timeout.Token);
        Assert.False(read.Confident);
        Assert.Contains(new Square(1, 0), read.Uncertain);
    }
}
