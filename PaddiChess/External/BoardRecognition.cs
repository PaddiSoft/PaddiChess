using PaddiXiangqi.Core;
using SkiaSharp;
using System.Numerics;

namespace PaddiXiangqi.External;

/// <summary>Coordinates are in screenshot pixels, with corners at the outer intersection centres.</summary>
public sealed record BoardCalibration(double Left, double Top, double Right, double Bottom, bool RedAtTop)
{
    public (double X, double Y) Point(Square square)
    {
        var file = RedAtTop ? 8 - square.File : square.File;
        var rank = RedAtTop ? 9 - square.Rank : square.Rank;
        return (Left + file * (Right - Left) / 8, Top + rank * (Bottom - Top) / 9);
    }
    public void Validate(int width, int height)
    {
        var dx = (Right - Left) / 8; var dy = (Bottom - Top) / 9;
        if (dx < 12 || dy < 12 || dx / dy is < .65 or > 1.5 || Left < dx * .35 || Top < dy * .35 ||
            Right + dx * .35 >= width || Bottom + dy * .35 >= height)
            throw new InvalidOperationException("请点击棋盘左上、右下两个交叉点的中心（不要点棋盘外框），并保留完整棋子。 ");
    }
}

public sealed class BoardObservation
{
    public float[][] Cells { get; }
    // Selected discs may lift above their intersection without making a move.
    // Alternative samples stay within the same square; empty squares use the fixed grid centre.
    private readonly float[][]?[] _liftedCells = new float[90][][];
    private readonly byte[]? _png;
    private readonly CapturedPixels? _pixels;
    private readonly BoardCalibration _calibration;
    public float[][][] LiftedCells
    {
        get { PrepareLiftedCells(Enumerable.Range(0, 90)); return _liftedCells.Select(cell => cell!).ToArray(); }
    }
    public int Width { get; }
    public int Height { get; }
    private BoardObservation(float[][] cells, byte[]? png, BoardCalibration calibration, int width, int height, CapturedPixels? pixels = null)
    { Cells = cells; _png = png; _pixels = pixels; _calibration = calibration; Width = width; Height = height; }
    public static BoardObservation Read(ExternalFrame frame, BoardCalibration calibration, BoardObservation? previous = null)
    {
        if (frame.Pixels is not { } pixels) return Read(frame.Png, calibration, previous);
        return Read(pixels, calibration, previous);
    }
    public static BoardObservation Read(CapturedPixels pixels, BoardCalibration calibration, BoardObservation? previous = null)
    {
        pixels.Validate();
        var patches = ReadPatches(null, calibration, false, out var width, out var height,
            previous: previous?._calibration == calibration ? previous : null, rawPixels: pixels);
        return new(patches.Select(p => p[0]).ToArray(), null, calibration, width, height, pixels);
    }
    public static BoardObservation Read(byte[] png, BoardCalibration calibration, BoardObservation? previous = null)
    {
        var patches = ReadPatches(png, calibration, false, out var width, out var height,
            previous: previous?._calibration == calibration ? previous : null);
        return new(patches.Select(p => p[0]).ToArray(), png, calibration, width, height);
    }
    internal void PrepareLiftedCells(IEnumerable<int> indices)
    {
        var missing = indices.Where(i => _liftedCells[i] == null).Distinct().ToArray();
        if (missing.Length == 0) return;
        // Decode once for this batch and sample only uncertain intersections.
        // One glowing/lifted disc must not allocate six patches for all 90 cells.
        var patches = ReadPatches(_png, _calibration, true, out _, out _, missing, rawPixels: _pixels);
        foreach (int i in missing) _liftedCells[i] = patches[i];
    }
    internal float[][] LiftedCell(int index) => _liftedCells[index] ?? throw new InvalidOperationException("尚未读取棋子上浮样本");
    internal bool HasLiftedCell(int index) => _liftedCells[index] != null;

