using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public class ExternalAppearanceRecoveryTests
{
    private const string FloatingFen = "3a1aC2/3n2N2/5k3/p7p/2bnP4/6P2/P7P/2NA1C3/4K4/3A4c w - - 0 1";

    [Fact]
    public void ActualSelectedFloatingPiecesAreTheSamePositionNotAMove()
    {
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","jj-selected-floating.png"));
        var geometry = BoardLocator.Locate(png)!;
        var observed = BoardObservation.Read(png, geometry);
        var skin = BuiltInBoardSkins.All[0];
        var read = skin.Recognize(observed, true);
        Assert.True(read.Confident, $"{read.Problem} {string.Join(',', read.Uncertain)}");
        Assert.Equal(FloatingFen, read.Fen);
        var game = new XiangqiGame(); game.LoadFen(FloatingFen);
        // A synthetic neutral baseline uses the skin's unselected pawn glyph at i3.
        // AFTER is the real user screenshot, including lift and glow. Never call this a real BEFORE frame.
        var baseline = BoardObservation.Read(png, geometry);
        baseline.Cells[6*9+8] = (float[])skin.Samples['P'][0].Clone();
        Assert.False(baseline.StableWith(observed));
        var match = new ExternalBoardTracker(baseline, game).MatchRecognizedPosition(read, game, frame: observed);
        Assert.True(match.Recognized, match.Message);
        Assert.Empty(match.Moves);
    }

    [Fact]
    public void ActualRookCaptureAndHorseRecapturePreserveBothMoves()
    {
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","jj-rook-recapture.png"));
        var geometry = BoardLocator.Locate(png)!;
        var after = BoardObservation.Read(png, geometry);
        var read = BuiltInBoardSkins.All[0].Recognize(after, true);
        Assert.True(read.Confident, $"{read.Problem} {string.Join(',', read.Uncertain)}");
        var game = new XiangqiGame();
        game.LoadFen("2bakabr1/9/2n1c1n2/p1p1p1p1p/9/2P3P2/P3P1c1P/C3C1N2/9/1NBAKABR1 w - - 0 1");
        // Full-board confident recognition does not use pixel baseline identities.
        // Supply a synthetic BEFORE image rather than claiming we have a second user screenshot.
        var before = BoardObservation.Read(ExternalBoardTests.Render(game), new(40,40,440,490,false));
        var tracker = new ExternalBoardTracker(before,game);
        var match = tracker.MatchRecognizedPosition(read,game,"h0h9");
        Assert.True(match.Recognized, match.Message);
        Assert.Equal(new[]{"h0h9","g7h9"},match.Moves);
        Assert.False(tracker.MatchRecognizedPosition(read,game,"a2b2").Recognized);
        foreach(var move in match.Moves) Assert.True(game.TryMoveUci(move,out _));
        Assert.Equal(read.Fen.Split(' ')[0],game.CurrentFen().Split(' ')[0]);
        Assert.Equal(2,game.Ply);
    }
}
