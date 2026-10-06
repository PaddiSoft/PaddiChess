namespace PaddiXiangqi.Engine;

/// <summary>Applies the bundled engine's casual difficulty policy to a frozen position.</summary>
public static class EngineMoveSelector
{
    public static string Select(SearchResult result, int level, bool playMove, bool bundled,
        IReadOnlySet<string> legalMoves, IReadOnlyList<string>? nativeAllowed = null)
    {
        // Other UCI engines own their strength and adjudication. Never replace their
        // selected move with a random line from a different rules implementation.
        if (!bundled) return result.BestMove;
        var bestAllowed = legalMoves.Contains(result.BestMove) &&
            (nativeAllowed is null || nativeAllowed.Contains(result.BestMove));
        if ((!playMove || level >= 12 || result.Candidates.Count <= 1) && bestAllowed) return result.BestMove;
        var choices = result.Candidates.Where(c => legalMoves.Contains(c.FirstMove) &&
                (nativeAllowed is null || nativeAllowed.Contains(c.FirstMove)))
            .OrderBy(c => c.MultiPv).ToArray();
        if (choices.Length == 0)
            return bestAllowed ? result.BestMove : throw new InvalidOperationException("引擎候选没有通过规则校验，已暂停自动走棋。");
        if (bestAllowed && level >= 9 && Random.Shared.NextDouble() < .75) return result.BestMove;
        if (bestAllowed && level >= 6 && Random.Shared.NextDouble() < .5) return result.BestMove;
        return choices[Random.Shared.Next(choices.Length)].FirstMove;
    }
}
