using Microsoft.ML.OnnxRuntime;
using PaddiXiangqi.Core;

namespace PaddiXiangqi.External;

public interface IExternalPositionRecognizer
{
    Task<SkinRecognition?> ReadAsync(ExternalFrame frame, BoardCalibration geometry,
        bool redToMove, CancellationToken ct, bool detectOrientation, BoardSkin? sessionSkin, string? customPath);
}

/// <summary>Initial/full-board recognition, separate from the lightweight move tracker.</summary>
public sealed class ExternalPositionRecognizer : IExternalPositionRecognizer
{
    public async Task<SkinRecognition?> ReadAsync(ExternalFrame frame, BoardCalibration geometry,
        bool redToMove, CancellationToken ct, bool detectOrientation, BoardSkin? sessionSkin, string? customPath)
    {
        var pixels = frame.Pixels ?? await Task.Run(() => CapturedPixels.DecodePng(frame.Png), ct).ConfigureAwait(false);
        LocalBoardClassifier.Prediction prediction;
        try { prediction = await LocalBoardClassifier.ReadAsync(pixels, geometry, detectOrientation, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OnnxRuntimeException or TypeInitializationException)
        {
            // A damaged/unsupported native model must leave the editor usable.
            // Ordinary recognition never depends on a matching built-in skin.
            return await BoardGlyphRecognizer.RecognizeAsync(pixels, geometry, redToMove, ct, detectOrientation).ConfigureAwait(false);
        }
        var read = prediction.ToRecognition(redToMove, detectOrientation);
        if (read.Confident || read.Uncertain.Count == 0 || read.Uncertain.Count > 12) return read;
        var selectedGeometry = geometry with { RedAtTop = prediction.RedAtTop };
        using var supplement = CancellationTokenSource.CreateLinkedTokenSource(ct);
        supplement.CancelAfter(TimeSpan.FromMilliseconds(800));
        try
        {
            var indices = read.Uncertain.Select(s => prediction.RedAtTop ? 89 - s.Rank * 9 - s.File : s.Rank * 9 + s.File).ToArray();
            var ocr = await LocalGlyphOcr.ReadCellsAsync(pixels, selectedGeometry, indices, supplement.Token).ConfigureAwait(false);
            return await Task.Run(() => Supplement(pixels, selectedGeometry, prediction, read, ocr.Glyphs,
                redToMove, detectOrientation), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return read; }
        catch (Exception ex) when (ex is IOException or OnnxRuntimeException) { return read; }
    }

    private static SkinRecognition Supplement(CapturedPixels pixels, BoardCalibration geometry,
        LocalBoardClassifier.Prediction prediction, SkinRecognition original, LocalGlyphOcr.Glyph[] ocr,
        bool redToMove, bool detectOrientation)
    {
        var uncertain = original.Uncertain.Select(s => s.Rank * 9 + s.File).ToHashSet();
        const string pieces = "RNBAKCPrnbakcp";
        const string glyphs = "俥傌相仕帥炮兵車馬象士將砲卒";
        // Confident model labels provide palette anchors; OCR only contributes
        // evidence for the unresolved cells. Confirmed empty cells stay intact.
        var combined = Enumerable.Range(0, 90).Select(display =>
        {
            var i = geometry.RedAtTop ? 89 - display : display;
            var index = pieces.IndexOf(prediction.Pieces[i]);
            return uncertain.Contains(i) ? ocr[display] : index < 0 ? new LocalGlyphOcr.Glyph("", 0) :
                new LocalGlyphOcr.Glyph(glyphs[index].ToString(), prediction.Confidence[i]);
        }).ToArray();
        var supplement = BoardGlyphRecognizer.BuildPosition(pixels, geometry, redToMove, combined);
        var candidates = supplement.Fen.Split(' ')[0].Where(c => c != '/').SelectMany(c =>
            char.IsDigit(c) ? Enumerable.Repeat('\0', c - '0') : [c]).ToArray();
        var resolved = (char[])prediction.Pieces.Clone(); var confidence = (double[])prediction.Confidence.Clone();
        foreach (var i in uncertain)
        {
            var display = geometry.RedAtTop ? 89 - i : i;
            var glyph = ocr[display]; var piece = candidates[i];
            if (piece == '\0' || glyph.Confidence < LocalBoardClassifier.MinimumConfidence ||
                !LocalGlyphOcr.IsReliable(glyph) || supplement.Uncertain.Contains(new Square(i % 9, i / 9))) continue;
            // Different identities remain disputed. A matching glyph type can
            // resolve a weak side classification using independent colour evidence.
            if (resolved[i] is not ('\0' or 'x') && char.ToUpperInvariant(resolved[i]) != char.ToUpperInvariant(piece)) continue;
            resolved[i] = piece; confidence[i] = glyph.Confidence;
        }
        return new LocalBoardClassifier.Prediction(resolved, confidence, prediction.RedAtTop, prediction.VisibleEmptyGrid)
            .ToRecognition(redToMove, detectOrientation);
    }

    public Task<SkinRecognition?> ReadTemplatesAsync(BoardObservation observation, bool redToMove,
        CancellationToken ct, BoardSkin? sessionSkin, string? customPath) => Task.Run(() =>
        {
            IEnumerable<BoardSkin> skins = BuiltInBoardSkins.All;
            if (sessionSkin != null) skins = new[] { sessionSkin }.Concat(skins);
            if (customPath != null) skins = new[] { BoardSkin.Load(customPath) }.Concat(skins);
            foreach (var skin in skins)
            {
                ct.ThrowIfCancellationRequested();
                var read = skin.Recognize(observation, redToMove);
                if (read.Confident) return read;
            }
            return (SkinRecognition?)null;
        }, ct);
}
