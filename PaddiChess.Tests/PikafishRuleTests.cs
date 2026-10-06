using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;

namespace PaddiXiangqi.Tests;

public class PikafishRuleTests
{
    private static XiangqiGame Play(string fen, string history)
    {
        var game = new XiangqiGame(); game.LoadFen(fen);
        foreach (var move in history.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            Assert.True(game.TryMoveUci(move, out _), move);
        return game;
    }

    private static Task<NativeRuleMoves> Read(PikafishRulesClient rules, XiangqiGame game) =>
        rules.GetAllowedMovesAsync(game.StartFen, game.UciMoveList, game.CurrentFen());

    [Fact]
    public async Task NativeRulesKeepEveryOpeningMoveAndReuseProcessAndExactHistoryCache()
    {
        await using var rules = new PikafishRulesClient();
        var game = new XiangqiGame();
        var first = await Read(rules, game);
        Assert.Equal(game.AllLegalMoves().Select(move => move.Uci).Order(), first.Allowed.Order());
        Assert.Empty(first.Excluded);
        Assert.Same(first, await Read(rules, game));
        Assert.Equal(1, rules.QueryCount);
        Assert.True(game.TryMoveUci("b2e2", out _));
        var second = await Read(rules, game);
        Assert.Equal(game.AllLegalMoves().Select(move => move.Uci).Order(), second.Allowed.Order());
        Assert.Equal(2, rules.QueryCount);
        Assert.Equal(1, rules.ProcessStartCount);
    }

    [Fact]
    public async Task PerpetualCheckUsesFullHistoryAndCannotBeRecoveredFromFenAlone()
    {
        await using var rules = new PikafishRulesClient();
        var game = Play("4k4/9/3R5/9/9/4P4/9/9/9/4K4 w - - 0 1",
            "d7e7 e9d9 e7d7 d9e9 d7e7 e9d9 e7d7 d9e9");
        var withHistory = await Read(rules, game);
        Assert.Equal(new[] { "d7e7" }, withHistory.Excluded);
        Assert.DoesNotContain("d7e7", withHistory.Allowed);
        var fenOnly = Play(game.CurrentFen(), "");
        Assert.Contains("d7e7", (await Read(rules, fenOnly)).Allowed);
        Assert.Contains("d7e7", (await Read(rules, game)).Excluded);
        Assert.True(game.GoToPly(4));
        Assert.Contains("d7e7", (await Read(rules, game)).Allowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PerpetualChaseRespectsNativeProtectedPieceException(bool protectedPiece)
    {
        await using var rules = new PikafishRulesClient();
        var game = Play((protectedPiece ? "1r2k4" : "4k4") + "/9/9/1n7/9/3RP4/9/9/9/4K4 w - - 0 1",
            "d4b4 b6d7 b4d4 d7b6 d4b4 b6d7 b4d4 d7b6");
        var result = await Read(rules, game);
        if (protectedPiece) Assert.Contains("d4b4", result.Allowed);
        else Assert.Contains("d4b4", result.Excluded);
    }

    [Fact]
    public async Task FifthConsecutiveCheckWithoutRepeatedPositionsRemainsAllowed()
    {
        await using var rules = new PikafishRulesClient();
        var game = Play("3k5/9/9/2R6/9/4P4/9/9/9/4K4 w - - 0 1",
            "c6d6 d9e9 d6e6 e9f9 e6f6 f9e9 f6f9 e9e8");
        Assert.All(game.AppliedMoves.Where(move => XiangqiGame.IsRed(move.Piece)), move => Assert.True(move.IsCheck));
        Assert.Contains("f9f8", (await Read(rules, game)).Allowed);
        Assert.True(game.TryMoveUci("f9f8", out var fifth));
        Assert.True(fifth.IsCheck);
    }

    [Fact]
    public async Task NativeDrawRepetitionsAreAllowedRatherThanForcedIntoDifferentMoves()
    {
        await using var rules = new PikafishRulesClient();
        var game = Play(XiangqiGame.InitialFen, "b0c2 b9c7 c2b0 c7b9 b0c2 b9c7 c2b0 c7b9");
        var result = await Read(rules, game);
        Assert.Contains("b0c2", result.Allowed);
        Assert.Empty(result.Excluded);
        Assert.Equal(GameResult.Ongoing, game.Result);
    }

    [Fact]
    public async Task InvalidHistoryAndMismatchedCurrentPositionDoNotFallBackToUnfilteredMoves()
    {
        await using var rules = new PikafishRulesClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => rules.GetAllowedMovesAsync(
            XiangqiGame.InitialFen, "b0b2", XiangqiGame.InitialFen));
        var game = Play(XiangqiGame.InitialFen, "a3a4");
        await Assert.ThrowsAsync<InvalidOperationException>(() => rules.GetAllowedMovesAsync(
            game.StartFen, game.UciMoveList, XiangqiGame.InitialFen));
        Assert.NotEmpty((await Read(rules, game)).Allowed);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rules.GetAllowedMovesAsync(
            game.StartFen, game.UciMoveList, game.CurrentFen(), cancelled.Token));
    }
}
