using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using SkiaSharp;

namespace PaddiXiangqi.Tests;

public class PlayXiangqiRecognitionTests
{
    [Theory]
    [InlineData(1.0, false)]
    [InlineData(.75, false)]
    [InlineData(.5, false)]
    [InlineData(1.0, true)]
    public async Task RealWebsiteGlyphsIncludeRareRooksAndNoRiverPawn(double scale, bool flipped)
    {
        using var original = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, "Fixtures", "playxiangqi-opening.png"));
        using var image = new SKBitmap((int)(original.Width * scale), (int)(original.Height * scale));
        using (var canvas = new SKCanvas(image))
        {
            canvas.Scale((float)scale);
            // Reverse positions, not the glyphs themselves, just as the website's
            // orientation toggle does. A 180-degree pixel rotation would change OCR.
            if (!flipped) canvas.DrawBitmap(original, 0, 0);
            else
            {
                canvas.DrawBitmap(original, 0, 0);
                var opening = new XiangqiGame();
                for (int r = 0; r < 10; r++) for (int f = 0; f < 9; f++)
                {
                    if (opening.Board[r, f] == '\0') continue;
                    var source = SKRect.Create(5 + f * 100, 5 + r * 100, 100, 100);
                    var target = SKRect.Create(5 + (8 - f) * 100, 5 + (9 - r) * 100, 100, 100);
                    // Opening occupancy is symmetric. Move the discs while
                    // retaining the real board beneath them: swapping whole
                    // tiles makes border arms face out and reverses river gaps.
                    using var disc = new SKPath();
                    disc.AddCircle(target.MidX, target.MidY, 48);
                    canvas.Save(); canvas.ClipPath(disc);
                    canvas.DrawBitmap(original, source, target);
                    canvas.Restore();
                }
            }
        }
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var frame = new ExternalFrame(new(1, 1, "PlayXiangqi fixture", 0, 0, image.Width, image.Height),
            CapturedPixels.DecodePng(encoded.ToArray()));
        var geometry = new BoardCalibration(55 * scale, 55 * scale, 855 * scale, 955 * scale, flipped);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await BoardGlyphRecognizer.RecognizeAsync(frame, geometry, true, timeout.Token, detectOrientation: true);
        Assert.Equal(flipped, result.DetectedRedAtTop);
        string diagnostic = "";
        if (!result.Confident)
        {
            var glyphs = await LocalGlyphOcr.ReadAsync(frame.Pixels!, geometry, timeout.Token);
            diagnostic = string.Join("; ", result.Uncertain.Select(square =>
            {
                int i = square.Rank * 9 + square.File; if (flipped) i = 89 - i;
                return $"{square.Uci} {glyphs[i]}";
            }));
        }
        Assert.True(result.Confident, $"{result.Problem}; {diagnostic}; {result.Fen}");
        Assert.Equal(XiangqiGame.InitialFen, result.Fen);
        Assert.False(frame.PngEncoded);
    }

    [Fact]
    public void LowConfidenceRiverTextIsNotPublishedAsSixthPawn()
    {
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "playxiangqi-opening.png"));
        var glyphs = OpeningGlyphs();
        glyphs[5 * 9 + 6] = new("卒", .37);
        var read = BoardGlyphRecognizer.BuildPosition(png, new(55, 55, 855, 955, false), true, glyphs);
        Assert.True(read.Confident, $"{read.Problem}; {string.Join(',', read.Uncertain)}");
        Assert.Equal(XiangqiGame.InitialFen, read.Fen);
    }
    [Fact]
    public void GridMisreadAsAdvisorDoesNotBecomeAPieceButAnUnreadableDiscStaysUncertain()
    {
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "playxiangqi-opening.png"));
        var glyphs = OpeningGlyphs();
        // The intersection adjoining the river can produce a strong 士 guess.
        // The advisor's actual home disc has its character obscured instead.
        glyphs[4 * 9 + 1] = new("士", .99);
        glyphs[3] = new("", 0);
        var read = BoardGlyphRecognizer.BuildPosition(png, new(55, 55, 855, 955, false), true, glyphs);
        Assert.False(read.Confident);
        Assert.Equal([new Square(3, 0)], read.Uncertain);
        Assert.DoesNotContain(new Square(1, 4), read.Uncertain);
    }

    [Theory]
    [InlineData("俥", .45)]
    [InlineData("", 0)]
    public void TransparentPieceWithWeakOrMissingGlyphCannotSilentlyDisappear(string text, double confidence)
    {
        // Same actual glyph outlines with the circle/face removed. Reliable
        // kings still make a superficially valid FEN if the weak rook is lost.
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "playxiangqi-transparent.png"));
        var glyphs = OpeningGlyphs();
        glyphs[9 * 9] = new(text, confidence);
        var read = BoardGlyphRecognizer.BuildPosition(png, new(55, 55, 855, 955, false), true, glyphs);
        Assert.False(read.Confident);
        Assert.Contains(new Square(0, 9), read.Uncertain);
        Assert.DoesNotContain(new Square(4, 0), read.Uncertain);
        Assert.DoesNotContain(new Square(4, 9), read.Uncertain);
        Assert.Contains('k', read.Fen); Assert.Contains('K', read.Fen);
    }

    [Fact]
    public void SparseDiscEndgameStillSeparatesRiverLettersFromUnrecognizedPieces()
    {
        const string fen = "4k4/9/9/9/4P4/9/9/9/9/R3K4 w - - 0 1";
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "playxiangqi-sparse.png"));
        var glyphs = GlyphsFor(fen);
        glyphs[4 * 9 + 1] = new("士", .99);
        glyphs[5 * 9 + 6] = new("卒", .37);
        var read = BoardGlyphRecognizer.BuildPosition(png, new(55, 55, 855, 955, false), true, glyphs);
        Assert.True(read.Confident, $"{read.Problem}; {string.Join(',', read.Uncertain)}");
        Assert.Equal(fen, read.Fen);
    }

    private static LocalGlyphOcr.Glyph[] OpeningGlyphs() => GlyphsFor(XiangqiGame.InitialFen);

    private static LocalGlyphOcr.Glyph[] GlyphsFor(string fen)
    {
        var game = new XiangqiGame(); game.LoadFen(fen);
        return Enumerable.Range(0, 90).Select(i =>
        {
            char p = game.Board[i / 9, i % 9]; int type = "RNBAKCP".IndexOf(char.ToUpperInvariant(p));
            return new LocalGlyphOcr.Glyph(type < 0 ? "" : (char.IsUpper(p) ? "俥傌相仕帥炮兵" : "車馬象士將砲卒")[type].ToString(), type < 0 ? 0 : 1);
        }).ToArray();
    }

}
