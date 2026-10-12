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
        var latest = Frame(after, flipped);
        var read = manual.Apply(latest, grid, new(after.CurrentFen(), [], 0, null));
        Assert.Equal(after.CurrentFen(), read.Fen); Assert.True(read.Confident);
        var gate = ExternalInputVerification.Check(tracker, game, BoardObservation.Read(latest, grid), read);
        Assert.True(gate.Recognized, gate.Message); Assert.Equal(new[] { "a6b6" }, gate.Moves);
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