    private static float[][][] ReadPatches(byte[]? png, BoardCalibration calibration, bool includeLifted, out int width, out int height,
        IReadOnlyCollection<int>? indices = null, BoardObservation? previous = null, CapturedPixels? rawPixels = null)
    {
        using var image = rawPixels == null ? SKBitmap.Decode(png) ?? throw new InvalidOperationException("截图无法读取") : null;
        width = rawPixels?.Width ?? image!.Width; height = rawPixels?.Height ?? image!.Height;
        calibration.Validate(width, height);
        if (previous is not null && (previous.Width != width || previous.Height != height)) previous = null;
        // Read opaque 32-bit pixels directly instead of making a native GetPixel
        // call for every sample. Keep the fallback for other formats/alpha.
        ReadOnlySpan<byte> pixels = rawPixels != null ? rawPixels.Bgra.AsSpan() : image!.GetPixelSpan();
        var colorType = rawPixels != null ? SKColorType.Bgra8888 : image!.ColorType;
        var rowBytes = rawPixels?.RowBytes ?? image!.RowBytes;
        var direct = colorType is SKColorType.Bgra8888 or SKColorType.Rgba8888;
        var redOffset = colorType == SKColorType.Bgra8888 ? 2 : 0;
        var blueOffset = colorType == SKColorType.Bgra8888 ? 0 : 2;
        Span<float> sample = stackalloc float[24 * 24 * 3];
        Span<int> columns = stackalloc int[24];
        Span<int> rows = stackalloc int[24];
        var lifted = new float[90][][];
        double dx = (calibration.Right - calibration.Left) / 8, dy = (calibration.Bottom - calibration.Top) / 9;
        for (var r = 0; r < 10; r++) for (var f = 0; f < 9; f++)
        {
            if (indices != null && !indices.Contains(r * 9 + f)) continue;
            var (cx, cy) = calibration.Point(new Square(f, r));
            ReadOnlySpan<double> offsets = includeLifted ? [0, -.08, -.16, -.24, .08, .16] : [0];
            var variants = new float[offsets.Length][];
            for (int x = 0; x < 24; x++) columns[x] = (int)Math.Round(cx + (x - 11.5) / 24 * dx * .66);
            for (int lift = 0; lift < variants.Length; lift++)
            {
                sample.Clear();
                for (int y = 0; y < 24; y++) rows[y] = Math.Clamp((int)Math.Round(
                    cy + (y - 11.5) / 24 * dy * .66 + offsets[lift] * dy), 0, height - 1);
                for (var y = 0; y < 24; y++) for (var x = 0; x < 24; x++)
                {
                    // Ignore disc rims/shadows and grid-dependent corners; keep the glyph core.
                    if ((x - 11.5) * (x - 11.5) + (y - 11.5) * (y - 11.5) > 121) continue;
                    var i = (y * 24 + x) * 3;
                    var offset = rows[y] * rowBytes + columns[x] * 4;
                    if (direct && (rawPixels != null || pixels[offset + 3] == 255))
                    {
                        sample[i] = pixels[offset + redOffset] / 255f;
                        sample[i + 1] = pixels[offset + 1] / 255f;
                        sample[i + 2] = pixels[offset + blueOffset] / 255f;
                    }
                    else
                    {
                        var pixel = image!.GetPixel(columns[x], rows[y]);
                        sample[i] = pixel.Red / 255f; sample[i + 1] = pixel.Green / 255f; sample[i + 2] = pixel.Blue / 255f;
                    }
                }
                // Exact equality only: never hide movement or selection animation
                // behind a similarity threshold. Reference patches remain immutable.
                var old = previous?.Cells[r * 9 + f];
                variants[lift] = old != null && sample.SequenceEqual(old) ? old : sample.ToArray();
            }
            lifted[r * 9 + f] = variants;
        }
        return lifted;
    }
    public static double Distance(float[] a, float[] b)
    {
        if (ReferenceEquals(a, b)) return 0;
        var sum = Vector<float>.Zero;
        int i = 0, width = Vector<float>.Count;
        for (; i <= a.Length - width; i += width)
            sum += Vector.Abs(new Vector<float>(a, i) - new Vector<float>(b, i));
        double total = Vector.Sum(sum);
        for (; i < a.Length; i++) total += Math.Abs(a[i] - b[i]);
        return total / a.Length;
    }
    // Last-move dots/rings occupy the centre of an empty square. Its outer annulus still identifies board texture.
    public static double EmptyDistance(float[] a, float[] b)
    {
        if (ReferenceEquals(a, b)) return 0;
        double total = 0; int count = 0;
        for (int y = 0; y < 24; y++) for (int x = 0; x < 24; x++)
        {
            double radius = (x - 11.5) * (x - 11.5) + (y - 11.5) * (y - 11.5);
            if (radius < 64 || radius > 121) continue;
            int offset = (y * 24 + x) * 3;
            total += Math.Abs(a[offset] - b[offset]) + Math.Abs(a[offset + 1] - b[offset + 1]) + Math.Abs(a[offset + 2] - b[offset + 2]);
            count += 3;
        }
        return total / count;
    }
    // Compare fixed-position glyph edges, not uniform lighting or colour pulses.
    // There is no translation search here: a moving glyph must still reset arrival.
    public static double GlyphMotionDistance(float[] a, float[] b)
    {
        if (ReferenceEquals(a, b)) return 0;
        double total = 0; int count = 0;
        ReadOnlySpan<int> neighbours = [3, 24 * 3];
        for (int y = 3; y < 21; y++) for (int x = 3; x < 21; x++)
        {
            if ((x - 11.5) * (x - 11.5) + (y - 11.5) * (y - 11.5) > 64) continue;
            int index = (y * 24 + x) * 3;
            foreach (int offset in neighbours)
                for (int channel = 0; channel < 3; channel++)
                {
                    int i = index + channel, neighbour = i + offset;
                    total += Math.Abs((a[i] - a[neighbour]) - (b[i] - b[neighbour]));
                    count++;
                }
        }
        return total / count;
    }
    public bool SettledWith(BoardObservation other)
    {
        if (Width != other.Width || Height != other.Height) return false;
        int animated = 0;
        for (int i = 0; i < 90; i++)
        {
            var delta = Distance(Cells[i], other.Cells[i]);
            if (delta > .07 || (delta > .025 && ++animated > 2)) return false;
        }
        return true;
    }
    public bool StableWith(BoardObservation other) => Width == other.Width && Height == other.Height &&
        Cells.Select((cell, i) => Distance(cell, other.Cells[i])).Max() < .025;
    public static bool? DetectRedAtTop(byte[] png, BoardCalibration geometry)
        => DetectRedAtTop(CapturedPixels.DecodePng(png), geometry);

