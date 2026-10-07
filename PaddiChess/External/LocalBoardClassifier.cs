using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PaddiXiangqi.Core;
using System.Security.Cryptography;

namespace PaddiXiangqi.External;

/// <summary>Offline, skin-independent 90-square classifier. No learned session templates or move history.</summary>
public static class LocalBoardClassifier
{
    public const string ModelSha256 = "da66ba9809f15127f8ae729b1755e42ee61c100c4f9979ce0ef13602ac471298";
    // The exported model's ordinary empty-square probabilities are around .81.
    // A .90 gate rejects virtually every empty cell, even on perfect captures.
    public const double MinimumConfidence = .80;
    private const string Classes = ".xKABNRCPkabnrcp";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Lazy<InferenceModel> Instance = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Recognition", "xiangqi-nano-v3.onnx");
        using (var file = File.OpenRead(path))
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(ModelSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("棋子识别模型校验失败，请重新安装完整客户端。");
        return InferenceModel.Open("棋子模型", path, [1, 3, 315, 280]);
    });

    public static Task PrepareAsync() => Instance.Value.PrepareAsync();

    public sealed record Prediction(char[] Pieces, double[] Confidence, bool RedAtTop, bool[]? VisibleEmptyGrid = null)
    {
        public bool HasOrientedKings => Enumerable.Range(0, 90).Count(i => Pieces[i] == 'K') == 1 &&
            Enumerable.Range(0, 90).Count(i => Pieces[i] == 'k') == 1 &&
            Enumerable.Range(0, 90).Where(i => Pieces[i] is 'K' or 'k').All(i =>
                Confidence[i] >= MinimumConfidence && PositionSetup.CanOccupySquare(Pieces[i], new(i % 9, i / 9)));

        public SkinRecognition ToRecognition(bool redToMove, bool detectOrientation)
        {
            var board = new char[10, 9]; var uncertain = new HashSet<Square>();
            for (var i = 0; i < 90; i++)
            {
                var square = new Square(i % 9, i / 9);
                if (Pieces[i] == 'x' || !double.IsFinite(Confidence[i]) ||
                    Confidence[i] < MinimumConfidence && !(Pieces[i] == '\0' && VisibleEmptyGrid?[i] == true))
                    uncertain.Add(square);
                if (Pieces[i] != 'x') board[square.Rank, square.File] = Pieces[i];
                if (!PositionSetup.CanOccupySquare(board[square.Rank, square.File], square)) uncertain.Add(square);
            }
            // Retain candidates for the editor, but mark every competing excess
            // identity rather than arbitrarily removing a rook/pawn to fit quotas.
            foreach (var piece in Pieces.Where(p => p is not ('\0' or 'x')).Distinct())
                if (Pieces.Count(p => p == piece) > PositionSetup.MaximumCount(piece))
                    foreach (var i in Enumerable.Range(0, 90).Where(i => Pieces[i] == piece)) uncertain.Add(new(i % 9, i / 9));
            string? problem = null;
            try { PositionSetup.Validate(board); } catch (FormatException ex) { problem = ex.Message; }
            return new(BoardGlyphRecognizer.ToFen(board, redToMove), uncertain.OrderBy(s => s.Rank * 9 + s.File).ToArray(),
                1 - Confidence.Min(), problem)
            { DetectedRedAtTop = detectOrientation && HasOrientedKings ? RedAtTop : null };
        }
    }

    public static async Task<Prediction> ReadAsync(CapturedPixels pixels, BoardCalibration geometry,
        bool detectOrientation, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var first = Infer(pixels, geometry, ct);
                if (!detectOrientation || first.HasOrientedKings) return first;
                var alternate = Infer(pixels, geometry with { RedAtTop = !geometry.RedAtTop }, ct);
                return alternate.HasOrientedKings ? alternate : first;
            }, ct).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private static Prediction Infer(CapturedPixels pixels, BoardCalibration geometry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); pixels.Validate(); geometry.Validate(pixels.Width, pixels.Height);
        var input = Preprocess(pixels, geometry, ct);
        var model = Instance.Value;
        ct.ThrowIfCancellationRequested();
        return model.Run(new DenseTensor<float>(input, [1, 3, 315, 280]), output =>
        {
            if (!output.Dimensions.SequenceEqual(new[] { 1, 90, 16 })) throw new IOException("棋子识别模型输出尺寸不匹配。");
            var pieces = new char[90]; var scores = new double[90]; var emptyGrid = new bool[90];
            for (var i = 0; i < 90; i++)
            {
                var best = 0;
                for (var c = 0; c < 16; c++)
                {
                    if (!float.IsFinite(output[0, i, c])) throw new IOException("棋子识别模型返回无效概率。");
                    if (output[0, i, c] > output[0, i, best]) best = c;
                }
                pieces[i] = best == 0 ? '\0' : Classes[best]; scores[i] = output[0, i, best];
                var display = geometry.RedAtTop ? 89 - i : i;
                if (IsUniformPatch(pixels, geometry, display))
                    scores[i] = 0; // Flat opaque covers cannot supply a piece identity or an empty intersection.
                if (best == 0)
                {
                    if (scores[i] >= .5 && scores[i] < MinimumConfidence)
                        emptyGrid[i] = BoardGridEvidence.HasVisibleGrid(pixels, geometry, display) &&
                            !PieceSilhouette.HasDisc(pixels, geometry, display);
                }
            }
            return new Prediction(pieces, scores, geometry.RedAtTop, emptyGrid);
        }, ct);
    }

    private static bool IsUniformPatch(CapturedPixels pixels, BoardCalibration geometry, int display)
    {
        var dx = (geometry.Right - geometry.Left) / 8; var dy = (geometry.Bottom - geometry.Top) / 9;
        var cx = geometry.Left + display % 9 * dx; var cy = geometry.Top + display / 9 * dy;
        Span<int> minimum = stackalloc int[] { 255, 255, 255 }, maximum = stackalloc int[3];
        for (var y = (int)Math.Floor(cy - dy * .27); y <= (int)Math.Ceiling(cy + dy * .27); y++)
        for (var x = (int)Math.Floor(cx - dx * .27); x <= (int)Math.Ceiling(cx + dx * .27); x++)
        {
            if (x < 0 || y < 0 || x >= pixels.Width || y >= pixels.Height) return false;
            for (var c = 0; c < 3; c++)
            {
                var value = pixels.Bgra[y * pixels.RowBytes + x * 4 + c];
                minimum[c] = Math.Min(minimum[c], value); maximum[c] = Math.Max(maximum[c], value);
                if (maximum[c] - minimum[c] > 5) return false;
            }
        }
        return true;
    }

    // Match the upstream 450x500 warp -> centre 400x450 crop -> 280x315 resize.
    // Two interpolation stages matter for small glyphs. This axis-aligned grid
    // implementation needs neither OpenCV nor a Python process at runtime.
    private static float[] Preprocess(CapturedPixels pixels, BoardCalibration geometry, CancellationToken ct)
    {
        var crop = new byte[400 * 450 * 3];
        var sx = (geometry.Right - geometry.Left) / 350;
        var sy = (geometry.Bottom - geometry.Top) / 400;
        double originX = geometry.Left, originY = geometry.Top;
        if (geometry.RedAtTop) { originX = geometry.Right; originY = geometry.Bottom; sx = -sx; sy = -sy; }
        for (var y = 0; y < 450; y++)
        {
            ct.ThrowIfCancellationRequested();
            var py = Math.Round((originY + (y - 25) * sy) * 32) / 32;
            var iy = (int)Math.Floor(py); var fy = py - iy;
            for (var x = 0; x < 400; x++)
            {
                var px = Math.Round((originX + (x - 25) * sx) * 32) / 32;
                var ix = (int)Math.Floor(px); var fx = px - ix;
                for (var c = 0; c < 3; c++)
                {
                    byte Pixel(int xx, int yy) => xx < 0 || yy < 0 || xx >= pixels.Width || yy >= pixels.Height
                        ? (byte)0 : pixels.Bgra[yy * pixels.RowBytes + xx * 4 + 2 - c];
                    var top = Pixel(ix, iy) * (1 - fx) + Pixel(ix + 1, iy) * fx;
                    var bottom = Pixel(ix, iy + 1) * (1 - fx) + Pixel(ix + 1, iy + 1) * fx;
                    crop[(y * 400 + x) * 3 + c] = (byte)Math.Clamp(Math.Floor(top * (1 - fy) + bottom * fy + .5), 0, 255);
                }
            }
        }
        var input = new float[3 * 315 * 280];
        double[] mean = [123.675, 116.28, 103.53], std = [58.395, 57.12, 57.375];
        for (var y = 0; y < 315; y++)
        {
            ct.ThrowIfCancellationRequested();
            var py = (y + .5) * 450 / 315 - .5; var iy = (int)py; var fy = py - iy;
            for (var x = 0; x < 280; x++)
            {
                var px = (x + .5) * 400 / 280 - .5; var ix = (int)px; var fx = px - ix;
                for (var c = 0; c < 3; c++)
                {
                    var top = crop[(iy * 400 + ix) * 3 + c] * (1 - fx) + crop[(iy * 400 + ix + 1) * 3 + c] * fx;
                    var bottom = crop[((iy + 1) * 400 + ix) * 3 + c] * (1 - fx) + crop[((iy + 1) * 400 + ix + 1) * 3 + c] * fx;
                    var value = Math.Floor(top * (1 - fy) + bottom * fy + .5);
                    input[c * 315 * 280 + y * 280 + x] = (float)((value - mean[c]) / std[c]);
                }
            }
        }
        return input;
    }
}
