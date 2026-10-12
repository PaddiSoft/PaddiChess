using PaddiXiangqi.External;
using PaddiXiangqi.Core;
using PaddiXiangqi.Sessions;
using SkiaSharp;
using Xunit.Abstractions;

namespace PaddiXiangqi.Tests;

// Board-only crop supplied in the 2026-10-11 stall report; validation data only.
// No fixture, palette, geometry or expected position is referenced by production code.
[Collection("Local recognition")]
public class VerificationStallTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)]
    [InlineData(.5)]
    public async Task PawnBesideTwoRooksIsNotAnExcessThirdRook(double scale)
    {
        using var original = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pawn-rook-correction.png"));
        using var scaled = original.Resize(new SKImageInfo((int)(original.Width * scale), (int)(original.Height * scale)),
            new SKSamplingOptions(SKFilterMode.Linear));
        using var encoded = scaled.Encode(SKEncodedImageFormat.Png, 100);
        var pixels = CapturedPixels.DecodePng(encoded.ToArray());
        var geometry = BoardLocator.Locate(encoded.ToArray())! with { RedAtTop = false };
        await RecognitionAcceleration.PrepareAsync();
        var prediction = await LocalBoardClassifier.ReadAsync(pixels, geometry, false, CancellationToken.None);
        var model = prediction.ToRecognition(true, false);
        var indices = model.Uncertain.Select(s => s.Rank * 9 + s.File).ToArray();
        var ocr = await LocalGlyphOcr.ReadCellsAsync(pixels, geometry, indices, CancellationToken.None);
        output.WriteLine($"model {model.Fen}: {string.Join(',', model.Uncertain.Select(s => s.Uci))}");
        foreach (var i in indices) output.WriteLine($"{new Square(i % 9, i / 9).Uci}: {prediction.Pieces[i]} {prediction.Confidence[i]:F4}; {ocr.Glyphs[i]}");
        var reader = new ExternalPositionRecognizer();
        var screenshot = new ExternalFrame(new(1, 2, "fixture", 0, 0, pixels.Width, pixels.Height), pixels);
        SkinRecognition? read = null;
        // Slow accelerator partitions may exceed a single 800 ms supplement
        // budget. Repeated reads must make progress instead of restarting OCR.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            read = await reader.ReadAsync(screenshot, geometry, true, CancellationToken.None, false, null, null);
            output.WriteLine($"supplement attempt {attempt + 1}: {read?.Confident}");
            if (read?.Confident == true) break;
        }
        Assert.NotNull(read);
        Assert.Equal("9/4c1P2/3kb4/2C6/3P5/9/9/4BA3/4pnppc/2BKnrrpp w - - 0 1", read.Fen);
        Assert.True(read.Confident, $"{read.Problem}; {string.Join(',', read.Uncertain.Select(s => s.Uci))}");
        // Quotas alone may not pick which rook to remove. If another member
        // of the competing group has weak OCR, the conflict must stay visible.
        var weak = (LocalGlyphOcr.Glyph[])ocr.Glyphs.Clone();
        weak[86] = weak[86] with { Confidence = .79 };
        var unresolved = ExternalPositionRecognizer.Supplement(pixels, geometry, prediction, model, weak, true, false);
        Assert.False(unresolved.Confident);
        Assert.Equal(model.Fen, unresolved.Fen);
        var game = new XiangqiGame(); game.LoadFen(read.Fen);
        var frame = BoardObservation.Read(pixels, geometry);
        var gate = ExternalInputVerification.Check(new(frame, game), game, frame, read);
        Assert.True(gate.Recognized, gate.Message);
        Assert.Empty(gate.Moves);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(.5)]
    public async Task ReportedBoardCanAuthorizeInputWithoutAManualMove(double scale)
    {
        using var original = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jj-verification-stall.png"));
        using var scaled = original.Resize(new SKImageInfo((int)(original.Width * scale), (int)(original.Height * scale)), new SKSamplingOptions(SKFilterMode.Linear));
        using var encoded = scaled.Encode(SKEncodedImageFormat.Png, 100);
        var png = encoded.ToArray();
        var geometry = BoardLocator.Locate(png)! with { RedAtTop = true };
        output.WriteLine(geometry.ToString());
        var pixels = CapturedPixels.DecodePng(png);
        await LocalBoardClassifier.PrepareAsync();
        await LocalGlyphOcr.PrepareAsync();
        var pred = await LocalBoardClassifier.ReadAsync(pixels, geometry, false, CancellationToken.None);
        var read = await new ExternalPositionRecognizer().ReadAsync(new(new(1, 2, "fixture", 0, 0, pixels.Width, pixels.Height), pixels),
            geometry, false, CancellationToken.None, false, null, null);
        output.WriteLine($"model: {pred.ToRecognition(false, false)}");
        output.WriteLine($"result: {read}; uncertain {string.Join(',', read!.Uncertain.Select(s => s.Uci))}");
        Assert.True(read.Confident);
        const string expected = "rnbakabr1/9/1c4nc1/p1p1p1p1p/9/9/P1P1P1P1P/1C2C1N2/9/RNBAKABR1 b - - 0 1";
        Assert.Equal(expected, read.Fen);
        var game = new XiangqiGame(); game.LoadFen(expected);
        var observation = BoardObservation.Read(pixels, geometry);
        var tracker = new ExternalBoardTracker(observation, game);
        await using var refresh = new ExternalRecognitionRefresh(CancellationToken.None, TimeSpan.Zero);
        refresh.Seed("same history", null, observation, read);
        // Replay a refresh already in flight when a new capture arrives. No input
        // is sent to a real window; this exercises the production pre-input gate.
        Assert.True(refresh.TryStart("same history", null, observation, TimeSpan.Zero, async ct =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return null; }));
        var fresh = BoardObservation.Read(pixels, geometry);
        var check = ExternalInputVerification.Check(tracker, game, fresh, refresh.Current("same history", null, fresh));
        Assert.True(check.Recognized, check.Message);
        Assert.Empty(check.Moves);
    }
}
