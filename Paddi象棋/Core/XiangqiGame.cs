using System.Text;

namespace PaddiXiangqi.Core;

public readonly record struct Square(int File, int Rank)
{
    public bool IsValid => File is >= 0 and < 9 && Rank is >= 0 and < 10;
    public string Uci => $"{(char)('a' + File)}{9 - Rank}";

    public static bool TryParseUci(string text, out Square square)
    {
        square = default;
        if (text.Length != 2 || text[0] is < 'a' or > 'i' || text[1] is < '0' or > '9')
            return false;
        square = new Square(text[0] - 'a', 9 - (text[1] - '0'));
        return true;
    }
}

public readonly record struct ChessMove(Square From, Square To, char Piece = '\0', char Captured = '\0', string Notation = "", bool IsCheck = false)
{
    public string Uci => From.Uci + To.Uci;
}

public enum GameResult { Ongoing, RedWins, BlackWins, Draw }

public sealed class XiangqiGame
{
    public const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";
    private sealed record Position(char[,] Board, bool RedToMove, ChessMove? LastMove);
    private readonly List<Position> _positions = [];
    private readonly List<ChessMove> _moves = [];
    private int _ply;
    private int _initialHalfmoves;
    private int _initialFullmove = 1;
    private bool _initialRedToMove = true;
    private int? _agreedDrawPly;
    private Position? _resultPosition;
    private int? _resultDrawPly;
    private GameResult _cachedResult;
    private bool _externalAdjudication;

    public XiangqiGame() => LoadFen(InitialFen);

    /// <summary>
    /// Records the source of a game for saved-record compatibility. All positions use
    /// basic movement legality here. History adjudication belongs to the engine or the
    /// remote game and must not prevent recording an observed continuation.
    /// </summary>
    public bool ExternalAdjudication
    {
        get => _externalAdjudication;
        set
        {
            if (_externalAdjudication == value) return;
            _externalAdjudication = value;
            _resultPosition = null;
        }
    }

    public string StartFen { get; private set; } = InitialFen;
    public char[,] Board => _positions[_ply].Board;
    public bool RedToMove => _positions[_ply].RedToMove;
    public ChessMove? LastMove => _positions[_ply].LastMove;
    public int Ply => _ply;
    public int TotalPly => _moves.Count;
    public int FullmoveNumber => _initialFullmove + (_initialRedToMove ? _ply / 2 : (_ply + 1) / 2);
    public int? AgreedDrawPly => _agreedDrawPly;
    public bool CanUndo => _ply > 0;
    public bool CanRedo => _ply < _moves.Count;
    public IReadOnlyList<ChessMove> AppliedMoves => _moves.Take(_ply).ToArray();
    public IReadOnlyList<ChessMove> History => _moves;
    public string UciMoveList => string.Join(' ', _moves.Take(_ply).Select(m => m.Uci));

    public void NewGame() => LoadFen(InitialFen);

