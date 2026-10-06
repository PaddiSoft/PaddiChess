using System.Collections.Frozen;

namespace PaddiXiangqi.Core;

public readonly record struct OpeningPosition(int Ply, bool RedToMove);

/// <summary>
/// Recognizes startpos and its first complete round without inventing imported
/// move history. Later middlegame screenshots still require observed history.
/// </summary>
public static class OpeningPositions
{
    private static readonly string InitialBoard = XiangqiGame.InitialFen.Split(' ')[0];
    private static readonly Lazy<FrozenDictionary<string, OpeningPosition>> FirstRound = new(Build);

    public static OpeningPosition? Find(string fen)
    {
        var board = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (board == InitialBoard) return new(0, true);
        return board is not null && FirstRound.Value.TryGetValue(board, out var position) ? position : null;
    }

    private static FrozenDictionary<string, OpeningPosition> Build()
    {
        var positions = new Dictionary<string, OpeningPosition>(StringComparer.Ordinal);
        var game = new XiangqiGame();
        foreach (var first in game.AllLegalMoves().ToArray())
        {
            if (!game.TryMoveUci(first.Uci, out _)) continue;
            Add(1);
            foreach (var reply in game.AllLegalMoves().ToArray())
            {
                if (!game.TryMoveUci(reply.Uci, out _)) continue;
                Add(2);
                game.Undo();
            }
            game.Undo();
        }
        return positions.ToFrozenDictionary(StringComparer.Ordinal);

        void Add(int ply) => positions[game.CurrentFen().Split(' ')[0]] = new(ply, game.RedToMove);
    }
}
