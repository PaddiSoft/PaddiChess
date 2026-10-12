using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using PaddiXiangqi.Sessions;
using SkiaSharp;

namespace PaddiXiangqi.Tests;

public class ManualPositionEvidenceTests
{
    private const string Correct = "9/4c1P2/3kb4/2C6/3P5/9/9/4BA3/4pnppc/2BKnrrpp w - - 0 1";
    private const string Wrong = "9/4c1P2/3kb4/2C6/3P5/9/9/4BA3/4pnppc/2BKnrrpr w - - 0 1";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReviewedPawnRemainsKnownAfterEighteenPliesAndCaptureBrightnessChanges(bool flipped)
    {
        var game = new XiangqiGame(); game.LoadFen(Correct);
        var source = Frame(game, flipped); var grid = Grid(flipped);
        var manual = new ExternalManualPosition(source, grid, Correct);
        // Exact continuation from the reported session. Corrections must survive
        // a real game, not merely authorize the first move from its initial FEN.
        string[] moves = ["d5d6", "d7d8", "d6d7", "d8d9", "d7d8", "d9e9", "d8e8", "e9e8",
            "c6e6", "e7c9", "e6e1", "c9e7", "e2g4", "e7c9", "c0e2", "c9e7", "g4i2", "e8e9"];
        foreach (var move in moves) Assert.True(game.TryMoveUci(move, out _), move);
        const string late = "4k4/6P2/4b4/9/9/9/9/4BA2B/4Cnppc/3Knrrpp w - - 7 10";
        Assert.Equal(late, game.CurrentFen());
        var current = Tint(Frame(game, flipped), 3);
        var raw = new SkinRecognition(late.Replace("rrpp", "rrpr"), [new(5, 9), new(6, 9), new(8, 9)], .15, "黑车超过允许数量 2。");
        var read = manual.Apply(current, grid, raw);
        Assert.True(read.Confident, string.Join(',', read.Uncertain));
        Assert.Equal(late.Split(' ')[0], read.Fen.Split(' ')[0]);
        var observation = BoardObservation.Read(current, grid);
        var gate = ExternalInputVerification.Check(new(observation, game), game, observation, read);
        Assert.True(gate.Recognized, gate.Message); Assert.Empty(gate.Moves);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(.5)]
    public void ReviewedScreenshotRetainsPawnIdentityAcrossFreshCaptureChanges(double scale)
    {
        using var bitmap = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pawn-rook-correction.png"));
        using var scaled = bitmap.Resize(new SKImageInfo((int)(bitmap.Width * scale), (int)(bitmap.Height * scale)),
            new SKSamplingOptions(SKFilterMode.Linear));
        using var png = scaled.Encode(SKEncodedImageFormat.Png, 100);
        var frame = new ExternalFrame(new(1, 2, "fixture", 0, 0, scaled.Width, scaled.Height), png.ToArray());
        var grid = BoardLocator.Locate(frame.Png)! with { RedAtTop = false };
        var manual = new ExternalManualPosition(frame, grid, Correct);
        var raw = new SkinRecognition(Wrong, [new(5, 9), new(6, 9), new(8, 9)], .15, "黑车超过允许数量 2。");
        foreach (var delta in new[] { 3, -3, 6, -6 })
        {
            var read = manual.Apply(Tint(frame, delta), grid, raw);
            Assert.True(read.Confident, $"scale {scale}, delta {delta}: {string.Join(',', read.Uncertain)}");
            Assert.Equal(Correct, read.Fen);
        }
    }

