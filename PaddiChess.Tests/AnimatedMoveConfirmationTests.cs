using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using SkiaSharp;

namespace PaddiXiangqi.Tests;

public class AnimatedMoveConfirmationTests
{
    private const string RookFen = "4k4/9/9/9/4P4/9/9/9/9/2R1K4 w - - 0 1";
    private static BoardCalibration Geometry(bool flipped) => new(40, 40, 440, 490, flipped);
    private static XiangqiGame RookGame(string? move = null)
    {
        var game = new XiangqiGame();
        game.LoadFen(RookFen);
        if (move != null) Assert.True(game.TryMoveUci(move, out _));
        return game;
    }
    private static BoardObservation Observe(XiangqiGame game, bool flipped = false) =>
        BoardObservation.Read(ExternalBoardTests.Render(game, flipped), Geometry(flipped));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RookCrossingLegalIntersectionDoesNotCommitBeforeItsActualDestination(bool flipped)
    {
        var game = RookGame();
        var tracker = new ExternalBoardTracker(Observe(game, flipped), game);
        var confirmation = new ExternalMoveConfirmation();
        var passing = Observe(RookGame("c0c3"), flipped);
        var passingMatch = tracker.Match(passing, game);
        // A single animation frame is indistinguishable from a legal shorter rook move.
        Assert.True(passingMatch.Recognized);
        Assert.Equal(new[] { "c0c3" }, passingMatch.Moves);
        Assert.False(confirmation.Observe(passingMatch, passing, TimeSpan.Zero));
        Assert.Equal(0, game.Ply);
        Assert.True(game.RedToMove);

        var destination = RookGame("c0c8");
        var arrived = Observe(destination, flipped);
        var finalMatch = tracker.Match(arrived, game);
        Assert.True(finalMatch.Recognized);
        Assert.Equal(new[] { "c0c8" }, finalMatch.Moves);
        Assert.False(confirmation.Observe(finalMatch, arrived, TimeSpan.FromMilliseconds(80)));
        Assert.True(confirmation.Observe(finalMatch, arrived, TimeSpan.FromMilliseconds(160)));
        Assert.True(game.TryMoveUci(Assert.Single(finalMatch.Moves), out _));
        tracker.Accept(arrived, game);
        Assert.Equal(destination.CurrentFen(), game.CurrentFen());
        Assert.Empty(tracker.Match(arrived, game).Moves);
    }

    [Fact]
    public void TwoCapturesTooCloseTogetherDoNotConfirmAnAnimationFrame()
    {
        var game = RookGame();
        var tracker = new ExternalBoardTracker(Observe(game), game);
        var frame = Observe(RookGame("c0c8"));
        var match = tracker.Match(frame, game);
        var confirmation = new ExternalMoveConfirmation();
        Assert.True(match.Recognized);
        Assert.False(confirmation.Observe(match, frame, TimeSpan.Zero));
        Assert.False(confirmation.Observe(match, frame, TimeSpan.FromMilliseconds(74)));
        Assert.True(confirmation.Observe(match, frame, TimeSpan.FromMilliseconds(75)));
    }

    [Fact]
    public void SubmittedEndpointConfirmsAtNextFrameButUnknownMoveStillNeedsAnimationGuard()
    {
        var game = RookGame();
        var tracker = new ExternalBoardTracker(Observe(game), game);
        var arrived = Observe(RookGame("c0c8"));
        var match = tracker.Match(arrived, game, "c0c8");
        var confirmation = new ExternalMoveConfirmation();
        Assert.False(confirmation.Observe(match, arrived, TimeSpan.Zero, "c0c8"));
        Assert.False(confirmation.Observe(match, arrived, TimeSpan.FromMilliseconds(10), "c0c8"));
        Assert.True(confirmation.Observe(match, arrived, TimeSpan.FromMilliseconds(17), "c0c8"));
        confirmation.Reset();
        Assert.False(confirmation.Observe(match, arrived, TimeSpan.Zero, "c0c7"));
        Assert.False(confirmation.Observe(match, arrived, TimeSpan.FromMilliseconds(17), "c0c7"));
        Assert.True(confirmation.Observe(match, arrived, TimeSpan.FromMilliseconds(75), "c0c7"));
    }