    public static bool? DetectRedAtTop(CapturedPixels pixels, BoardCalibration geometry)
    {
        var view = Read(pixels, geometry with { RedAtTop = false });
        double Redness(float[] cell)
        {
            double sum = 0;
            for (int i = 0; i < cell.Length; i += 3)
            {
                // Saturated red discs or red glyphs; exclude warm wood and yellow highlights.
                var r = cell[i]; var g = cell[i + 1]; var b = cell[i + 2];
                if (r > g * 1.38 && r > b * 1.25 && Math.Abs(g - b) < .18) sum += r - (g + b) / 2;
            }
            return sum / (cell.Length / 3);
        }
        var top = Enumerable.Range(0, 45).Sum(i => Redness(view.Cells[i]));
        var bottom = Enumerable.Range(45, 45).Sum(i => Redness(view.Cells[i]));
        if (Math.Abs(top - bottom) < .08 || Math.Max(top, bottom) < Math.Min(top, bottom) * 1.5) return null;
        return top > bottom;
    }
}

public sealed record BoardMatch(bool Recognized, IReadOnlyList<string> Moves, double Error, string Message);

/// <summary>
/// Learns a confirmed board skin locally, then reconciles screenshots against legal successors.
/// It never guesses an arbitrary board from colours alone, or accepts an unverified engine move.
/// </summary>
public sealed class ExternalBoardTracker
{
    private BoardObservation _baseline;
    public BoardObservation ConfirmedFrame => _baseline;
    private bool[] _baselineEmpty;
    private readonly List<float[]> _empty;
    public ExternalBoardTracker(BoardObservation confirmedFrame, XiangqiGame game)
    {
        _baseline = confirmedFrame;
        _baselineEmpty = Enumerable.Range(0,90).Select(i => game.Board[i/9,i%9] == '\0').ToArray();
        _empty = Enumerable.Range(0, 90).Where(i => game.Board[i / 9, i % 9] == '\0').Select(i => confirmedFrame.Cells[i]).ToList();
        if (_empty.Count == 0) throw new InvalidOperationException("标定局面需要空交叉点");
        var extremes = confirmedFrame.Cells.SelectMany(x => x).ToArray();
        if (extremes.Max() - extremes.Min() < .1) throw new InvalidOperationException("截图没有可识别内容，请检查屏幕录制权限");
    }
    public void Accept(BoardObservation frame, XiangqiGame game)
    {
        _baseline = frame;
        _baselineEmpty = Enumerable.Range(0,90).Select(i => game.Board[i/9,i%9] == '\0').ToArray();
        // Keep samples from all grid locations, including palace and river-adjacent intersections.
        foreach (var i in Enumerable.Range(0, 90).Where(i => game.Board[i / 9, i % 9] == '\0'))
            if (_empty.All(e => BoardObservation.Distance(e, frame.Cells[i]) > .025)) _empty.Add(frame.Cells[i]);
        if (_empty.Count > 180) _empty.RemoveRange(0, _empty.Count - 180);
    }
    /// <summary>Reuse confirmed identities only where the glyph pixels are essentially unchanged.</summary>
    public SkinRecognition? RecognizeChanges(BoardObservation frame, XiangqiGame game, IEnumerable<BoardSkin> skins)
    {
        if (frame.Width != _baseline.Width || frame.Height != _baseline.Height)
            return ExternalPositionRecovery.Recognize(frame, skins, game.RedToMove);
        var knownPieces = new char?[90];
        for (int i = 0; i < 90; i++)
            if (BoardObservation.Distance(_baseline.Cells[i], frame.Cells[i]) < .004)
                knownPieces[i] = game.Board[i / 9, i % 9];
        SkinRecognition? best = null;
        foreach (var skin in skins)
        {
            var result = skin.RecognizeWithKnownPieces(frame, game.RedToMove, knownPieces);
            if (result.Confident) return result;
            if (best == null || result.Uncertain.Count < best.Uncertain.Count) best = result;
        }
        return best;
    }
    // Per-frame memoization: each patch pair is compared once, shared by all legal successors.
    private sealed class FrameScorer
    {
        private readonly BoardObservation _baseline, _frame;
        private readonly List<float[]> _empty;
        private readonly char[] _pieces;
        private readonly Dictionary<(int From, int To), double> _pairs = new();
        private readonly Dictionary<int, double> _vacant = new();
        private readonly Dictionary<(int To, char Expected), double> _otherIdentities = new();
        private readonly double[] _unchanged;
        private readonly int[] _descendingErrors;
        private readonly double _unchangedSum;
        public bool EssentiallyUnchanged => _unchanged[_descendingErrors[0]] < .004;
        public FrameScorer(BoardObservation baseline, BoardObservation frame, List<float[]> empty, bool[] baselineEmpty, char[,] board)
        {
            _baseline=baseline; _frame=frame; _empty=empty;
            _pieces = Enumerable.Range(0, 90).Select(i => board[i / 9, i % 9]).ToArray();
            _unchanged = Enumerable.Range(0, 90).Select(i => baselineEmpty[i]
                ? BoardObservation.EmptyDistance(baseline.Cells[i], frame.Cells[i])
                : BoardObservation.Distance(baseline.Cells[i], frame.Cells[i])).ToArray();
            _unchangedSum = _unchanged.Sum();
            _descendingErrors = Enumerable.Range(0,90).OrderByDescending(i => _unchanged[i]).ToArray();
        }
        public double Error(IReadOnlyList<ChessMove> moves)
        {
            // One/two plies touch at most four intersections. Keep the unchanged sum
            // and maximum once per frame instead of rescoring all 90 cells per candidate.
            Span<int> changed = stackalloc int[4];
            Span<int> sources = stackalloc int[4];
            int count=0;
            foreach (var move in moves)
            {
                int from = move.From.Rank * 9 + move.From.File, to = move.To.Rank * 9 + move.To.File;
                int fromIndex=changed[..count].IndexOf(from);
                if (fromIndex<0) { fromIndex=count; changed[count]=from; sources[count++]=from; }
                int toIndex=changed[..count].IndexOf(to);
                if (toIndex<0) { toIndex=count; changed[count]=to; sources[count++]=to; }
                sources[toIndex]=sources[fromIndex]; sources[fromIndex]=-1;
            }
            double sum = _unchangedSum, max = 0;
            foreach (var i in _descendingErrors)
                if (!changed[..count].Contains(i)) { max=_unchanged[i]; break; }
            for (int entry = 0; entry < count; entry++)
            {
                int i=changed[entry], source=sources[entry];
                double distance;
                if (source == i) distance = _unchanged[i];
                else if (source < 0)
                {
                    if (!_vacant.TryGetValue(i, out distance))
                        _vacant[i] = distance = _empty.Min(e => BoardObservation.EmptyDistance(e, _frame.Cells[i]));
                }
                else if (!_pairs.TryGetValue((source, i), out distance))
                {
                    distance = BoardObservation.Distance(_baseline.Cells[source], _frame.Cells[i]);
                    // A slightly imperfect grid changes the glyph's sampled position
                    // between its source and destination, especially across several ranks.
                    // Ivory discs can have sharp, high-contrast glyphs: that small shift
                    // produced a large RGB error and rejected otherwise legal captures.
                    // Reuse the bounded glyph-core translation matcher only for moved
                    // pieces with a large residual. Unchanged cells, vacant sources,
                    // legal successors and the confidence margin keep their strict checks.
                    if (distance > .04 && _unchanged[source] > .025 && _unchanged[i] > .025)
                    {
                        distance = Math.Min(distance, BoardSkin.GlyphDistance(_baseline.Cells[source], _frame.Cells[i]));
                        // Similar disc fills can hide a different side/type beneath
                        // the whole-board error budget. An already confirmed piece
                        // identity that fits this endpoint much better vetoes the move.
                        // Compare this board's own pixels; red ink is not assumed.
                        if (distance > .05 && OtherIdentityDistance(i, _pieces[source]) is var other &&
                            other < .025 && other + .04 < distance)
                            distance = 1;
                    }
                    _pairs[(source, i)] = distance;
                }
                sum += distance - _unchanged[i]; max = Math.Max(max, distance);
            }
            return sum / 90 + max * .6;
        }
        private double OtherIdentityDistance(int to, char expected)
        {
            if (_otherIdentities.TryGetValue((to, expected), out var cached)) return cached;
            double best = 1;
            for (int i = 0; i < _pieces.Length; i++)
                if (_pieces[i] != '\0' && _pieces[i] != expected)
                    best = Math.Min(best, BoardObservation.Distance(_baseline.Cells[i], _frame.Cells[to]));
            return _otherIdentities[(to, expected)] = best;
        }
    }
    private string? _candidateKey;
    private readonly Dictionary<(string? PendingMove, bool RecoverPair), IReadOnlyList<ChessMove[]>> _candidates = new();
    private IEnumerable<ChessMove[]> Candidates(XiangqiGame game, string? pendingMove, bool recoverMissedPair)
    {
        var key = game.StartFen + "|" + game.UciMoveList + "|" + game.AgreedDrawPly + "|" + game.ExternalAdjudication;
        if (_candidateKey != key)
        {
            _candidates.Clear();
            _candidateKey = key;
        }
        // Pixel matching first tries one ply, then a missed pair. Keep both views for this
        // position so an animation frame does not rebuild the same game tree every poll.
        var request = (pendingMove, recoverMissedPair || pendingMove != null);
        if (_candidates.TryGetValue(request, out var cached)) return cached;
        var candidates = new List<ChessMove[]>();
        if (game.Result != GameResult.Ongoing) return candidates;
        XiangqiGame? next = null;
        if (request.Item2)
        {
            // Reconstruct history once, preserving the source game's adjudication policy.
            // Undoing each tentative first move lets the following branch reuse that history.
            next = new XiangqiGame { ExternalAdjudication = game.ExternalAdjudication }; next.LoadFen(game.StartFen);
            foreach (var previous in game.AppliedMoves) next.TryMoveUci(previous.Uci, out _);
        }
        foreach (var move in game.AllLegalMoves().Where(m => pendingMove == null || m.Uci == pendingMove))
        {
            candidates.Add([move]);
            if (next == null || !next.TryMoveUci(move.Uci, out _)) continue;
            if (next.Result == GameResult.Ongoing)
                foreach (var reply in next.AllLegalMoves()) candidates.Add([move, reply]);
            next.Undo();
        }
        _candidates[request] = candidates;
        return candidates;
    }
    public BoardMatch MatchRecognizedPosition(SkinRecognition observed, XiangqiGame game, string? pendingMove = null, BoardObservation? frame = null)
    {
        if (!observed.Confident && frame != null && frame.Width == _baseline.Width && frame.Height == _baseline.Height)
        {
            // An unchanged, already-confirmed piece does not need to be re-identified from its glyph.
            // Only reuse its identity if its actual pixels are unchanged; moved squares still need recognition.
            var rows = observed.Fen.Split(' ')[0].Split('/'); var board = new char[10,9];
            for (int r=0;r<10;r++)
            {
                int f=0;
                foreach (var c in rows[r]) { if (c is >= '1' and <= '9') f+=c-'0'; else board[r,f++]=c; }
            }
            var remaining = new List<Square>();
            foreach (var square in observed.Uncertain)
            {
                int i=square.Rank*9+square.File;
                if (BoardObservation.Distance(_baseline.Cells[i],frame.Cells[i]) < .025)
                    board[square.Rank,square.File]=game.Board[square.Rank,square.File];
                else remaining.Add(square);
            }
            string? problem=null; try { PositionSetup.Validate(board); } catch(Exception ex) { problem=ex.Message; }
            observed=new(BoardGlyphRecognizer.ToFen(board,game.RedToMove),remaining,observed.Error,problem);
        }
        if (!observed.Confident) return new(false, [], observed.Error, "发生变化的棋子尚不能确定");
        var target = new XiangqiGame();
        try { target.LoadFen(observed.Fen); PositionSetup.Validate(target.Board); }
        catch (Exception) { return new(false, [], observed.Error, "识别局面不符合象棋摆放规则"); }
        if (ExternalPositionRecovery.SamePieces(observed.Fen, game.CurrentFen()))
            return new(true, [], observed.Error, "整盘棋子一致");
        ChessMove[]? matched = null;
        foreach (var moves in Candidates(game, pendingMove, recoverMissedPair: true))
        {
            var expected = (char[,])game.Board.Clone();
            foreach (var move in moves)
            {
                expected[move.To.Rank, move.To.File] = expected[move.From.Rank, move.From.File];
                expected[move.From.Rank, move.From.File] = '\0';
            }
            bool same = true;
            for (int r = 0; r < 10 && same; r++) for (int f = 0; f < 9 && same; f++) same = expected[r,f] == target.Board[r,f];
            if (!same) continue;
            if (matched != null) return new(false, [], observed.Error, "多个合法顺序对应同一局面，请核对");
            matched = moves;
        }
        return matched == null ? new(false, [], observed.Error, "当前棋子不能对应已记录局面的合法后继")
            : new(true, matched.Select(m => m.Uci).ToArray(), observed.Error, "已按棋子身份核验合法变化");
    }
    public BoardMatch Match(BoardObservation frame, XiangqiGame game, string? pendingMove = null, bool recoverMissedPair = false)
    {
        if (frame.Width != _baseline.Width || frame.Height != _baseline.Height)
            return new(false, [], 1, "截图尺寸改变，请重新标定");
        var scorer = new FrameScorer(_baseline, frame, _empty, _baselineEmpty, game.Board);
        var unchanged = scorer.Error([]);
        if (scorer.EssentiallyUnchanged) return new(true, [], unchanged, "等待落子");
        (ChessMove[] Moves, double Error) best = ([], unchanged);
        double runnerUp = double.PositiveInfinity;
        foreach (var moves in Candidates(game, pendingMove, recoverMissedPair))
        {
            var error=scorer.Error(moves);
            if (error < best.Error) { runnerUp=best.Error; best=(moves,error); }
            else if (error < runnerUp) runnerUp=error;
        }
        var margin = runnerUp - best.Error;
        // A small real move can score below the former whole-board tolerance.
        // Compare legal moves before returning unchanged, without relaxing the
        // confidence margin for accepting any move.
        if (best.Moves.Length == 0 && unchanged < .025)
            return new(true, [], unchanged, "等待落子");
        if (best.Error > .085 || margin < .008 || best.Moves.Length == 0)
            return new(false, [], best.Error, "局面变化尚不能唯一对应合法着法；请等待动画结束或重新标定");
        return new(true, best.Moves.Select(m => m.Uci).ToArray(), best.Error, "已核验合法变化");
    }
}
