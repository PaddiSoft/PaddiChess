using System.Text;
using PaddiXiangqi.Core;

namespace PaddiXiangqi.External;

public static class BoardGlyphRecognizer
{
    public static async Task<SkinRecognition> RecognizeAsync(byte[] png, BoardCalibration geometry, bool redToMove, CancellationToken ct, bool detectOrientation = false)
    {
        var pixels = await Task.Run(() => CapturedPixels.DecodePng(png), ct).ConfigureAwait(false);
        return await RecognizeAsync(pixels, geometry, redToMove, ct, detectOrientation).ConfigureAwait(false);
    }

    public static Task<SkinRecognition> RecognizeAsync(ExternalFrame frame, BoardCalibration geometry, bool redToMove, CancellationToken ct, bool detectOrientation = false)
        => frame.Pixels is { } pixels ? RecognizeAsync(pixels, geometry, redToMove, ct, detectOrientation)
            : RecognizeAsync(frame.Png, geometry, redToMove, ct, detectOrientation);

    public static async Task<SkinRecognition> RecognizeAsync(CapturedPixels pixels, BoardCalibration geometry, bool redToMove, CancellationToken ct, bool detectOrientation = false)
    {
        var glyphs = await LocalGlyphOcr.ReadAsync(pixels, geometry, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var direction = detectOrientation ? InferOrientation(glyphs) : null;
        if (direction is { } redAtTop) geometry = geometry with { RedAtTop = redAtTop };
        // Palette clustering and complete position validation never run on the UI thread.
        return await Task.Run(() => BuildPosition(pixels, geometry, redToMove, glyphs)
            with { DetectedRedAtTop = direction }, ct).ConfigureAwait(false);
    }

    public static bool? InferOrientation(IReadOnlyList<LocalGlyphOcr.Glyph> glyphs)
    {
        if (glyphs.Count != 90) return null;
        var reds = Enumerable.Range(0,90).Where(i => LocalGlyphOcr.IsReliable(glyphs[i]) && "帥帅".Contains(glyphs[i].Text)).ToArray();
        var blacks = Enumerable.Range(0,90).Where(i => LocalGlyphOcr.IsReliable(glyphs[i]) && "將将".Contains(glyphs[i].Text)).ToArray();
        if (reds.Length != 1 || blacks.Length != 1) return null;
        bool Palace(int i, bool top) => i % 9 is >= 3 and <= 5 && (top ? i / 9 <= 2 : i / 9 >= 7);
        if (Palace(reds[0],true) && Palace(blacks[0],false)) return true;
        if (Palace(blacks[0],true) && Palace(reds[0],false)) return false;
        return null;
    }

    public static SkinRecognition BuildPosition(byte[] png, BoardCalibration geometry, bool redToMove, IReadOnlyList<LocalGlyphOcr.Glyph> glyphs)
        => BuildPosition(CapturedPixels.DecodePng(png), geometry, redToMove, glyphs);

    public static SkinRecognition BuildPosition(CapturedPixels pixels, BoardCalibration geometry, bool redToMove, IReadOnlyList<LocalGlyphOcr.Glyph> glyphs)
    {
        if (glyphs.Count != 90) throw new IOException("识字结果缺少交叉点");
        pixels.Validate();
        geometry.Validate(pixels.Width, pixels.Height);
        var board = new char[10, 9]; var uncertain = new List<Square>();
        var discs = Enumerable.Range(0, 90).Select(i => PieceSilhouette.HasDisc(pixels, geometry, i)).ToArray();
        bool discTheme = HasConsistentDiscTheme(glyphs, geometry, discs);
        var observation = BoardObservation.Read(pixels, geometry);
        var tones = observation.Cells.Select(PiecePalette.FromPatch).ToArray();
        int InternalIndex(int i) => geometry.RedAtTop ? 89 - i : i;
        var redAnchors = Enumerable.Range(0,90).Where(i => LocalGlyphOcr.IsReliable(glyphs[i]) && "帥帅相仕兵俥傌".Contains(glyphs[i].Text[0])).Select(i => tones[InternalIndex(i)]).ToArray();
        var blackAnchors = Enumerable.Range(0,90).Where(i => LocalGlyphOcr.IsReliable(glyphs[i]) && "將将象卒".Contains(glyphs[i].Text[0])).Select(i => tones[InternalIndex(i)]).ToArray();
        // Distinct stroke coverage/antialiasing on monochrome discs is not a side colour.
        var distinctPalettes = redAnchors.Length > 0 && blackAnchors.Length > 0 &&
            redAnchors.Min(red => blackAnchors.Min(black => PiecePalette.Distance(red,black))) > .035;
        for (int i = 0; i < 90; i++)
        {
            int r = geometry.RedAtTop ? 9 - i / 9 : i / 9, f = geometry.RedAtTop ? 8 - i % 9 : i % 9;
            var square = new Square(f, r); var glyph = glyphs[i];
            // A weak prediction is a suggestion for recovery, never a piece.
            // River labels and grid intersections can look like 卒 or 士 to OCR.
            if (!LocalGlyphOcr.IsReliable(glyph))
            {
                // No glyph and no circle can also mean an opaque obstruction.
                // Require visible grid strokes as additional positive evidence;
                // retain the conservative disc/transparent-theme checks too.
                if (discs[i] || !BoardGridEvidence.HasVisibleGrid(pixels, geometry, i) ||
                    !discTheme && !LocalGlyphOcr.IsPlainGrid(pixels, geometry, i))
                    uncertain.Add(square);
                continue;
            }
            var token = glyph.Text.FirstOrDefault(c => "車车俥馬马傌象相士仕將将帥帅炮砲兵卒".Contains(c));
            var patch = observation.Cells[r * 9 + f];
            bool red = HasRedInk(patch);
            bool colourKnown = false;
            if (distinctPalettes)
            {
                var tone = tones[r * 9 + f];
                double Difference(PiecePalette.Colour[] anchor) => PiecePalette.Distance(anchor,tone);
                var redDistance = redAnchors.Min(Difference); var blackDistance = blackAnchors.Min(Difference);
                red = redDistance < blackDistance;
                colourKnown = Math.Abs(redDistance-blackDistance) > .025;
                if (!colourKnown && PiecePalette.TryResolveSide(patch, redAnchors, blackAnchors, out var resolvedRed))
                { colourKnown = true; red = resolvedRed; }
            }
            char piece = token switch
            {
                '車' or '车' or '俥' => 'R', '馬' or '马' or '傌' => 'N', '相' or '象' => 'B',
                '仕' or '士' => 'A', '帥' or '帅' or '將' or '将' => 'K', '炮' or '砲' => 'C', '兵' or '卒' => 'P', _ => '\0'
            };
            if ("相仕帥帅兵俥傌".Contains(token) && token != '\0') { red = true; colourKnown = true; }
            if ("象將将卒".Contains(token) && token != '\0') { red = false; colourKnown = true; }
            if (piece != '\0')
            {
                board[r, f] = red ? piece : char.ToLowerInvariant(piece);
                if (!PositionSetup.CanOccupySquare(board[r, f], square))
                {
                    // OCR cannot turn a rook at its home rank into a pawn. Leave
                    // the square unresolved rather than publishing an illegal label.
                    board[r, f] = '\0';
                    // A bare grid / river label may be confidently misread as
                    // 士. Discard it only with both a consistent disc theme and
                    // positive visible-grid evidence at this intersection.
                    if (discs[i] || !discTheme || !BoardGridEvidence.HasVisibleGrid(pixels, geometry, i)) uncertain.Add(square);
                }
                else if (!colourKnown) uncertain.Add(square);
            }

        }
        var fen = ToFen(board, redToMove); string? problem = null;
        try { PositionSetup.Validate(board); } catch (Exception ex) { problem = ex.Message; }
        return new(fen, uncertain, 0, problem);
    }
    private static bool HasConsistentDiscTheme(IReadOnlyList<LocalGlyphOcr.Glyph> glyphs,
        BoardCalibration geometry, IReadOnlyList<bool> discs)
    {
        var redKings = Enumerable.Range(0, 90).Where(i => LocalGlyphOcr.IsReliable(glyphs[i]) && "帥帅".Contains(glyphs[i].Text)).ToArray();
        var blackKings = Enumerable.Range(0, 90).Where(i => LocalGlyphOcr.IsReliable(glyphs[i]) && "將将".Contains(glyphs[i].Text)).ToArray();
        if (redKings.Length != 1 || blackKings.Length != 1 || !discs[redKings[0]] || !discs[blackKings[0]]) return false;
        int identified = 0, circular = 0;
        for (int i = 0; i < 90; i++)
        {
            if (!LocalGlyphOcr.IsReliable(glyphs[i])) continue;
            char piece = glyphs[i].Text[0] switch
            {
                '車' or '车' or '俥' => 'R', '馬' or '马' or '傌' => 'N', '象' or '相' => 'B',
                '士' or '仕' => 'A', '將' or '将' or '帥' or '帅' => 'K', '炮' or '砲' => 'C', _ => 'P'
            };
            int squareIndex = geometry.RedAtTop ? 89 - i : i;
            var square = new Square(squareIndex % 9, squareIndex / 9);
            // Exclude board lettering read as a piece on an impossible square.
            if (!PositionSetup.CanOccupySquare(piece, square) &&
                !PositionSetup.CanOccupySquare(char.ToLowerInvariant(piece), square)) continue;
            identified++; if (discs[i]) circular++;
        }
        // The two kings provide anchors even in a sparse endgame. Other reliable
        // pieces must agree; absent evidence returns to conservative grid checks.
        return identified >= 2 && circular >= Math.Ceiling(identified * .8);
    }

    public static bool HasRedInk(float[] patch)
    {
        // Selection/capture halos belong to the rim, not the glyph's colour.
        int votes = 0;
        for (int y = 4; y < 20; y++) for (int x = 4; x < 20; x++)
        {
            if ((x - 11.5) * (x - 11.5) + (y - 11.5) * (y - 11.5) > 49) continue;
            int p = (y * 24 + x) * 3;
            if (patch[p] - patch[p + 1] > .12 && patch[p] - patch[p + 2] > .12 && Math.Abs(patch[p + 1] - patch[p + 2]) < .20) votes++;
        }
        return votes > 8;
    }
    public static string ToFen(char[,] board, bool redToMove)
    {
        var fen = new StringBuilder();
        for (int r = 0; r < 10; r++)
        {
            int empty = 0;
            for (int f = 0; f < 9; f++)
            {
                if (board[r,f] == '\0') { empty++; continue; }
                if (empty > 0) { fen.Append(empty); empty = 0; }
                fen.Append(board[r,f]);
            }
            if (empty > 0) fen.Append(empty); if (r < 9) fen.Append('/');
        }
        return fen + (redToMove ? " w - - 0 1" : " b - - 0 1");
    }
}
