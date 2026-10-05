using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public class ExternalMissedMoveTests
{
    private static readonly BoardCalibration Geometry = new(40, 40, 440, 490, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LowContrastOpponentMoveIsNotMistakenForAnUnchangedBoard(bool flipped)
    {
        var game = new XiangqiGame();
        Assert.True(game.TryMoveUci("h2e2", out _));
        var before = Observe(game, flipped);
        ReduceContrast(before);
        var tracker = new ExternalBoardTracker(before, game);
        Assert.True(game.TryMoveUci("h9g7", out _));
        var after = Observe(game, flipped);
        ReduceContrast(after);
        game.Undo();
        // A generic pixel-stability check also cannot authorize input here:
        // the fresh board has changed legally despite its small pixel delta.
        Assert.True(before.StableWith(after));
        var match = tracker.Match(after, game);
        Assert.True(match.Recognized, match.Message);
        Assert.Equal(new[] { "h9g7" }, match.Moves);

        var unchanged = tracker.Match(before, game);
        Assert.True(unchanged.Recognized);
        Assert.Empty(unchanged.Moves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PulsingDestinationColourDoesNotKeepResettingAStationaryOpponentMove(bool flipped)
    {
        var game = new XiangqiGame();
        Assert.True(game.TryMoveUci("h2e2", out _));
        var tracker = new ExternalBoardTracker(Observe(game, flipped), game);
        Assert.True(game.TryMoveUci("h9g7", out _));
        var first = Observe(game, flipped);
        var second = Observe(game, flipped);
        var index = 2 * 9 + 6; // Black horse arrives at g7 in canonical coordinates.
        for (int i = 0; i < second.Cells[index].Length; i++)
            if (second.Cells[index][i] != 0)
                second.Cells[index][i] = Math.Min(1, second.Cells[index][i] + .055f);
        game.Undo();
        var firstMatch = tracker.Match(first, game);
        var secondMatch = tracker.Match(second, game);
        Assert.True(firstMatch.Recognized, firstMatch.Message);
        Assert.True(secondMatch.Recognized, secondMatch.Message);
        Assert.Equal(new[] { "h9g7" }, firstMatch.Moves);
        Assert.Equal(firstMatch.Moves, secondMatch.Moves);
        Assert.True(BoardObservation.Distance(first.Cells[index], second.Cells[index]) > .012);
        var confirmation = new ExternalMoveConfirmation();
        Assert.False(confirmation.Observe(firstMatch, first, TimeSpan.Zero));
        Assert.True(confirmation.Observe(secondMatch, second, TimeSpan.FromMilliseconds(90)));
    }

    private static BoardObservation Observe(XiangqiGame game, bool flipped) =>
        BoardObservation.Read(ExternalBoardTests.Render(game, flipped), Geometry with { RedAtTop = flipped });

    private static void ReduceContrast(BoardObservation observation)
    {
        foreach (var cell in observation.Cells)
            for (int i = 0; i < cell.Length; i++)
                if (cell[i] != 0) cell[i] = .5f + (cell[i] - .5f) * .03f;
    }
}
