using System.Text.Json;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using SkiaSharp;

namespace PaddiXiangqi.Tests;

[Collection("Local recognition")]
public class BoardClassifierTests
{
    public sealed record Sample(string Id, string Path, double[] Grid, bool Flipped, string Fen, string Group);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static IEnumerable<object[]> Samples() => JsonSerializer.Deserialize<Sample[]>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "recognition-cases.json")), Json)!
        .Select(sample => new object[] { sample.Id });
    private static Sample Load(string id) => JsonSerializer.Deserialize<Sample[]>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "recognition-cases.json")), Json)!.Single(x => x.Id == id);
    private static char[] Expand(string fen) => fen.Split(' ')[0].Where(c => c != '/').SelectMany(c =>
        char.IsDigit(c) ? Enumerable.Repeat('\0', c - '0') : [c]).ToArray();

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task ProductionReaderDoesNotAcceptIncorrectIdentitiesAcrossReportedAndUnseenBoards(string id)
    {
        await RecognitionAcceleration.PrepareAsync();
        var sample = Load(id);
        var pixels = CapturedPixels.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, sample.Path)));
        var frame = new ExternalFrame(new(1, 2, "regression", 0, 0, pixels.Width, pixels.Height), pixels);
        var grid = new BoardCalibration(sample.Grid[0], sample.Grid[1], sample.Grid[2], sample.Grid[3], sample.Flipped);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var read = await new ExternalPositionRecognizer().ReadAsync(frame, grid, true, timeout.Token, false, null, null);
        Assert.NotNull(read); Assert.False(frame.PngEncoded);
        var truth = Expand(sample.Fen); var actual = Expand(read.Fen);
        for (var i = 0; i < 90; i++)
            if (!read.Uncertain.Contains(new Square(i % 9, i / 9))) Assert.Equal(truth[i], actual[i]);
        if (sample.Group == "reported")
        {
            Assert.Equal(truth, actual);
            Assert.True(read.Confident, $"{id}: {read.Problem} / {string.Join(',', read.Uncertain)}");
        }
        if (read.Confident) Assert.Equal(truth, actual);
    }

    [Theory]
    [InlineData("jj-silver-blue-opening-flipped")]
    [InlineData("ocr-unseen-blue-green")]
    public async Task WrongInitialOrientationAndPaddedBgraRowsAreRecoveredWithoutSkinTemplates(string id)
    {
        var sample = Load(id);
        var image = CapturedPixels.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, sample.Path)));
        var stride = image.RowBytes + 64; var data = new byte[stride * image.Height];
        for (var y = 0; y < image.Height; y++) image.Bgra.AsSpan(y * image.RowBytes, image.Width * 4).CopyTo(data.AsSpan(y * stride));
        var frame = new ExternalFrame(new(1, 2, "regression", 0, 0, image.Width, image.Height), new CapturedPixels(image.Width, image.Height, stride, data));
        var grid = new BoardCalibration(sample.Grid[0], sample.Grid[1], sample.Grid[2], sample.Grid[3], false);
        var read = await new ExternalPositionRecognizer().ReadAsync(frame, grid, true, CancellationToken.None, true, null, null);
        Assert.NotNull(read); Assert.True(read.DetectedRedAtTop);
        Assert.True(read.Confident, $"{read.Problem} / {string.Join(',', read.Uncertain)}");
        Assert.Equal(Expand(sample.Fen), Expand(read.Fen)); Assert.False(frame.PngEncoded);
    }

    [Theory]
    [InlineData(200, 200, 200)]
    [InlineData(230, 193, 126)]
    public async Task OpaqueCoverCannotAuthorizeAMissingHorse(byte red, byte green, byte blue)
    {
        using var image = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, "Fixtures", "playxiangqi-opening.png"));
        using (var canvas = new SKCanvas(image))
        using (var paint = new SKPaint { Color = new(red, green, blue) }) canvas.DrawRect(SKRect.Create(105, 5, 100, 100), paint);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var frame = new ExternalFrame(new(1, 2, "cover", 0, 0, image.Width, image.Height), png.ToArray());
        var read = await new ExternalPositionRecognizer().ReadAsync(frame, new(55, 55, 855, 955, false), true, CancellationToken.None, false, null, null);
        Assert.NotNull(read); Assert.False(read.Confident);
        Assert.Contains(new Square(1, 0), read.Uncertain);
    }

    [Fact]
    public void ExcessPiecesAndIllegalSquaresCannotBeSilentlyRepairedIntoAnAcceptedBoard()
    {
        var pieces = Expand(XiangqiGame.InitialFen); pieces[45] = 'p'; pieces[3] = 'r';
        var prediction = new LocalBoardClassifier.Prediction(pieces, Enumerable.Repeat(.99, 90).ToArray(), false);
        var read = prediction.ToRecognition(true, false);
        Assert.False(read.Confident); Assert.NotNull(read.Problem);
        Assert.Contains(new Square(0, 0), read.Uncertain); Assert.Contains(new Square(3, 0), read.Uncertain);
        Assert.Contains(new Square(0, 5), read.Uncertain);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(255)]
    public async Task BlankCaptureNeverAuthorizesABoard(byte shade)
    {
        using var image = new SKBitmap(500, 550); image.Erase(new SKColor(shade, shade, shade));
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var pixels = CapturedPixels.DecodePng(png.ToArray());
        var prediction = await LocalBoardClassifier.ReadAsync(pixels, new(32.5, 32.5, 462.5, 516.25, false), true, CancellationToken.None);
        Assert.False(prediction.ToRecognition(true, true).Confident);
        Assert.Equal(90, prediction.ToRecognition(true, true).Uncertain.Count);
    }

    [Fact]
    public async Task SupplementalOcrOnlyEvaluatesRequestedCells()
    {
        var pixels = CapturedPixels.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ocr-unseen-ivory.png")));
        var read = await LocalGlyphOcr.ReadCellsAsync(pixels, new(50, 50, 570, 635, false), [0], CancellationToken.None);
        Assert.InRange(read.InferenceCells, 1, 33);
        Assert.True(LocalGlyphOcr.IsReliable(read.Glyphs[0]));
        Assert.All(read.Glyphs.Skip(1), glyph => Assert.Equal("", glyph.Text));
    }

    [Fact]
    public async Task CancelledWorkDoesNotHoldTheInferenceGate()
    {
        var sample = Load("web-default-opening");
        var pixels = CapturedPixels.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, sample.Path)));
        var grid = new BoardCalibration(sample.Grid[0], sample.Grid[1], sample.Grid[2], sample.Grid[3], false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalBoardClassifier.ReadAsync(pixels, grid, false, new CancellationToken(true)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.True((await LocalBoardClassifier.ReadAsync(pixels, grid, false, timeout.Token)).ToRecognition(true, false).Confident);
    }
}

// OCR has a shared inference gate and a real time budget. Serialize the
// recognition suites so tests measure work rather than other tests' queue time.
[CollectionDefinition("Local recognition", DisableParallelization = true)]
public class LocalRecognitionCollection;
