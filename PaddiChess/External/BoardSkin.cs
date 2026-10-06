using System.Text;
using System.Text.Json;
using System.Numerics;
using PaddiXiangqi.Core;

namespace PaddiXiangqi.External;

/// <summary>Local, user-confirmed visual vocabulary. No network or model credentials.</summary>
public sealed record BoardSkin(string Name, Dictionary<char, List<float[]>> Samples)
{
    private static readonly (int Offset, int Length)[] GlyphRows = Enumerable.Range(4, 16)
        .Select(y => (Y: y, Xs: Enumerable.Range(4, 16).Where(x =>
            (x - 11.5) * (x - 11.5) + (y - 11.5) * (y - 11.5) <= 60).ToArray()))
        .Where(row => row.Xs.Length != 0)
        .Select(row => ((row.Y * 24 + row.Xs[0]) * 3, row.Xs.Length * 3)).ToArray();
    private static readonly int GlyphValues = GlyphRows.Sum(row => row.Length);
    private static readonly (int Offset, double Penalty)[] GlyphShifts =
        (from y in Enumerable.Range(-3, 7) from x in Enumerable.Range(-3, 7)
         orderby Math.Abs(x) + Math.Abs(y)
         select ((y * 24 + x) * 3, (Math.Abs(x) + Math.Abs(y)) * .001)).ToArray();
    public static BoardSkin Learn(string name, BoardObservation image, XiangqiGame game, bool requireAllPieces = true)
    {
        var samples = new Dictionary<char, List<float[]>>();
        for (int i = 0; i < 90; i++)
        {
            var piece = game.Board[i / 9, i % 9];
            if (!samples.TryGetValue(piece, out var list)) samples[piece] = list = [];
            if (list.Count < (piece == '\0' ? 60 : 5)) list.Add(image.Cells[i]);
        }
        if (requireAllPieces && "rnbakcpRNBAKCP".Any(p => !samples.ContainsKey(p)))
            throw new InvalidOperationException("学习样式需要红黑双方的全部七种棋子。请使用完整开局学习，之后就能识别同款棋盘的中局。");
        return new(name, samples);
    }
    public SkinRecognition Recognize(BoardObservation image, bool redToMove) => RecognizeWithKnownPieces(image, redToMove, null);
    /// <summary>Reuse an independent full read only for exactly identical recognition samples.</summary>
    public SkinRecognition RecognizeFromPreviousRead(BoardObservation image, bool redToMove,
        BoardObservation previous, SkinRecognition previousRead)
    {
        if (!previousRead.Confident || image.Width != previous.Width || image.Height != previous.Height)
            return Recognize(image, redToMove);
        var prior = new XiangqiGame();
        prior.LoadFen(previousRead.Fen);
        var identical = Enumerable.Range(0, 90).Where(i => image.Cells[i].AsSpan().SequenceEqual(previous.Cells[i])).ToArray();
        // A prior glyph may have needed a lifted sample. Its core alone is then
        // insufficient evidence of identical input; compare every used variant too.
        image.PrepareLiftedCells(identical.Where(previous.HasLiftedCell));
        var known = new char?[90];
        foreach (var i in identical)
        {
            if (previous.HasLiftedCell(i) && image.LiftedCell(i).Where((cell, variant) =>
                !cell.AsSpan().SequenceEqual(previous.LiftedCell(i)[variant])).Any()) continue;
            known[i] = prior.Board[i / 9, i % 9];
        }
        return RecognizeWithKnownPieces(image, redToMove, known);
    }
    // Before orientation is known, both palaces must remain candidates. This
    // result is only used for locating/orienting the grid, never accepting play.
    internal SkinRecognition RecognizeForOrientation(BoardObservation image) =>
        RecognizeWithKnownPieces(image, true, null, restrictSquares: false);

