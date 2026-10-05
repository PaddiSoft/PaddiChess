using System.Text;

namespace PaddiXiangqi.Core;

public static class PositionSetup
{
    private static readonly Dictionary<char, int> MaximumPieces = new()
    {
        ['R'] = 2, ['N'] = 2, ['B'] = 2, ['A'] = 2, ['K'] = 1, ['C'] = 2, ['P'] = 5
    };

    public static int MaximumCount(char piece) => MaximumPieces.GetValueOrDefault(char.ToUpperInvariant(piece));

    public static int CountPieces(char[,] board, char piece)
    {
        var count = 0;
        foreach (var current in board) if (current == piece) count++;
        return count;
    }

    /// <summary>Invariant squares reachable by a piece, independent of move history.</summary>
    public static bool CanOccupySquare(char piece, Square square)
    {
        if (square.File is < 0 or > 8 || square.Rank is < 0 or > 9) return false;
        if (piece == '\0') return true;
        bool red = char.IsUpper(piece);
        int file = square.File, rank = red ? 9 - square.Rank : square.Rank;
        return char.ToUpperInvariant(piece) switch
        {
            'R' or 'N' or 'C' => true,
            'K' => file is >= 3 and <= 5 && rank <= 2,
            'A' => (rank is 0 or 2 && file is 3 or 5) || rank == 1 && file == 4,
            'B' => (rank is 0 or 4 && file is 2 or 6) || rank == 2 && file is 0 or 4 or 8,
            'P' => rank >= 3 && (rank >= 5 || file % 2 == 0),
            _ => false
        };
    }

    public static string BuildFen(char[,] board, bool redToMove)
    {
        Validate(board);
        var ranks = new string[10];
        for (var rank = 0; rank < 10; rank++)
        {
            var line = new StringBuilder(9);
            var empty = 0;
            for (var file = 0; file < 9; file++)
            {
                var piece = board[rank, file];
                if (piece == '\0') { empty++; continue; }
                if (empty > 0) { line.Append(empty); empty = 0; }
                line.Append(piece);
            }
            if (empty > 0) line.Append(empty);
            ranks[rank] = line.ToString();
        }
        return $"{string.Join('/', ranks)} {(redToMove ? 'w' : 'b')} - - 0 1";
    }

    public static void Validate(char[,] board)
    {
        if (board.GetLength(0) != 10 || board.GetLength(1) != 9)
            throw new FormatException("棋盘必须是十行九路。");

        var counts = new Dictionary<char, int>();
        for (var rank = 0; rank < 10; rank++)
        for (var file = 0; file < 9; file++)
        {
            var piece = board[rank, file];
            if (piece == '\0') continue;
            var type = char.ToUpperInvariant(piece);
            if (!MaximumPieces.TryGetValue(type, out var maximum))
                throw new FormatException($"棋盘中有未知棋子：{piece}");
            counts[piece] = counts.GetValueOrDefault(piece) + 1;
            if (counts[piece] > maximum)
                throw new FormatException($"{(char.IsUpper(piece) ? "红" : "黑")}{XiangqiGame.DisplayPiece(piece)}超过允许数量 {maximum}。");

            var red = char.IsUpper(piece);
            if ((type == 'K' || type == 'A') && (file < 3 || file > 5 || (red ? rank < 7 : rank > 2)))
                throw new FormatException("将、帅和士、仕必须摆在各自九宫内。");
            if (type == 'B' && (red ? rank < 5 : rank > 4))
                throw new FormatException("象、相不能摆在对方半场。");
            if (type == 'P' && (red ? rank > 6 : rank < 3))
                throw new FormatException("兵、卒不能摆在出发线后方。");
        }
        if (counts.GetValueOrDefault('K') != 1 || counts.GetValueOrDefault('k') != 1)
            throw new FormatException("必须各摆一枚红帅和黑将。");

        var redKing = XiangqiGame.FindKing(board, true)!.Value;
        var blackKing = XiangqiGame.FindKing(board, false)!.Value;
        if (redKing.File == blackKing.File)
        {
            var blocked = false;
            for (var rank = blackKing.Rank + 1; rank < redKing.Rank; rank++)
                blocked |= board[rank, redKing.File] != '\0';
            if (!blocked) throw new FormatException("帅和将不能在同一路上直接照面。");
        }
    }
}