    internal static ExternalFrame Tint(ExternalFrame frame, int delta)
    {
        var pixels = CapturedPixels.DecodePng(frame.Png);
        var data = (byte[])pixels.Bgra.Clone();
        for (var i = 0; i < data.Length; i += 4)
            for (var c = 0; c < 3; c++) data[i + c] = (byte)Math.Clamp(data[i + c] + delta, 0, 255);
        return new(frame.Window, new CapturedPixels(pixels.Width, pixels.Height, pixels.RowBytes, data));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReviewedPixelsRetainTheUsersPawnCorrectionDuringIndependentVerification(bool flipped)
    {
        var game = new XiangqiGame(); game.LoadFen(Correct);
        var frame = Frame(game, flipped); var grid = Grid(flipped);
        var manual = new ExternalManualPosition(frame, grid, Correct);
        var raw = new SkinRecognition(Wrong, [new(5, 9), new(6, 9), new(8, 9)], .15, "黑车超过允许数量 2。");
        var read = manual.Apply(Frame(game, flipped), grid, raw);
        Assert.Equal(Correct, read.Fen); Assert.True(read.Confident);
        var source = BoardObservation.Read(frame, grid);
        var gate = ExternalInputVerification.Check(new(source, game), game, BoardObservation.Read(frame, grid), read);
        Assert.True(gate.Recognized, gate.Message); Assert.Empty(gate.Moves);
    }

    [Fact]
    public void CorrectionIsNotTransferredToAnotherWindowGridOrOrientation()
    {
        var game = new XiangqiGame(); game.LoadFen(Correct);
        var frame = Frame(game); var grid = Grid();
        var manual = new ExternalManualPosition(frame, grid, Correct);
        var raw = new SkinRecognition(Wrong, [new(8, 9)], .15, "数量冲突");
        Assert.Same(raw, manual.Apply(new(frame.Window with { Id = 10 }, frame.Png), grid, raw));
        Assert.Same(raw, manual.Apply(new(frame.Window with { Pid = 10 }, frame.Png), grid, raw));
        Assert.Same(raw, manual.Apply(frame, grid with { Left = grid.Left + 2 }, raw));
        Assert.Same(raw, manual.Apply(frame, grid with { RedAtTop = true }, raw));
        var reoriented = raw with { DetectedRedAtTop = true };
        Assert.Same(reoriented, manual.Apply(frame, grid, reoriented));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualCaptureCannotBeOverriddenByTheReviewedPawn(bool flipped)
    {
        var game = new XiangqiGame(); game.LoadFen("4k4/9/9/rP7/9/9/9/9/9/3K5 b - - 0 1");
        var frame = Frame(game, flipped); var grid = Grid(flipped);
        var manual = new ExternalManualPosition(frame, grid, game.CurrentFen());
        var tracker = new ExternalBoardTracker(BoardObservation.Read(frame, grid), game);
        var after = new XiangqiGame(); after.LoadFen(game.CurrentFen());
        Assert.True(after.TryMoveUci("a6b6", out _));
        var latest = Tint(Frame(after, flipped), 3);
        var read = manual.Apply(latest, grid, new(after.CurrentFen(), [], 0, null));
        Assert.Equal(after.CurrentFen(), read.Fen); Assert.True(read.Confident);
        var gate = ExternalInputVerification.Check(tracker, game, BoardObservation.Read(latest, grid), read);
        Assert.True(gate.Recognized, gate.Message); Assert.Equal(new[] { "a6b6" }, gate.Moves);
        var uncertainCapture = new SkinRecognition(after.CurrentFen(), [new(1, 3)], .2, "尚不确定");
        var unresolved = manual.Apply(latest, grid, uncertainCapture);
        Assert.False(unresolved.Confident);
        Assert.Contains(new Square(1, 3), unresolved.Uncertain);
        Assert.Equal(after.CurrentFen(), unresolved.Fen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedRookGlyphDoesNotInheritTheOldReviewedPawn(bool flipped)
    {
        var game = new XiangqiGame(); game.LoadFen(Correct);
        var grid = Grid(flipped);
        var manual = new ExternalManualPosition(Frame(game, flipped), grid, Correct);
        game.LoadFen(Wrong);
        var raw = new SkinRecognition(Wrong, [new(8, 9)], .15, "数量冲突");
        var read = manual.Apply(Tint(Frame(game, flipped), 3), grid, raw);
        Assert.False(read.Confident); Assert.Equal(Wrong, read.Fen);
        Assert.Contains(new Square(8, 9), read.Uncertain);
    }

    [Fact]
    public void UnchangedCoreDoesNotHideChangesInTheLiftedGlyphRegion()
    {
        var game = new XiangqiGame(); game.LoadFen(Correct);
        var frame = Frame(game); var grid = Grid();
        var manual = new ExternalManualPosition(frame, grid, Correct);
        using var changed = SKBitmap.Decode(frame.Png);
        using var canvas = new SKCanvas(changed);
        using var paint = new SKPaint { Color = SKColors.White };
        canvas.DrawRect(434, 462, 12, 6, paint);
        using var png = changed.Encode(SKEncodedImageFormat.Png, 100);
        var latest = new ExternalFrame(frame.Window, png.ToArray());
        Assert.Equal(BoardObservation.Read(frame, grid).Cells[89], BoardObservation.Read(latest, grid).Cells[89]);
        var raw = new SkinRecognition(Wrong, [new(8, 9)], .15, "数量冲突");
        var read = manual.Apply(latest, grid, raw);
        Assert.False(read.Confident); Assert.Equal(Wrong, read.Fen);
        Assert.Contains(new Square(8, 9), read.Uncertain);
    }

    private static BoardCalibration Grid(bool flipped = false) => new(40, 40, 440, 490, flipped);
    private static ExternalFrame Frame(XiangqiGame game, bool flipped = false) =>
        new(new(1, 2, "reviewed fixture", 0, 0, 480, 530), ExternalBoardTests.Render(game, flipped));
}