    internal SkinRecognition RecognizeWithKnownPieces(BoardObservation image, bool redToMove, IReadOnlyList<char?>? knownPieces,
        bool restrictSquares = true)
    {
        var board = new char[90]; var uncertain = new List<Square>(); double worst = 0;
        // Enable colour evidence only for templates that actually use red versus
        // non-red cores. Custom monochrome/blue skins must remain supported.
        bool colourReliable = "RNBAKCP".All(p => Samples.TryGetValue(p, out var samples) && samples.All(BoardGlyphRecognizer.HasRedInk)) &&
            "rnbakcp".All(p => Samples.TryGetValue(p, out var samples) && samples.All(s => !BoardGlyphRecognizer.HasRedInk(s)));
        double Distance(char piece, float[] cell) => PieceDistance(piece, Samples[piece], cell) +
            (colourReliable && piece != '\0' && BoardGlyphRecognizer.HasRedInk(cell) != char.IsUpper(piece) ? .08 : 0);
        var rankedCells = new (char Piece, double Error)[90][];
        var resample = new List<int>();
        for (int i = 0; i < 90; i++)
        {
            if (knownPieces?[i] is { } known) { board[i] = known; continue; }
            var square = new Square(i % 9, i / 9);
            var ranked = Samples.Where(pair => !restrictSquares || PositionSetup.CanOccupySquare(pair.Key, square))
                .Select(pair => (Piece: pair.Key, Error: Distance(pair.Key, image.Cells[i])))
                .OrderBy(p => p.Error).ToArray();
            if (ranked[0].Error > .04 || ranked.Length > 1 && ranked[1].Error - ranked[0].Error < .018)
                resample.Add(i);
            rankedCells[i] = ranked;
        }
        image.PrepareLiftedCells(resample);
        for (int i = 0; i < 90; i++)
        {
            if (knownPieces?[i] is not null) continue;
            var ranked = rankedCells[i];
            if (resample.Contains(i))
            {
                // Re-sample the glyph above the intersection for lift animations. Do not move
                // the grid or use the empty background left underneath a lifted piece.
                ranked = ranked.Select(candidate => candidate.Piece == '\0' ? candidate :
                    (Piece: candidate.Piece, Error: Math.Min(candidate.Error,
                        image.LiftedCell(i).Skip(1).Select(cell =>
                            Distance(candidate.Piece, cell) + .002).Min())))
                    .OrderBy(p => p.Error).ToArray();
            }
            var best = ranked[0];
            // Keep uncertain candidates available for correction/recovery. A globally
            // incompatible vocabulary is rejected below, never published as a board.
            board[i] = best.Piece; worst = Math.Max(worst, best.Error);
            rankedCells[i] = ranked;
            if (best.Error > .10 || ranked.Length < 2 || ranked[1].Error - best.Error < .012)
                uncertain.Add(new Square(i % 9, i / 9));
        }
        if (restrictSquares && knownPieces is null)
            RepairOpening(board, rankedCells, uncertain);
        if (knownPieces is null && uncertain.Count > 45)
            return new("9/9/9/9/9/9/9/9/9/9" + (redToMove ? " w - - 0 1" : " b - - 0 1"),
                Enumerable.Range(0, 90).Select(i => new Square(i % 9, i / 9)).ToArray(), worst,
                "当前样式与棋盘不匹配，需要通用识字或重新学习；未采用不可靠的棋子分类。");
        var fen = new StringBuilder();
        for (int r = 0; r < 10; r++)
        {
            int empty = 0;
            for (int f = 0; f < 9; f++)
            {
                var piece = board[r * 9 + f];
                if (piece == '\0') { empty++; continue; }
                if (empty > 0) { fen.Append(empty); empty = 0; }
                fen.Append(piece);
            }
            if (empty > 0) fen.Append(empty);
            if (r < 9) fen.Append('/');
        }
        fen.Append(redToMove ? " w - - 0 1" : " b - - 0 1");
        string? problem = null;
        try { var game = new XiangqiGame(); game.LoadFen(fen.ToString()); PositionSetup.Validate(game.Board); }
        catch (Exception ex) { problem = ex.Message; }
        return new(fen.ToString(), uncertain, worst, problem);
    }
    // An almost complete opening supplies evidence that an isolated corner label
    // cannot provide. Only repair a unique, visually plausible opening; a rook
    // entering the enemy home rank in a middle game must keep its own colour.
    private static readonly Lazy<char[][]> OpeningBoards = new(() =>
    {
        var game = new XiangqiGame();
        char[] Snapshot() => Enumerable.Range(0, 90).Select(i => game.Board[i / 9, i % 9]).ToArray();
        var positions = new List<char[]> { Snapshot() };
        foreach (var move in game.AllLegalMoves().ToArray())
        {
            game.TryMoveUci(move.Uci, out _); positions.Add(Snapshot()); game.Undo();
        }
        return positions.ToArray();
    });
    private static void RepairOpening(char[] board, (char Piece, double Error)[][] ranked,
        List<Square> uncertain)
    {
        var nearby = OpeningBoards.Value.Select(position =>
            (Position: position, Differences: Enumerable.Range(0, 90).Count(i => position[i] != board[i])))
            .Where(candidate => candidate.Differences <= 2).OrderBy(candidate => candidate.Differences).ToArray();
        if (nearby.Length == 0 || nearby.Length > 1 && nearby[1].Differences == nearby[0].Differences) return;
        var expected = nearby[0].Position;
        for (var i = 0; i < 90; i++)
        {
            if (board[i] == expected[i]) continue;
            var match = ranked[i].FirstOrDefault(candidate => candidate.Piece == expected[i], (Piece: '\0', Error: 1d));
            if (match.Error > .10 || match.Error - ranked[i][0].Error > .045) return;
        }
        for (var i = 0; i < 90; i++)
        {
            if (board[i] == expected[i] && !uncertain.Contains(new Square(i % 9, i / 9))) continue;
            var match = ranked[i].FirstOrDefault(candidate => candidate.Piece == expected[i], (Piece: '\0', Error: 1d));
            if (match.Error > .10 || match.Error - ranked[i][0].Error > .045) continue;
            board[i] = expected[i]; uncertain.Remove(new Square(i % 9, i / 9));
        }
    }
    private static double PieceDistance(char piece, List<float[]> samples, float[] observed)
    {
        double best = 1;
        foreach (var sample in samples)
            best = piece == '\0' ? Math.Min(best, BoardObservation.EmptyDistance(sample, observed)) : GlyphDistance(sample, observed, best);
        return best;
    }
    // Mirrored phone images are small and often calibrated a pixel off-centre.
    // Compare the glyph core with a small translation tolerance, without accepting arbitrary shapes.
    public static double GlyphDistance(float[] reference, float[] observed) => GlyphDistance(reference, observed, 1);

