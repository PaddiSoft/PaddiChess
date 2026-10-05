using PaddiXiangqi.Core;

namespace PaddiXiangqi.Engine;

public static class AnalysisFormatter
{
    public static string ScoreForPlayers(EngineInfo info, bool redToMove)
    {
        if (info.Mate is int mate)
        {
            var winnerIsRed = (mate >= 0) == redToMove;
            return $"{(winnerIsRed ? "红" : "黑")}方杀 {Math.Abs(mate)}";
        }
        if (info.Centipawns is not int cp) return "—";
        return ScoreForRed(redToMove ? (double)cp : -(double)cp);
    }

    // UCI's `score cp` is an integer channel, not a guarantee that an engine
    // uses pawn units. Pikafish builds exposing ScoreType default to Elo.
    public static string ScoreForRed(double score) => Math.Abs(score) < .5 ? "均势 0 分"
        : $"{(score > 0 ? "红" : "黑")}优 {Math.Abs(score):0} 分";

    public static string Variation(XiangqiGame position, string pv, int maxMoves)
    {
        if (maxMoves <= 0) return "—";
        var replay = new XiangqiGame();
        replay.LoadFen(position.CurrentFen());
        var moves = pv.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var notation = new List<string>(Math.Min(maxMoves, moves.Length));
        foreach (var uci in moves.Take(maxMoves))
        {
            if (!replay.TryMoveUci(uci, out var move)) break;
            notation.Add(move.Notation + (move.IsCheck ? " 将" : ""));
        }
        if (notation.Count == 0) return "—";
        return string.Join("  ·  ", notation) + (moves.Length > maxMoves ? "  …" : "");
    }
}
