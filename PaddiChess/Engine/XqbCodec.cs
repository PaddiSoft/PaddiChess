using PaddiXiangqi.Core;

namespace PaddiXiangqi.Engine;

// XQB v1 wire format: https://github.com/fisherfan/xqbook
// Piece codes are relative to the player to move; canonicalize before bit packing.
public sealed record XqbKey(byte[] Bytes, bool Rotated, bool Mirrored)
{
    public string DecodeMove(int value)
    {
        if (value is < 0 or > 65535) return "";
        Square Transform(int row, int col)
        {
            if (Mirrored) col = 8 - col;
            if (Rotated) { row = 9 - row; col = 8 - col; }
            return new(col, row);
        }
        var from = Transform(value >> 12, (value >> 8) & 15);
        var to = Transform((value >> 4) & 15, value & 15);
        return from.IsValid && to.IsValid && from != to ? from.Uci + to.Uci : "";
    }
}

public static class XqbCodec
{
    public static XqbKey Encode(string fen)
    {
        var game = new XiangqiGame(); game.LoadFen(fen);
        var cells = new int[90];
        for (var i = 0; i < 90; i++)
        {
            var source = game.RedToMove ? i : 89 - i;
            var piece = game.Board[source / 9, source % 9];
            var code = char.ToUpperInvariant(piece) switch
            { 'R' => 1, 'N' => 2, 'B' => 3, 'A' => 4, 'K' => 5, 'C' => 6, 'P' => 7, _ => -1 };
            cells[i] = code < 0 ? -1 : code + (char.IsUpper(piece) == game.RedToMove ? 0 : 8);
        }
        bool mirror = false, decided = false;
        for (var r = 0; r < 10 && !decided; r++)
        for (var c = 0; c < 4 && !decided; c++)
            if (cells[r * 9 + c] != cells[r * 9 + 8 - c])
            { mirror = cells[r * 9 + 8 - c] > cells[r * 9 + c]; decided = true; }
        var bytes = new byte[57]; var bit = 0;
        void Write(int value, int count)
        {
            for (var b = count - 1; b >= 0; b--, bit++)
                if ((value & (1 << b)) != 0) bytes[bit / 8] |= (byte)(1 << (7 - bit % 8));
        }
        for (var i = 0; i < 90; i++)
        {
            var code = cells[(i / 9) * 9 + (mirror ? 8 - i % 9 : i % 9)];
            if (code < 0) Write(0, 1); else Write(16 | code, 5);
        }
        return new(bytes[..((bit + 7) / 8)], !game.RedToMove, mirror);
    }
}
