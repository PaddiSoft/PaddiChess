using System.Reflection;
using PaddiXiangqi.Core;

namespace PaddiXiangqi.Tests;

public class ExternalAdjudicationTests
{
    private static readonly string[] QuietCycle = ["b0c2", "b9c7", "c2b0", "c7b9"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatingThreeTimesDoesNotDeclareADrawOrBlockObservedContinuation(bool external)
    {
        var game = new XiangqiGame { ExternalAdjudication = external };
        Play(game, QuietCycle.Concat(QuietCycle));
        Assert.Equal(8, game.Ply);
        Assert.Equal(GameResult.Ongoing, game.Result);
        Play(game, QuietCycle);
        Assert.Equal(12, game.Ply);
        Assert.Equal(GameResult.Ongoing, game.Result);
        Assert.Null(game.AgreedDrawPly);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BasicCandidatesDoNotImposeAFixedRepetitionLimit(bool external)
    {
        var game = new XiangqiGame { ExternalAdjudication = external };
        for (var cycle = 0; cycle < 3; cycle++)
        {
            Play(game, QuietCycle.Take(3));
            Assert.Contains("c7b9", game.AllLegalMoves().Select(move => move.Uci));
            var choices = game.AllControllerLegalMoves().Select(move => move.Uci).ToArray();
            Assert.Contains("c7b9", choices);
            Assert.NotEmpty(choices);
            Assert.Equal(GameResult.Ongoing, game.Result);
            Assert.True(game.TryMoveUci("c7b9", out _));
        }
    }

    [Fact]
    public void ThirdCurrentPositionPreservesMovementCandidatesForNativeAdjudication()
    {
        var game = new XiangqiGame { ExternalAdjudication = true };
        Play(game, QuietCycle.Concat(QuietCycle));
        Assert.Contains("b0c2", game.AllLegalMoves().Select(move => move.Uci));
        Assert.Contains("b0c2", game.AllControllerLegalMoves().Select(move => move.Uci));
        Assert.Contains("h2e2", game.AllControllerLegalMoves().Select(move => move.Uci));
        Assert.True(game.GoToPly(4));
        Assert.Contains("b0c2", game.AllControllerLegalMoves().Select(move => move.Uci));
    }

    [Fact]
    public void MovementCandidatesLeaveCheckingCycleJudgmentToNativeRules()
    {
        var game = new XiangqiGame { ExternalAdjudication = true };
        game.LoadFen("3k5/9/2R6/9/9/4P4/9/9/9/4K4 w - - 0 1");
        Play(game, ["c7d7", "d9e9", "d7e7", "e9f9", "e7f7", "f9e9"]);
        Assert.Contains("f7e7", game.AllLegalMoves().Select(move => move.Uci));
        Assert.Contains("f7e7", game.AllControllerLegalMoves().Select(move => move.Uci));
        Assert.NotEmpty(game.AllControllerLegalMoves());
        Assert.Equal(GameResult.Ongoing, game.Result);
        // Observed moves remain recordable regardless of native history adjudication.
        Assert.True(game.TryMoveUci("f7e7", out _));
    }

    [Fact]
    public void NavigationAndBranchingKeepCompleteHistoryWithoutClientRepeatBans()
    {
        var game = new XiangqiGame { ExternalAdjudication = true };
        Play(game, QuietCycle.Concat(QuietCycle).Concat(QuietCycle.Take(3)));
        Assert.Contains("c7b9", game.AllControllerLegalMoves().Select(move => move.Uci));
        Assert.True(game.GoToPly(7));
        Assert.Contains("c7b9", game.AllControllerLegalMoves().Select(move => move.Uci));
        Assert.True(game.TryMoveUci("c7e8", out _));
        Assert.False(game.CanRedo);
        Assert.Equal(8, game.TotalPly);
        game.LoadFen(XiangqiGame.InitialFen);
        Assert.Contains("b0c2", game.AllControllerLegalMoves().Select(move => move.Uci));
    }

    [Fact]
    public void LocalAndExternalMovementValidationAcceptFifthCheckForNativeJudgment()
    {
        var game = FourCheckPosition();
        Assert.Contains("e7d7", game.AllLegalMoves().Select(move => move.Uci));
        game.ExternalAdjudication = true;
        Assert.Contains("e7d7", game.AllLegalMoves().Select(move => move.Uci));
        Assert.Contains("e7d7", game.AllControllerLegalMoves().Select(move => move.Uci));
        Assert.True(game.TryMoveUci("e7d7", out var move));
        Assert.True(move.IsCheck);
        Assert.Equal(GameResult.Ongoing, game.Result);
        Assert.True(game.TryMoveUci("d9e9", out _));
        Assert.Equal(10, game.Ply);
    }

    [Fact]
    public void ExternalSourceFlagDoesNotChangeMovementLegality()
    {
        var game = FourCheckPosition();
        var field = typeof(XiangqiGame).GetField("_resultPosition", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(GameResult.Ongoing, game.Result);
        Assert.NotNull(field.GetValue(game));
        game.ExternalAdjudication = true;
        Assert.Null(field.GetValue(game));
        Assert.Equal(GameResult.Ongoing, game.Result);
        Assert.Contains("e7d7", game.AllLegalMoves().Select(move => move.Uci));
        game.ExternalAdjudication = false;
        Assert.Null(field.GetValue(game));
        Assert.Equal(GameResult.Ongoing, game.Result);
        Assert.Contains("e7d7", game.AllLegalMoves().Select(move => move.Uci));
    }

    [Fact]
    public void LoadingAndNewGamePreserveChosenAdjudicationPolicy()
    {
        var game = new XiangqiGame { ExternalAdjudication = true };
        game.LoadFen("3k5/9/2R6/9/9/4P4/9/9/9/4K4 w - - 0 1");
        Assert.True(game.ExternalAdjudication);
        game.NewGame();
        Assert.True(game.ExternalAdjudication);
        game.ExternalAdjudication = false;
        game.LoadFen(XiangqiGame.InitialFen);
        Assert.False(game.ExternalAdjudication);
    }

    [Fact]
    public void ExternalObservationStillRejectsGeometryErrorsAndExposingKing()
    {
        var game = new XiangqiGame { ExternalAdjudication = true };
        Assert.False(game.TryMoveUci("b0b2", out _));
        game.LoadFen("4k4/9/9/9/9/9/9/9/4R4/4K4 w - - 0 1");
        Assert.False(game.TryMoveUci("e1f1", out _));
        Assert.DoesNotContain("e1f1", game.AllLegalMoves().Select(move => move.Uci));
        Assert.DoesNotContain("e1f1", game.AllControllerLegalMoves().Select(move => move.Uci));
    }

    [Fact]
    public void ExplicitAgreedDrawRemainsADrawAndStopsControllerSelection()
    {
        var game = new XiangqiGame { ExternalAdjudication = true };
        Play(game, QuietCycle.Take(2));
        Assert.True(game.DeclareDraw());
        Assert.Equal(GameResult.Draw, game.Result);
        Assert.Empty(game.AllControllerLegalMoves());
        Assert.False(game.TryMoveUci("c2b0", out _));
    }

    [Fact]
    public void PositionWithNoGeometricResponseKeepsItsLocalMateInference()
    {
        var game = new XiangqiGame { ExternalAdjudication = true };
        game.LoadFen("3k5/9/9/9/9/9/9/2n1r4/5r3/4K4 w - - 0 1");
        Assert.True(game.SideInCheck);
        Assert.Empty(game.AllLegalMoves());
        Assert.Empty(game.AllControllerLegalMoves());
        Assert.Equal(GameResult.BlackWins, game.Result);
        // The external session treats this as a local board inference: it should
        // withhold input and keep observing until the remote game supplies evidence.
    }

    private static XiangqiGame FourCheckPosition()
    {
        var game = new XiangqiGame();
        game.LoadFen("3k5/9/2R6/9/9/4P4/9/9/9/4K4 w - - 0 1");
        Play(game, ["c7d7", "d9e9", "d7e7", "e9f9", "e7f7", "f9e9", "f7e7", "e9d9"]);
        return game;
    }

    private static void Play(XiangqiGame game, IEnumerable<string> moves)
    {
        foreach (var move in moves) Assert.True(game.TryMoveUci(move, out _), $"{move} at ply {game.Ply}");
    }
}
