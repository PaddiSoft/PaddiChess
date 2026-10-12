using PaddiXiangqi.External;
using PaddiXiangqi.Core;
using PaddiXiangqi.Sessions;
using SkiaSharp;
using Xunit.Abstractions;

namespace PaddiXiangqi.Tests;

[Collection("Local recognition")]
public class BorderVerificationTests(ITestOutputHelper output)
{
    private const string Expected = "r1bakab2/9/2n2cn2/p1C3p1p/4p4/2P3P2/P5c1P/4BAr2/3N1R3/R3KA3 b - - 0 1";

    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(.5, 0, false)]
    [InlineData(1, -2, false)]
    [InlineData(1, 2, false)]
    [InlineData(1, 0, true)]
    [InlineData(.5, 0, true)]
    [InlineData(1, -2, true)]
    [InlineData(1, 2, true)]
    public async Task IndependentReadSurvivesSmallImageChanges(double scale, double shift, bool sparse)
    {
        var expected = sparse ? "4k4/3PaP3/9/9/9/9/9/5K3/9/9 w - - 0 1" : Expected;
        using var original = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, "Fixtures",
            sparse ? "sparse-board-verification.png" : "ornate-board-verification.png"));
        using var scaled = original.Resize(new SKImageInfo((int)(original.Width * scale), (int)(original.Height * scale)),
            new SKSamplingOptions(SKFilterMode.Linear));
        using var encoded = scaled.Encode(SKEncodedImageFormat.Png, 100);
        var pixels = CapturedPixels.DecodePng(encoded.ToArray());
        var g = BoardLocator.Locate(encoded.ToArray())! with { RedAtTop = !sparse };
        var geometry = g with { Top = g.Top + shift * scale, Bottom = g.Bottom + shift * scale,
            Left = g.Left + shift * scale, Right = g.Right + shift * scale };
        await RecognitionAcceleration.PrepareAsync();
        output.WriteLine(geometry.ToString());
        var read = await new ExternalPositionRecognizer().ReadAsync(
            new(new(1, 2, "fixture", 0, 0, pixels.Width, pixels.Height), pixels),
            geometry, sparse, CancellationToken.None, false, null, null);
        Assert.NotNull(read);
        Assert.Equal(expected, read.Fen);
        Assert.True(read.Confident, string.Join(',', read.Uncertain.Select(s => s.Uci)));
        await using var refresh = new ExternalRecognitionRefresh(CancellationToken.None);
        var source = BoardObservation.Read(pixels, geometry);
        refresh.Seed("history", null, source, read);
        var game = new XiangqiGame(); game.LoadFen(expected);
        var tracker = new ExternalBoardTracker(source, game);
        // The first screenshot has 63 empties; the former 60-sample cap omitted
        // i0. Even a synthetic one-level lighting change then rejected that
        // square after the complete model read had correctly identified it.
        // The sparse screenshot has a second failure mode: filtering identities
        // by legal squares before classification left no runner-up for 26 empties.
        foreach (var brightness in new[] { 1, -1, 3 })
        {
            using var changed = scaled.Copy();
            for (var y = 0; y < changed.Height; y++)
            for (var x = 0; x < changed.Width; x++)
            {
                var c = changed.GetPixel(x, y);
                byte Adjust(byte value) => (byte)Math.Clamp(value + brightness, 0, 255);
                changed.SetPixel(x, y, new SKColor(Adjust(c.Red), Adjust(c.Green), Adjust(c.Blue), c.Alpha));
            }
            using var changedPng = changed.Encode(SKEncodedImageFormat.Png, 100);
            var frame = BoardObservation.Read(changedPng.ToArray(), geometry);
            Assert.False(ExternalRecognitionRefresh.SameSamples(source, frame));
            var current = refresh.Current("history", null, frame);
            Assert.NotNull(current);
            output.WriteLine($"Brightness {brightness}: uncertain={string.Join(',', current.Uncertain.Select(s => s.Uci))}");
            Assert.True(current.Confident, string.Join(',', current.Uncertain.Select(s => s.Uci)));
            Assert.Equal(expected, current.Fen);
            var gate = ExternalInputVerification.Check(tracker, game, frame, current);
            Assert.True(gate.Recognized, gate.Message);
            Assert.Empty(gate.Moves);
        }
    }
}