    private static double GlyphDistance(float[] reference, float[] observed, double best)
    {
        var original = BoardObservation.Distance(reference, observed);
        if (original < .004) return Math.Min(best, original);
        foreach (var shift in GlyphShifts)
        {
            double sum = 0;
            // A partial absolute-error sum is a lower bound. Stop a translation as soon
            // as it cannot improve the current best; the score and tolerance stay the same.
            double maximumSum = (best - shift.Penalty) * GlyphValues / .66;
            foreach (var row in GlyphRows)
            {
                int a = row.Offset, b = a + shift.Offset, end = a + row.Length;
                var vectorSum = Vector<float>.Zero;
                for (; a <= end - Vector<float>.Count; a += Vector<float>.Count, b += Vector<float>.Count)
                    vectorSum += Vector.Abs(new Vector<float>(reference, a) - new Vector<float>(observed, b));
                sum += Vector.Sum(vectorSum);
                for (; a < end; a++, b++) sum += Math.Abs(reference[a] - observed[b]);
                if (sum >= maximumSum) break;
            }
            if (sum < maximumSum) best = sum / GlyphValues * .66 + shift.Penalty;
        }
        return best;
    }
    public BoardCalibration RefineGeometry(byte[] png, BoardCalibration geometry)
        => RefineGeometry(CapturedPixels.DecodePng(png), geometry);

    public BoardCalibration RefineGeometry(CapturedPixels pixels, BoardCalibration geometry)
    {
        double Score(BoardCalibration candidate)
        {
            BoardObservation image;
            try { image = BoardObservation.Read(pixels, candidate); } catch (InvalidOperationException) { return 1; }
            double total = 0;
            for (int i = 0; i < 90; i++)
                total += Samples.Min(pair => pair.Value.Min(sample => pair.Key == '\0'
                    ? BoardObservation.EmptyDistance(sample, image.Cells[i]) : BoardObservation.Distance(sample, image.Cells[i])));
            return total / 90;
        }
        var best = geometry; var score = Score(best);
        double step = (geometry.Right - geometry.Left) / 8 * .045;
        for (int pass = 0; pass < 3; pass++)
        {
            for (int axis = 0; axis < 4; axis++)
            {
                var origin = best;
                foreach (int direction in new[]{-1,1})
                {
                    var delta = step * direction;
                    var candidate = axis switch { 0 => origin with { Left=origin.Left+delta }, 1 => origin with { Top=origin.Top+delta },
                        2 => origin with { Right=origin.Right+delta }, _ => origin with { Bottom=origin.Bottom+delta } };
                    var value = Score(candidate);
                    if (value < score) { score = value; best = candidate; }
                }
            }
            step *= .6;
        }
        return best;
    }
    public void Save(string path)
    {
        var destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, destination, true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    public static BoardSkin Load(string path)
    {
        // A skin is a small local dictionary, not an unbounded image/model file.
        // Reject broken optional fields before their first recognition invocation.
        if (new FileInfo(path).Length > 64 * 1024 * 1024)
            throw new FormatException("棋子样式文件过大，请重新学习");
        BoardSkin skin;
        try { skin = JsonSerializer.Deserialize<BoardSkin>(File.ReadAllText(path)) ?? throw new FormatException("样式文件为空"); }
        catch (JsonException ex) { throw new FormatException("棋子样式文件损坏，请重新学习", ex); }
        if (string.IsNullOrWhiteSpace(skin.Name) || skin.Samples == null || skin.Samples.Count != 15 ||
            "rnbakcpRNBAKCP\0".Any(c => !skin.Samples.ContainsKey(c)) ||
            skin.Samples.Values.Any(v => v == null || v.Count is < 1 or > 60 ||
                v.Any(s => s == null || s.Length != 1728 || s.Any(n => !float.IsFinite(n) || n < 0 || n > 1))))
            throw new FormatException("棋子样式文件损坏，请重新学习");
        return skin;
    }
}
public sealed record SkinRecognition(string Fen, IReadOnlyList<Square> Uncertain, double Error, string? Problem)
{
    public bool? DetectedRedAtTop { get; init; }
    public bool Confident => Uncertain.Count == 0 && Problem == null;
}
