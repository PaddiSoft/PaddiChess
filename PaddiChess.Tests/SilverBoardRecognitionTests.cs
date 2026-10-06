using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using SkiaSharp;
using System.Diagnostics;
using Xunit.Abstractions;

namespace PaddiXiangqi.Tests;

/// <summary>Reported board crops are validation data only; no skin is learned or loaded.</summary>
public class SilverBoardRecognitionTests(ITestOutputHelper output)
{
    public const string ExpectedFen = "rnbakab2/9/1c4n2/2p1p3p/p5p2/2P6/P3P1P1P/4C2r1/4A4/RNBAK1BN1 w - - 0 1";
    public const string FlippedOpeningFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C2B2C1/9/RNBAKA1NR w - - 0 1";

    [Fact]
    public void AViewOutsideTheCapturedPixelsCannotInventDarkStrokesFromTransparentPadding()
    {
        using var image = new SKBitmap(320, 350);
        image.Erase(SKColors.Black);
        var geometry = new BoardCalibration(13, 13, 293, 328, false);
        geometry.Validate(image.Width, image.Height);
        var input = new float[3 * 48 * 160];
        var fill = typeof(LocalGlyphOcr).GetMethod("Fill", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        // This valid grid permits the ordinary crop, but the upper retry view
        // exceeds the capture. No clipped fragment should be offered to OCR.
        fill.Invoke(null, [input, image, geometry, 0, 27]);
        Assert.All(input, value => Assert.Equal(1f, value));
    }

    [Theory]
    [InlineData("JJ")]
    [InlineData("Web")]
    public async Task ExistingBoardsStillRecognizeEverySquareThroughCharacters(string board)
    {
        var jj = board == "JJ";
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, jj ? "Assets/BoardSkins/jj-classic.png" : "Fixtures/web-default-opening.png"));
        var geometry = jj ? BuiltInBoardSkins.JjReferenceGeometry : new BoardCalibration(32.5, 32.5, 462.5, 516.25, false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var read = await BoardGlyphRecognizer.RecognizeAsync(png, geometry, true, cancellation.Token, detectOrientation: true);
        Assert.Equal(jj ? BuiltInBoardSkins.JjReferenceFen : new XiangqiGame().CurrentFen(), read.Fen);
        Assert.True(read.Confident, $"{read.Problem}: {string.Join(',', read.Uncertain.Select(square => square.Uci))}");
        Assert.Empty(read.Uncertain);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task AllNinetySquaresAreRecognizedThroughCharactersWithoutASkinTemplate(bool flipped, int colourTransform)
    {
        var (png, geometry) = Read(flipped, 1, colourTransform);
        var timer = Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var read = await BoardGlyphRecognizer.RecognizeAsync(png, geometry, true, cancellation.Token, detectOrientation: true);
        output.WriteLine($"OCR {timer.Elapsed.TotalMilliseconds:F0} ms; {geometry}; {read.Fen}; {read.Problem}; uncertain: {string.Join(',', read.Uncertain.Select(square => square.Uci))}");
        Assert.Equal(flipped, read.DetectedRedAtTop);
        Assert.Equal(flipped ? FlippedOpeningFen : ExpectedFen, read.Fen);
        Assert.True(read.Confident, $"{read.Problem}: {string.Join(',', read.Uncertain.Select(square => square.Uci))}");
        Assert.Empty(read.Uncertain);
        if (colourTransform == 0)
        {
            // The production entry tries built-in/user templates first. An
            // incompatible template must not short-circuit this new appearance
            // with a different confidently reported board.
            var pixels = CapturedPixels.DecodePng(png);
            var frame = new ExternalFrame(new(1, 2, "fixture", 0, 0, pixels.Width, pixels.Height), pixels);
            var integrated = await new ExternalPositionRecognizer().ReadAsync(frame, geometry, true,
                cancellation.Token, detectOrientation: true, sessionSkin: null, customPath: null);
            Assert.NotNull(integrated);
            Assert.True(integrated.Confident, $"{integrated.Problem}: {string.Join(',', integrated.Uncertain.Select(square => square.Uci))}");
            Assert.Equal(flipped ? FlippedOpeningFen : ExpectedFen, integrated.Fen);
            Assert.Empty(integrated.Uncertain);
            Assert.Equal(flipped, integrated.DetectedRedAtTop ?? geometry.RedAtTop);
        }
    }

    [Theory]
    [InlineData(false, .5, 0)]
    [InlineData(true, .5, 0)]
    [InlineData(false, .75, 1)]
    [InlineData(true, .75, 1)]
    public async Task ReducedResolutionNeverAuthorizesAnIncorrectPieceOrClearsAnUnreadableDisc(bool flipped, double scale, int colourTransform)
    {
        var (png, geometry) = Read(flipped, scale, colourTransform);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        // A user-selected orientation is valid evidence even when a low-resolution
        // king cannot establish orientation automatically. It cannot supply a type.
        var read = await BoardGlyphRecognizer.RecognizeAsync(png, geometry with { RedAtTop = flipped }, true, cancellation.Token, detectOrientation: true);
        var expected = new XiangqiGame(); expected.LoadFen(flipped ? FlippedOpeningFen : ExpectedFen);
        // Partial recognition may legitimately lack a king, so inspect its 90
        // cells directly instead of loading it as an authorized game position.
        var actual = read.Fen.Split(' ')[0].Replace("/", "").SelectMany(c =>
            c is >= '1' and <= '9' ? Enumerable.Repeat('\0', c - '0') : [c]).ToArray();
        Assert.Equal(90, actual.Length);
        output.WriteLine($"{read.Fen}; uncertain: {string.Join(',', read.Uncertain.Select(square => square.Uci))}");
        Assert.All(Enumerable.Range(0, 90), index =>
        {
            var square = new Square(index % 9, index / 9);
            if (!read.Uncertain.Contains(square)) Assert.Equal(expected.Board[square.Rank, square.File], actual[index]);
        });
        if (read.Confident)
        {
            Assert.Equal(expected.CurrentFen(), read.Fen);
            Assert.Empty(read.Uncertain);
        }
        else
        {
            // Without two readable kings the theme cannot establish that
            // textured intersections are empty, so extra uncertainty is valid.
            Assert.NotEmpty(read.Uncertain);
        }
    }

    private static (byte[] Png, BoardCalibration Geometry) Read(bool flipped, double scale, int colourTransform)
    {
        var file = flipped ? "jj-silver-blue-opening-flipped.png" : "jj-silver-blue-midgame.png";
        using var source = SKBitmap.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));
        using var image = source.Resize(new SKImageInfo((int)(source.Width * scale), (int)(source.Height * scale)), new SKSamplingOptions(SKFilterMode.Linear));
        if (colourTransform != 0)
        {
            var colours = image.Pixels;
            for (int i = 0; i < colours.Length; i++)
            {
                var p = colours[i];
                if (colourTransform == 1)
                {
                    // Invert contrast and permute channels: semantic glyphs must
                    // still establish the sides without any red-channel rule.
                    colours[i] = new((byte)(255 - p.Blue), (byte)(255 - p.Red), (byte)(255 - p.Green));
                }
                else
                {
                    // Reduce saturation and exposure without changing geometry.
                    var mean = (p.Red + p.Green + p.Blue) / 3d;
                    byte Transform(byte value) => (byte)Math.Clamp((mean + (value - mean) * .70) * .80 + 12, 0, 255);
                    colours[i] = new(Transform(p.Red), Transform(p.Green), Transform(p.Blue));
                }
            }
            image.Pixels = colours;
        }
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var png = encoded.ToArray();
        var geometry = BoardLocator.Locate(png);
        Assert.NotNull(geometry);
        return (png, geometry);
    }
}
