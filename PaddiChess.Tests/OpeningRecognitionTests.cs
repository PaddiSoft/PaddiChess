using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public class OpeningRecognitionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiscHaloDoesNotVoteForGlyphColour(bool redInk)
    {
        var patch = Enumerable.Repeat(.7f, 1728).ToArray();
        for (int y = 0; y < 24; y++) for (int x = 0; x < 24; x++)
        {
            var radius = (x - 11.5) * (x - 11.5) + (y - 11.5) * (y - 11.5);
            bool ink = x is >= 10 and <= 13 && y is >= 7 and <= 16;
            if (radius > 64 || redInk && ink)
            { int i = (y * 24 + x) * 3; patch[i] = .85f; patch[i + 1] = patch[i + 2] = .15f; }
        }
        Assert.Equal(redInk, BoardGlyphRecognizer.HasRedInk(patch));
    }
    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(true, "h2e2")]
    [InlineData(true, "g0e2")]
    public void IsolatedWrongColourCornerUsesUniqueOpeningEvidence(bool flipped, string firstMove)
    {
        var game = new XiangqiGame();
        if (firstMove.Length > 0) Assert.True(game.TryMoveUci(firstMove, out _));
        var image = BoardObservation.Read(ExternalBoardTests.Render(game, flipped), new(40, 40, 440, 490, flipped));
        var skin = BoardSkin.Learn("ambiguous corner colour", image, game);
        var samples = skin.Samples.ToDictionary(pair => pair.Key, pair => pair.Value.Select(sample => sample.ToArray()).ToList());
        // Contaminated red-rook template is an exact match for the black corner.
        // Independent classification would publish a third red rook and no black rook.
        samples['R'].Add(image.Cells[8].ToArray());
        samples['r'] = samples['r'].Select(sample => sample.Select(value => Math.Clamp(value + .025f, 0, 1)).ToArray()).ToList();
        var read = new BoardSkin("contaminated", samples).Recognize(image, game.RedToMove);
        Assert.Equal(game.CurrentFen().Split(' ')[0], read.Fen.Split(' ')[0]);
        Assert.Null(read.Problem);
        Assert.DoesNotContain(new Square(8, 0), read.Uncertain);
    }

    [Fact]
    public void EnemyCornerRookInMiddleGameIsNotForcedToHomeColour()
    {
        var game = new XiangqiGame(); game.LoadFen("4k3R/9/9/4p4/9/9/9/9/9/4K4 w - - 0 1");
        var reference = new XiangqiGame();
        var skin = BoardSkin.Learn("midgame", BoardObservation.Read(ExternalBoardTests.Render(reference), new(40,40,440,490,false)), reference);
        var read = skin.Recognize(BoardObservation.Read(ExternalBoardTests.Render(game), new(40,40,440,490,false)), true);
        Assert.StartsWith("4k3R/", read.Fen);
    }
    [Fact]
    public void AllLegalFirstRedMovesIdentifyBlackWithoutReplayingHistory()
    {
        var game = new XiangqiGame();
        foreach (var move in game.AllLegalMoves().ToArray())
        {
            Assert.True(game.TryMoveUci(move.Uci, out _));
            Assert.Equal(new OpeningPosition(1, false), OpeningPositions.Find(game.CurrentFen()));
            Assert.False(ExternalTurnInference.FromPosition(game.CurrentFen()));
            game.Undo();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HomeRankRookCannotBeClassifiedAsPawnEvenWhenPawnTemplateIsCloser(bool flipped)
    {
        var game = new XiangqiGame();
        var geometry = new BoardCalibration(40, 40, 440, 490, flipped);
        var observation = BoardObservation.Read(ExternalBoardTests.Render(game, flipped), geometry);
        var learned = BoardSkin.Learn("rook/pawn confusion", observation, game);
        // Model a visually ambiguous / poorly learned pawn exemplar. The corner
        // rook's pixels fit it perfectly, while valid rook exemplars are slightly noisier.
        var samples = learned.Samples.ToDictionary(p => p.Key, p => p.Value.Select(s => s.ToArray()).ToList());
        samples['p'].Add(observation.Cells[8].ToArray());
        samples['r'] = samples['r'].Select(s => s.Select(v => Math.Clamp(v + .012f, 0, 1)).ToArray()).ToList();
        var read = new BoardSkin("ambiguous", samples).Recognize(observation, true);
        var result = new XiangqiGame(); result.LoadFen(read.Fen);
        Assert.Equal('r', result.Board[0, 8]);
        Assert.Equal(game.CurrentFen().Split(' ')[0], read.Fen.Split(' ')[0]);
        Assert.Null(read.Problem);
    }

    [Theory]
    [InlineData('p', 8, 0, false)]
    [InlineData('P', 0, 9, false)]
    [InlineData('p', 2, 3, true)]
    [InlineData('P', 2, 6, true)]
    [InlineData('p', 3, 4, false)]
    [InlineData('p', 3, 5, true)]
    [InlineData('b', 2, 0, true)]
    [InlineData('b', 3, 0, false)]
    [InlineData('A', 4, 8, true)]
    [InlineData('A', 3, 8, false)]
    public void RecognitionUsesReachablePieceSquares(char piece, int file, int rank, bool expected)
        => Assert.Equal(expected, PositionSetup.CanOccupySquare(piece, new(file, rank)));
}
