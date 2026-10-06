using PaddiXiangqi.External;
using SkiaSharp;

namespace PaddiXiangqi.Tests;

public class FlippedBoardLocatorTests
{
    private const string Expected = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C2B2C1/9/RNBAKA1NR w - - 0 1";

    [Theory]
    [InlineData("jj-red-top-grid.png", 1)]
    [InlineData("jj-red-top-grid.png", .75)]
    [InlineData("jj-red-top-grid.png", .5)]
    [InlineData("jj-red-top-editor.png", 1)]
    [InlineData("jj-red-top-editor.png", .75)]
    [InlineData("jj-red-top-editor.png", .5)]
    public void RealRedOnTopScreenshotsKeepFirstRankAndExcludeToolbar(string fixture, double scale)
    {
        // Cropped from the user's real screenshots. Only the phone game is retained;
        // pixels outside its rounded window are masked. Board and toolbar are unaltered.
        using var source = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        using var resized = source.Resize(new SKImageInfo((int)(source.Width*scale), (int)(source.Height*scale)),
            new SKSamplingOptions(SKFilterMode.Linear));
        using var data = resized.Encode(SKEncodedImageFormat.Png, 100);
        var png = data.ToArray();
        var geometry = BoardLocator.Locate(png);
        Assert.NotNull(geometry);
        // The old detector started at rank two and included the row of circular action buttons.
        Assert.InRange(geometry.Top / scale, 363, 388);
        Assert.InRange(geometry.Bottom / scale, 947, 978);
        Assert.True(BoardLocator.DetectOrientation(png, geometry));
        var recognized = BuiltInBoardSkins.All[0].Recognize(
            BoardObservation.Read(png, geometry with { RedAtTop = true }), true);
        Assert.Equal(Expected, recognized.Fen);
        Assert.Null(recognized.Problem);
        Assert.True(recognized.Uncertain.Count <= 2, string.Join(',', recognized.Uncertain));
    }

    [Fact]
    public void ActualMiddleGameKingsKeepBlackOnTop()
    {
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jj-selected-floating.png"));
        var geometry = BoardLocator.Locate(png);
        Assert.NotNull(geometry);
        Assert.False(BoardLocator.DetectOrientation(png, geometry));
    }
}