    public void LoadFen(string fen)
    {
        var fields = fen.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 || fields[1] is not ("w" or "b"))
            throw new FormatException("FEN 需要棋盘和行棋方（w 或 b）。");
        var ranks = fields[0].Split('/');
        if (ranks.Length != 10)
            throw new FormatException("象棋 FEN 必须有 10 横行。");
        var board = new char[10, 9];
        for (var rank = 0; rank < 10; rank++)
        {
            var file = 0;
            foreach (var c in ranks[rank])
            {
                if (c is >= '1' and <= '9') file += c - '0';
                else if ("rnbakcpRNBAKCP".Contains(c))
                {
                    if (file >= 9) throw new FormatException("FEN 横行超出 9 路。");
                    board[rank, file++] = c;
                }
                else throw new FormatException($"FEN 中有未知棋子：{c}");
            }
            if (file != 9) throw new FormatException("FEN 每横行必须恰好有 9 路。");
        }
        if (CountPieces(board, 'K') != 1 || CountPieces(board, 'k') != 1)
            throw new FormatException("FEN 必须各有一枚红帅和黑将。");
        StartFen = fields.Length >= 6 ? fen.Trim() : $"{fields[0]} {fields[1]} - - 0 1";
        _initialRedToMove = fields[1] == "w";
        _initialHalfmoves = fields.Length >= 5 && int.TryParse(fields[4], out var halfmove) ? Math.Max(0, halfmove) : 0;
        _initialFullmove = fields.Length >= 6 && int.TryParse(fields[5], out var fullmove) ? Math.Max(1, fullmove) : 1;
        _moves.Clear();
        _agreedDrawPly = null;
        _positions.Clear();
        _positions.Add(new Position(board, fields[1] == "w", null));
        _ply = 0;
    }

    public string CurrentFen()
    {
        var parts = new List<string>();
        for (var rank = 0; rank < 10; rank++)
        {
            var line = new StringBuilder();
            var empty = 0;
            for (var file = 0; file < 9; file++)
            {
                var piece = Board[rank, file];
                if (piece == '\0') { empty++; continue; }
                if (empty != 0) { line.Append(empty); empty = 0; }
                line.Append(piece);
            }
            if (empty != 0) line.Append(empty);
            parts.Add(line.ToString());
        }
        var halfmoves = _initialHalfmoves;
        for (var i = 0; i < _ply; i++)
            halfmoves = char.ToUpperInvariant(_moves[i].Piece) == 'P' || _moves[i].Captured != '\0' ? 0 : halfmoves + 1;
        return $"{string.Join('/', parts)} {(RedToMove ? 'w' : 'b')} - - {halfmoves} {FullmoveNumber}";
    }

    public IReadOnlyList<ChessMove> LegalMovesFrom(Square from)
    {
        if (!from.IsValid || Result != GameResult.Ongoing) return [];
        var piece = Board[from.Rank, from.File];
        if (piece == '\0' || IsRed(piece) != RedToMove) return [];
        return PseudoMoves(Board, from).Where(to => IsLegalCurrentMove(from, to, RedToMove))
            .Select(to => new ChessMove(from, to, piece, Board[to.Rank, to.File])).ToArray();
    }

    public IReadOnlyList<ChessMove> AllLegalMoves()
    {
        var result = new List<ChessMove>();
        for (var rank = 0; rank < 10; rank++)
        for (var file = 0; file < 9; file++)
        {
            var piece = Board[rank, file];
            if (piece == '\0' || IsRed(piece) != RedToMove) continue;
            var from = new Square(file, rank);
            foreach (var to in PseudoMoves(Board, from))
                if (IsLegalCurrentMove(from, to, RedToMove))
                    result.Add(new ChessMove(from, to, piece, Board[to.Rank, to.File]));
        }
        return result;
    }

    /// <summary>
    /// Basic move candidates for a controller. Pikafish receives the full history and
    /// applies its own check/chase/repetition rules during search. A model's candidate
    /// list validates piece movement and king safety, not tournament history rules.
    /// </summary>
    public IReadOnlyList<ChessMove> AllControllerLegalMoves()
    {
        if (Result != GameResult.Ongoing) return [];
        return AllLegalMoves();
    }

    public bool TryMove(Square from, Square to, out ChessMove move)
    {
        move = default;
        if (!from.IsValid || !to.IsValid || Result != GameResult.Ongoing) return false;
        var piece = Board[from.Rank, from.File];
        if (piece == '\0' || IsRed(piece) != RedToMove || !IsLegalCurrentMove(from, to, RedToMove)) return false;

        var captured = Board[to.Rank, to.File];
        var notation = Notation(Board, from, to, piece);
        var board = (char[,])Board.Clone();
        board[to.Rank, to.File] = piece;
        board[from.Rank, from.File] = '\0';
        var check = IsInCheck(board, !RedToMove);
        move = new ChessMove(from, to, piece, captured, notation, check);
        if (_ply < _moves.Count)
        {
            _moves.RemoveRange(_ply, _moves.Count - _ply);
            _positions.RemoveRange(_ply + 1, _positions.Count - _ply - 1);
            if (_agreedDrawPly > _ply) _agreedDrawPly = null;
        }
        _moves.Add(move);
        _positions.Add(new Position(board, !RedToMove, move));
        _ply++;
        return true;
    }

    public bool TryMoveUci(string uci, out ChessMove move)
    {
        move = default;
        return uci.Length == 4 && Square.TryParseUci(uci[..2], out var from)
            && Square.TryParseUci(uci[2..], out var to) && TryMove(from, to, out move);
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        _ply--;
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        _ply++;
        return true;
    }

    public bool GoToPly(int ply)
    {
        if (ply < 0 || ply > _moves.Count) return false;
        _ply = ply;
        return true;
    }

    /// <summary>Record a mutually accepted draw at the current position.</summary>
    public bool DeclareDraw()
    {
        if (_ply == 0 || _ply != _moves.Count || Result != GameResult.Ongoing) return false;
        _agreedDrawPly = _ply;
        return true;
    }

    public bool SideInCheck => IsInCheck(Board, RedToMove);

    public GameResult Result
    {
        get
        {
            var position = _positions[_ply];
            if (ReferenceEquals(_resultPosition, position) && _resultDrawPly == _agreedDrawPly) return _cachedResult;
            _cachedResult = CalculateResult();
            _resultPosition = position;
            _resultDrawPly = _agreedDrawPly;
            return _cachedResult;
        }
    }

    // A Position and its preceding history do not change. Moving, loading a FEN, or branching
    // creates a different Position; a draw agreement is the only separate result dependency.
    private GameResult CalculateResult()
    {
        if (_agreedDrawPly == _ply) return GameResult.Draw;
        if (FindKing(Board, true) is null) return GameResult.BlackWins;
        if (FindKing(Board, false) is null) return GameResult.RedWins;
        for (var rank = 0; rank < 10; rank++)
        for (var file = 0; file < 9; file++)
        {
            var piece = Board[rank, file];
            if (piece == '\0' || IsRed(piece) != RedToMove) continue;
            var from = new Square(file, rank);
            // History restrictions select our next move. They cannot establish a win
            // for either side while the board still has a geometrically legal response.
            if (PseudoMoves(Board, from).Any(to => IsLegal(Board, from, to, RedToMove)))
                return GameResult.Ongoing;
        }
        return RedToMove ? GameResult.BlackWins : GameResult.RedWins;
    }

    public static bool IsRed(char piece) => char.IsUpper(piece);

    public static char DisplayPiece(char piece) => char.ToUpperInvariant(piece) switch
    {
        'R' => '車', 'N' => '馬', 'B' => IsRed(piece) ? '相' : '象',
        'A' => IsRed(piece) ? '仕' : '士', 'K' => IsRed(piece) ? '帥' : '將',
        'C' => '炮', 'P' => IsRed(piece) ? '兵' : '卒', _ => '?'
    };

    // Side-specific board lettering; record notation retains the familiar 車 / 馬 / 炮.
    public static char DisplayBoardPiece(char piece) => piece switch
    {
        'R' => '俥', 'N' => '傌', 'c' => '砲', _ => DisplayPiece(piece)
    };

    private static int CountPieces(char[,] board, char piece)
    {
        var count = 0;
        foreach (var p in board) if (p == piece) count++;
        return count;
    }

    private static bool IsLegal(char[,] board, Square from, Square to, bool red)
    {
        if (!to.IsValid || !PseudoMoves(board, from).Contains(to)) return false;
        var next = (char[,])board.Clone();
        next[to.Rank, to.File] = next[from.Rank, from.File];
        next[from.Rank, from.File] = '\0';
        return !IsInCheck(next, red);
    }

    private bool IsLegalCurrentMove(Square from, Square to, bool red) => IsLegal(Board, from, to, red);

    public static bool IsInCheck(char[,] board, bool red)
    {
        var king = FindKing(board, red);
        if (king is null) return true;
        for (var rank = 0; rank < 10; rank++)
        for (var file = 0; file < 9; file++)
        {
            var piece = board[rank, file];
            if (piece == '\0' || IsRed(piece) == red) continue;
            if (PseudoMoves(board, new Square(file, rank)).Contains(king.Value)) return true;
        }
        return false;
    }

    public static Square? FindKing(char[,] board, bool red)
    {
        var target = red ? 'K' : 'k';
        for (var rank = 0; rank < 10; rank++)
        for (var file = 0; file < 9; file++)
            if (board[rank, file] == target) return new Square(file, rank);
        return null;
    }

    private static IEnumerable<Square> PseudoMoves(char[,] board, Square from)
    {
        var piece = board[from.Rank, from.File];
        if (piece == '\0') yield break;
        var red = IsRed(piece);
        var f = from.File;
        var r = from.Rank;
        bool CanLand(int nf, int nr) => nf is >= 0 and < 9 && nr is >= 0 and < 10 &&
            (board[nr, nf] == '\0' || IsRed(board[nr, nf]) != red);

        switch (char.ToUpperInvariant(piece))
        {
            case 'R':
            case 'C':
                foreach (var (df, dr) in Orthogonal)
                {
                    var screen = false;
                    for (int nf = f + df, nr = r + dr; nf is >= 0 and < 9 && nr is >= 0 and < 10; nf += df, nr += dr)
                    {
                        var target = board[nr, nf];
                        if (char.ToUpperInvariant(piece) == 'R')
                        {
                            if (CanLand(nf, nr)) yield return new Square(nf, nr);
                            if (target != '\0') break;
                        }
                        else if (!screen)
                        {
                            if (target == '\0') yield return new Square(nf, nr);
                            else screen = true;
                        }
                        else if (target != '\0')
                        {
                            if (IsRed(target) != red) yield return new Square(nf, nr);
                            break;
                        }
                    }
                }
                break;

            case 'N':
                foreach (var (df, dr, lf, lr) in HorseJumps)
                {
                    var nf = f + df; var nr = r + dr;
                    if (CanLand(nf, nr) && board[r + lr, f + lf] == '\0')
                        yield return new Square(nf, nr);
                }
                break;

            case 'B':
                foreach (var (df, dr) in Diagonal)
                {
                    var nf = f + 2 * df; var nr = r + 2 * dr;
                    if (CanLand(nf, nr) && (red ? nr >= 5 : nr <= 4) && board[r + dr, f + df] == '\0')
                        yield return new Square(nf, nr);
                }
                break;

            case 'A':
                foreach (var (df, dr) in Diagonal)
                {
                    var nf = f + df; var nr = r + dr;
                    if (CanLand(nf, nr) && InPalace(nf, nr, red)) yield return new Square(nf, nr);
                }
                break;

            case 'K':
                foreach (var (df, dr) in Orthogonal)
                {
                    var nf = f + df; var nr = r + dr;
                    if (CanLand(nf, nr) && InPalace(nf, nr, red)) yield return new Square(nf, nr);
                }
                for (var nr = r + (red ? -1 : 1); nr is >= 0 and < 10; nr += red ? -1 : 1)
                {
                    if (board[nr, f] == '\0') continue;
                    if (board[nr, f] == (red ? 'k' : 'K')) yield return new Square(f, nr);
                    break;
                }
                break;

            case 'P':
                var nextRank = r + (red ? -1 : 1);
                if (CanLand(f, nextRank)) yield return new Square(f, nextRank);
                if (red ? r <= 4 : r >= 5)
                {
                    if (CanLand(f - 1, r)) yield return new Square(f - 1, r);
                    if (CanLand(f + 1, r)) yield return new Square(f + 1, r);
                }
                break;
        }
    }

    private static bool InPalace(int file, int rank, bool red) =>
        file is >= 3 and <= 5 && (red ? rank is >= 7 and <= 9 : rank is >= 0 and <= 2);

    private static readonly (int df, int dr)[] Orthogonal = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private static readonly (int df, int dr)[] Diagonal = [(1, 1), (-1, 1), (1, -1), (-1, -1)];
    private static readonly (int df, int dr, int lf, int lr)[] HorseJumps =
    [
        (2, 1, 1, 0), (2, -1, 1, 0), (-2, 1, -1, 0), (-2, -1, -1, 0),
        (1, 2, 0, 1), (-1, 2, 0, 1), (1, -2, 0, -1), (-1, -2, 0, -1)
    ];

    private static string Notation(char[,] board, Square from, Square to, char piece)
    {
        var red = IsRed(piece);
        var numbers = new[] { "一", "二", "三", "四", "五", "六", "七", "八", "九" };
        string FileName(int file) => numbers[(red ? 8 - file : file)];
        var name = DisplayPiece(piece).ToString();
        var sameFile = Enumerable.Range(0, 10).Where(rank => board[rank, from.File] == piece).ToArray();
        var prefix = sameFile.Length > 1 ? (red ? from.Rank < sameFile.Max() ? "前" : "后" : from.Rank > sameFile.Min() ? "前" : "后") : "";
        var head = prefix.Length > 0 ? prefix + name : name + FileName(from.File);
        if (from.Rank == to.Rank) return head + "平" + FileName(to.File);
        var action = (to.Rank < from.Rank) == red ? "进" : "退";
        var tail = "NBA".Contains(char.ToUpperInvariant(piece)) ? FileName(to.File) : numbers[Math.Abs(to.Rank - from.Rank) - 1];
        return head + action + tail;
    }
}
