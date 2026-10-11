using PaddiXiangqi.External;
using PaddiXiangqi.Core;
using PaddiXiangqi.Sessions;
using SkiaSharp;
using Xunit.Abstractions;

namespace PaddiXiangqi.Tests;

[Collection("Local recognition")]
public class MarkerRecognitionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("jj-marker-opening", "2rakabnr/3n5/1c2b2c1/p1p1p3p/6p2/2P6/P3P1P1P/2N1C1C2/9/1RBAKABNR b - - 0 1")]
    [InlineData("jj-marker-midgame", "1rb1kab2/4aR3/2n3c2/p1p1p4/4c4/3C1p3/P1Pr5/2N1nC2B/4A4/2R1KA3 b - - 0 1")]
    public async Task EmptyOriginMarkerDoesNotBlockRecognition(string fixture, string expected)
    {
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture + ".png"));
        var pixels = CapturedPixels.DecodePng(png);
        var geometry = BoardLocator.Locate(png)! with { RedAtTop = true };
        await RecognitionAcceleration.PrepareAsync();
        output.WriteLine(geometry.ToString());
        var prediction = await LocalBoardClassifier.ReadAsync(pixels, geometry, false, CancellationToken.None);
        for (int i = 0; i < 90; i++)
            if (prediction.Confidence[i] < .8)
                output.WriteLine($"{new Square(i % 9, i / 9).Uci}: {(int)prediction.Pieces[i]} {prediction.Confidence[i]} grid={BoardGridEvidence.HasVisibleGrid(pixels, geometry, 89-i)} disc={PieceSilhouette.DiscRays(pixels, geometry, 89-i)}");
        var read = await new ExternalPositionRecognizer().ReadAsync(new(new(1, 2, "fixture", 0, 0, pixels.Width, pixels.Height), pixels), geometry, false, CancellationToken.None, false, null, null);
        output.WriteLine($"{read!.Fen} uncertain {string.Join(',', read.Uncertain.Select(s=>s.Uci))}");
        Assert.Equal(expected, read.Fen);
        Assert.True(read.Confident);
    }

    [Theory]
    [InlineData(1, -2)]
    [InlineData(1, 2)]
    [InlineData(.5, 0)]
    [InlineData(.5, -2)]
    [InlineData(.5, 2)]
    public async Task EmptyCornerWithMarkerToleratesCaptureScaleAndSmallGridOffset(double scale, double shift)
    {
        using var original = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jj-marker-opening.png"));
        using var scaled = original.Resize(new SKImageInfo((int)(original.Width*scale), (int)(original.Height*scale)), new SKSamplingOptions(SKFilterMode.Linear));
        using var encoded = scaled.Encode(SKEncodedImageFormat.Png, 100);
        var pixels = CapturedPixels.DecodePng(encoded.ToArray());
        var g = BoardLocator.Locate(encoded.ToArray())! with { RedAtTop = true };
        var geometry = g with { Top = g.Top+shift*scale, Bottom = g.Bottom+shift*scale, Left=g.Left+shift*scale, Right=g.Right+shift*scale };
        await RecognitionAcceleration.PrepareAsync();
        var pred = await LocalBoardClassifier.ReadAsync(pixels, geometry, false, CancellationToken.None);
        output.WriteLine(geometry.ToString());
        for (int i=0; i<90; i++)
            if(pred.Confidence[i]<.8) output.WriteLine($"{new Square(i%9,i/9).Uci} piece={(int)pred.Pieces[i]} conf={pred.Confidence[i]:F4} grid={BoardGridEvidence.HasVisibleGrid(pixels,geometry,89-i)} disc={PieceSilhouette.DiscRays(pixels,geometry,89-i)}");
        var read = await new ExternalPositionRecognizer().ReadAsync(new(new(1, 2, "fixture", 0, 0, pixels.Width, pixels.Height), pixels), geometry, false, CancellationToken.None, false, null, null);
        output.WriteLine($"{read!.Fen} uncertain {string.Join(',',read.Uncertain.Select(s=>s.Uci))}");
        Assert.Equal("2rakabnr/3n5/1c2b2c1/p1p1p3p/6p2/2P6/P3P1P1P/2N1C1C2/9/1RBAKABNR b - - 0 1", read.Fen);
        Assert.True(read.Confident);

        var observation = BoardObservation.Read(pixels, geometry);
        var game = new XiangqiGame(); game.LoadFen(read.Fen);
        var tracker = new ExternalBoardTracker(observation, game);
        await using var refresh = new ExternalRecognitionRefresh(CancellationToken.None);
        refresh.Seed("history", null, observation, read);
        // A last-move marker keeps pulsing while the chess position is stationary.
        // Check the actual changed pixels through the production pre-input gate.
        for (int phase = 0; phase < 6; phase++)
        {
            using var pulse = scaled.Copy();
            using (var canvas = new SKCanvas(pulse))
            using (var paint = new SKPaint { Color = new SKColor((byte)(120+phase*20), (byte)(120+phase*20), (byte)(120+phase*20)) })
                canvas.DrawCircle((float)g.Right, (float)g.Top, (float)(4*scale), paint);
            using var png = pulse.Encode(SKEncodedImageFormat.Png, 100);
            var frame = BoardObservation.Read(png.ToArray(), geometry);
            var fresh = refresh.Current("history", null, frame);
            Assert.NotNull(fresh);
            Assert.True(fresh.Confident, string.Join(',', fresh.Uncertain.Select(s => s.Uci)));
            var verified = ExternalInputVerification.Check(tracker, game, frame, fresh);
            Assert.True(verified.Recognized, verified.Message);
            Assert.Empty(verified.Moves);
        }

        // Reconstruct only the preceding rook step for a deterministic playback.
        // The after image and its labels come from the production model above.
        var prior = new XiangqiGame();
        prior.LoadFen(read.Fen.Replace("1RBAKABNR b", "R1BAKABNR w"));
        var before = BoardObservation.Read(pixels, geometry);
        (before.Cells[81], before.Cells[82]) = (before.Cells[82], before.Cells[81]);
        var oldTracker = new ExternalBoardTracker(before, prior);
        var synchronization = new ExternalSynchronizationSession();
        var first = synchronization.Observe(observation, oldTracker, prior, null, false, null, null, TimeSpan.Zero, read);
        Assert.False(first.Confirmed);
        var confirmed = synchronization.Observe(observation, oldTracker, prior, null, false, null, null, TimeSpan.FromMilliseconds(100), read);
        Assert.True(confirmed.Confirmed, confirmed.Match.Message);
        Assert.Equal(new[] { "a0b0" }, confirmed.Match.Moves);
    }
}