    [Fact]
    public void SameLegalCandidateMustAlsoStopMovingWithinItsDestinationCell()
    {
        var game = RookGame();
        var tracker = new ExternalBoardTracker(Observe(game), game);
        var destination = RookGame("c0c8");
        var first = ObserveShiftedRook(destination, -4);
        var second = ObserveShiftedRook(destination, 4);
        var firstMatch = tracker.Match(first, game);
        var secondMatch = tracker.Match(second, game);
        Assert.True(firstMatch.Recognized, firstMatch.Message);
        Assert.True(secondMatch.Recognized, secondMatch.Message);
        Assert.Equal(new[] { "c0c8" }, firstMatch.Moves);
        Assert.Equal(firstMatch.Moves, secondMatch.Moves);
        var confirmation = new ExternalMoveConfirmation();
        Assert.False(confirmation.Observe(firstMatch, first, TimeSpan.Zero));
        Assert.False(confirmation.Observe(secondMatch, second, TimeSpan.FromMilliseconds(100)));
        Assert.False(confirmation.Observe(secondMatch, second, TimeSpan.FromMilliseconds(174)));
        Assert.True(confirmation.Observe(secondMatch, second, TimeSpan.FromMilliseconds(175)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingMoveAndImmediateReplyCanConfirmTogetherWithoutCommittingIntermediateFrame(bool flipped)
    {
        var game = new XiangqiGame();
        var tracker = new ExternalBoardTracker(Observe(game, flipped), game);
        var target = new XiangqiGame();
        Assert.True(target.TryMoveUci("h2e2", out _));
        var ownFrame = Observe(target, flipped);
        var ownMatch = tracker.Match(ownFrame, game, "h2e2");
        var confirmation = new ExternalMoveConfirmation();
        Assert.True(ownMatch.Recognized);
        Assert.False(confirmation.Observe(ownMatch, ownFrame, TimeSpan.Zero));

        Assert.True(target.TryMoveUci("h9g7", out _));
        var replyFrame = Observe(target, flipped);
        var replyMatch = tracker.Match(replyFrame, game, "h2e2");
        Assert.True(replyMatch.Recognized);
        Assert.Equal(new[] { "h2e2", "h9g7" }, replyMatch.Moves);
        Assert.False(confirmation.Observe(replyMatch, replyFrame, TimeSpan.FromMilliseconds(80)));
        Assert.True(confirmation.Observe(replyMatch, replyFrame, TimeSpan.FromMilliseconds(160)));
        foreach (var move in replyMatch.Moves) Assert.True(game.TryMoveUci(move, out _));
        tracker.Accept(replyFrame, game);
        Assert.Equal(2, game.Ply);
        Assert.Equal(target.CurrentFen(), game.CurrentFen());
        Assert.True(game.RedToMove);
    }

    [Fact]
    public void LostCandidateAndExplicitResetBothRequireFreshConfirmation()
    {
        var game = RookGame();
        var tracker = new ExternalBoardTracker(Observe(game), game);
        var frame = Observe(RookGame("c0c8"));
        var match = tracker.Match(frame, game);
        var confirmation = new ExternalMoveConfirmation();
        Assert.False(confirmation.Observe(match, frame, TimeSpan.Zero));
        Assert.True(confirmation.HasCandidate);
        Assert.False(confirmation.Observe(new(false, [], 1, "animation"), frame, TimeSpan.FromMilliseconds(80)));
        Assert.False(confirmation.HasCandidate);
        Assert.False(confirmation.Observe(match, frame, TimeSpan.FromMilliseconds(160)));
        confirmation.Reset();
        Assert.False(confirmation.HasCandidate);
        Assert.False(confirmation.Observe(match, frame, TimeSpan.FromMilliseconds(240)));
    }

    private static BoardObservation ObserveShiftedRook(XiangqiGame game, float offset)
    {
        using var bmp = new SKBitmap(480, 530);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(232, 201, 151));
        using var grid = new SKPaint { Color = new SKColor(128, 92, 52), StrokeWidth = 1.3f, IsAntialias = true };
        for (var i = 0; i < 9; i++) canvas.DrawLine(40 + i * 50, 40, 40 + i * 50, 490, grid);
        for (var i = 0; i < 10; i++) canvas.DrawLine(40, 40 + i * 50, 440, 40 + i * 50, grid);
        using var piece = new SKPaint { IsAntialias = true };
        using var text = new SKPaint { IsAntialias = true, Color = SKColors.White };
        using var font = new SKFont(SKTypeface.Default, 26);
        for (var r = 0; r < 10; r++)
        for (var f = 0; f < 9; f++)
        {
            var c = game.Board[r, f];
            if (c == '\0') continue;
            var (x, y) = Geometry(false).Point(new Square(f, r));
            if (c == 'R') y += offset;
            piece.Color = char.IsUpper(c) ? new SKColor(170, 42, 50) : new SKColor(32, 40, 45);
            canvas.DrawCircle((float)x, (float)y, 22, piece);
            canvas.DrawText(char.ToUpper(c).ToString(), (float)x, (float)y + 9, SKTextAlign.Center, font, text);
        }
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return BoardObservation.Read(data.ToArray(), Geometry(false));
    }
}
