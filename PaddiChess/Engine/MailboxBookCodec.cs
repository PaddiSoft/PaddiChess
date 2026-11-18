using PaddiXiangqi.Core;

namespace PaddiXiangqi.Engine;

internal static class MailboxBookCodec
{
    internal static int PieceIndex(char piece) => char.ToUpperInvariant(piece) switch
    { 'K' => 0, 'A' => 1, 'B' => 2, 'N' => 3, 'R' => 4, 'C' => 5, 'P' => 6, _ => -1 };

    internal static string Decode(int encoded, bool sourceHigh, bool mirrored)
    {
        if (encoded is < 0 or > 65535) return "";
        Square SquareAt(int mailbox)
        {
            var col = (mailbox & 15) - 3;
            return new(mirrored ? 8 - col : col, (mailbox >> 4) - 3);
        }
        var from = SquareAt(sourceHigh ? encoded >> 8 : encoded & 255);
        var to = SquareAt(sourceHigh ? encoded & 255 : encoded >> 8);
        return from.IsValid && to.IsValid && from != to ? from.Uci + to.Uci : "";
    }
}

public static partial class BingheBookCodec
{
    public static ulong Key(string fen, bool mirrored = false)
    {
        var game = new XiangqiGame(); game.LoadFen(fen);
        ulong key = game.RedToMove ? 0xA0CE2AF90C452F58UL : 0;
        for (var row = 0; row < 10; row++)
        for (var col = 0; col < 9; col++)
        {
            var piece = game.Board[row, col]; var type = MailboxBookCodec.PieceIndex(piece);
            if (type >= 0) key ^= Pieces[(type + (char.IsUpper(piece) ? 0 : 7)) * 90 + row * 9 + (mirrored ? 8 - col : col)];
        }
        return key;
    }
}

/// <summary>ElephantEye's fixed RC4-zero Zobrist sequence, not Polyglot's chess keys.</summary>
public static class ElephantEyeBookCodec
{
    private static readonly uint[] Keys = BuildKeys();
    public static uint Key(string fen, bool mirrored = false)
    {
        var game = new XiangqiGame(); game.LoadFen(fen);
        var key = game.RedToMove ? 0 : Keys[0];
        for (var row = 0; row < 10; row++)
        for (var col = 0; col < 9; col++)
        {
            var piece = game.Board[row, col]; var type = MailboxBookCodec.PieceIndex(piece);
            if (type >= 0) key ^= Keys[1 + (type + (char.IsUpper(piece) ? 0 : 7)) * 256 +
                (row + 3) * 16 + (mirrored ? 8 - col : col) + 3];
        }
        return key;
    }
    private static uint[] BuildKeys()
    {
        var state = Enumerable.Range(0, 256).ToArray(); var j = 0;
        for (var i = 0; i < 256; i++) { j = (j + state[i]) & 255; (state[i], state[j]) = (state[j], state[i]); }
        var x = 0; var y = 0;
        byte Next()
        {
            x = (x + 1) & 255; y = (y + state[x]) & 255;
            (state[x], state[y]) = (state[y], state[x]);
            return (byte)state[(state[x] + state[y]) & 255];
        }
        var keys = new uint[1 + 14 * 256];
        for (var i = 0; i < keys.Length; i++)
        {
            for (var skip = 0; skip < 8; skip++) Next(); // dwKey, dwLock0
            for (var b = 0; b < 4; b++) keys[i] |= (uint)Next() << (8 * b); // dwLock1, little endian
        }
        return keys;
    }
}
