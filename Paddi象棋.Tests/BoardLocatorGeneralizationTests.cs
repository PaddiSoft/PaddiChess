using PaddiXiangqi.External;
using SkiaSharp;
using Xunit.Abstractions;

namespace PaddiXiangqi.Tests;

public class BoardLocatorGeneralizationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)]
    [InlineData(.75)]
    [InlineData(.5)]
    public void LocalizedPieceEdgesCannotOutscoreTheCompleteGrid(double scale)
    {
        var geometry = LocateFixture("jj-rook-capture-before.png", scale, false);
        // The rejected candidate covered only x134..584 / y151..650. Its
        // colourful pieces had a higher mean response than the actual grid.
        Assert.InRange(geometry.Left / scale, 42, 61);
        Assert.InRange(geometry.Right / scale, 714, 734);
        Assert.InRange(geometry.Top / scale, 48, 67);
        Assert.InRange(geometry.Bottom / scale, 777, 800);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(.75, false)]
    [InlineData(.5, false)]
    [InlineData(.75, true)]
    public void UnregisteredTexturedBoardUsesCompleteGridAcrossPolarityAndScale(double scale, bool invert)
    {
        var geometry = LocateFixture("jj-silver-blue-midgame.png", scale, invert);
        // Reference the visible grid intersections, not the raised glyph centres.
        // The screenshot has slight perspective: left 43–49 and right 719–726.
        Assert.InRange(geometry.Left / scale, 39, 54);
        Assert.InRange(geometry.Right / scale, 714, 733);
        Assert.InRange(geometry.Top / scale, 53, 66);
        Assert.InRange(geometry.Bottom / scale, 780, 796);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(.75)]
    [InlineData(.5)]
    public void FullWindowKeepsTenRanksAboveTheCircularToolbar(double scale)
    {
        // Same full capture dimensions, board and controls; only the two avatars
        // outside the board are masked in the public test fixture.
        var geometry = LocateFixture("jj-silver-blue-full-window.png", scale, false);
        Assert.InRange(geometry.Left / scale, 39, 59);
        Assert.InRange(geometry.Right / scale, 714, 737);
        Assert.InRange(geometry.Top / scale, 400, 422);
        Assert.InRange(geometry.Bottom / scale, 1133, 1155);
    }

    private BoardCalibration LocateFixture(string file, double scale, bool invert)
    {
        using var original = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        if (invert)
        {
            var colours = original.Pixels;
            for (int i = 0; i < colours.Length; i++)
            {
                var p = colours[i];
                colours[i] = new((byte)(255 - p.Red), (byte)(255 - p.Green), (byte)(255 - p.Blue), p.Alpha);
            }
            original.Pixels = colours;
        }
        using var image = original.Resize(new SKImageInfo((int)(original.Width * scale), (int)(original.Height * scale)),
            new SKSamplingOptions(SKFilterMode.Linear));
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var geometry = BoardLocator.Locate(encoded.ToArray());
        Assert.NotNull(geometry);
        output.WriteLine($"{file} / {scale} / inverted={invert}: {geometry}");
        return geometry;
    }
}
