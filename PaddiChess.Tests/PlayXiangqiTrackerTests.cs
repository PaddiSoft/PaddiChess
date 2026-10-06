using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using PaddiXiangqi.Sessions;

namespace PaddiXiangqi.Tests;

/// <summary>
/// Offline frames rendered from PlayXiangqi's public SVG glyphs and board colours.
/// Both sides have the same ivory disc fill; only their glyphs identify colour/type.
/// The fixture follows the nine recorded plies visible in the user's capture report.
/// </summary>
public class PlayXiangqiTrackerTests
{
    private static readonly BoardCalibration Geometry = new(55, 55, 855, 955, false);
    private static readonly string[] Opening =
        "h2e2 b9c7 h0g2 h7g7 b0c2 g7g3 i0h0 b7a7 a0b0".Split(' ');

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void IvoryBlackCannonCaptureTracksWithoutReidentifyingEveryPiece(int verticalInset)
    {
        var game = BeforeCapture();
        var before = Observe("before", verticalInset);
        var tracker = new ExternalBoardTracker(before, game);
        var after = Observe("after", verticalInset);
        var match = tracker.Match(after, game);
        Assert.True(match.Recognized, $"{match.Error:P2}: {match.Message}");
        Assert.Equal(new[] { "a7a3" }, match.Moves);
        Assert.True(game.TryMoveUci(match.Moves[0], out var capture));
        Assert.Equal('P', capture.Captured);
        tracker.Accept(after, game);
        Assert.True(game.RedToMove);
        var settled = tracker.Match(after, game);
        Assert.True(settled.Recognized);
        Assert.Empty(settled.Moves);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void CaptureAndImmediateReplyRecoverBothPliesWithCorrectNextSide(int verticalInset)
    {
        var game = BeforeCapture();
        var tracker = new ExternalBoardTracker(Observe("before", verticalInset), game);
        var reply = Observe("reply", verticalInset);
        var session = new ExternalSynchronizationSession();
        var first = session.Observe(reply, tracker, game, null, false, null, null, TimeSpan.Zero);
        Assert.False(first.Confirmed);
        var confirmed = session.Observe(reply, tracker, game, null, false, null, null, TimeSpan.FromMilliseconds(90));
        Assert.True(confirmed.Confirmed, confirmed.Status ?? confirmed.Match.Message);
        Assert.Equal(new[] { "a7a3", "h0i0" }, confirmed.Match.Moves);
        foreach (var move in confirmed.Match.Moves) Assert.True(game.TryMoveUci(move, out _));
        Assert.False(game.RedToMove);
        Assert.Equal(11, game.Ply);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void SubmittedCaptureCannotBeConfusedWithADifferentCannonDestination(int verticalInset)
    {
        var game = BeforeCapture();
        var tracker = new ExternalBoardTracker(Observe("before", verticalInset), game);
        var after = Observe("after", verticalInset);
        var intended = tracker.Match(after, game, "a7a3");
        Assert.True(intended.Recognized, intended.Message);
        Assert.Equal(new[] { "a7a3" }, intended.Moves);
        Assert.False(tracker.Match(after, game, "a7a8").Recognized);
    }

    [Theory]
    [InlineData("wrong-horse", 0)]
    [InlineData("wrong-horse", 10)]
    [InlineData("wrong-colour", 0)]
    [InlineData("wrong-colour", 10)]
    public void SourceVacatedButWrongPieceAtDestinationDoesNotConfirmCapture(string frame, int verticalInset)
    {
        var game = BeforeCapture();
        var tracker = new ExternalBoardTracker(Observe("before", verticalInset), game);
        var match = tracker.Match(Observe(frame, verticalInset), game, "a7a3");
        Assert.False(match.Recognized && match.Moves.Count > 0,
            $"A cannon cannot become a horse or change sides: {match.Error:P2}");
    }

    private static XiangqiGame BeforeCapture()
    {
        var game = new XiangqiGame { ExternalAdjudication = true };
        foreach (var move in Opening) Assert.True(game.TryMoveUci(move, out _), move);
        Assert.False(game.RedToMove);
        return game;
    }

    // A 10 px inset is a 2.2% error in rank spacing, reproducing a plausible
    // auto-calibration discrepancy rather than distorting or recolouring the image.
    private static BoardObservation Observe(string name, int verticalInset = 0) => BoardObservation.Read(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"playxiangqi-capture-{name}.png")),
        Geometry with { Top = Geometry.Top + verticalInset, Bottom = Geometry.Bottom - verticalInset });
}
