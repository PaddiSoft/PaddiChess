using SkiaSharp;
using PaddiXiangqi.Core;

namespace PaddiXiangqi.External;

/// <summary>Detects regular, front-facing 9 × 10 grids using local line contrast.</summary>
public static class BoardLocator
{
    /// <summary>Prefer recognized kings and palace placement to the distribution of red pixels.</summary>
    public static bool? DetectOrientation(byte[] png, BoardCalibration geometry)
        => DetectOrientation(CapturedPixels.DecodePng(png), geometry);

    public static bool? DetectOrientation(ExternalFrame frame, BoardCalibration geometry)
        => DetectOrientation(frame.Pixels ?? CapturedPixels.DecodePng(frame.Png), geometry);

    public static bool? DetectOrientation(CapturedPixels pixels, BoardCalibration geometry)
    {
        var view = BoardObservation.Read(pixels, geometry with { RedAtTop = false });
        foreach (var skin in BuiltInBoardSkins.All)
        {
            var read = skin.RecognizeForOrientation(view);
            if (read.Error > .13 || read.Uncertain.Count > 4) continue;
            var game = new XiangqiGame();
            try { game.LoadFen(read.Fen); } catch (FormatException) { continue; }
            if (read.Uncertain.Any(s => char.ToUpperInvariant(game.Board[s.Rank, s.File]) == 'K')) continue;
            var reversed = new char[10, 9];
            for (int r = 0; r < 10; r++) for (int f = 0; f < 9; f++)
                reversed[9-r, 8-f] = game.Board[r, f];
            bool Valid(char[,] board)
            {
                try { PositionSetup.Validate(board); return true; }
                catch (FormatException) { return false; }
            }
            var blackOnTop = Valid(game.Board); var redOnTop = Valid(reversed);
            if (blackOnTop != redOnTop) return redOnTop;
        }
        return BoardObservation.DetectRedAtTop(pixels, geometry);
    }

    public static BoardCalibration? Locate(byte[] png)
        => Locate(CapturedPixels.DecodePng(png));

    public static BoardCalibration? Locate(ExternalFrame frame)
        => Locate(frame.Pixels ?? CapturedPixels.DecodePng(frame.Png));

    private sealed record GridCandidate(BoardCalibration Geometry);

    private static BoardCalibration? Locate(CapturedPixels pixels)
    {
        using var original = pixels.ToBitmap();
        var scale = Math.Min(1, 800d / Math.Max(original.Width, original.Height));
        int width = (int)(original.Width * scale), height = (int)(original.Height * scale);
        if (width < 150 || height < 160) return null;
        using var image = original.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));
        if (image is null) return null;
        var colours = image.Pixels;
        // Unknown boards can have light grid lines and dark decorative strokes.
        // A weak dark-line candidate must not suppress the light/colour search.
        // The two response maps have different amplitudes: colourful glyphs can
        // outweigh a complete dark grid. Compare their final geometries against
        // the same image, using line support across the entire board.
        var dark = LocateCore(pixels, colours, width, height, scale, colourContrast: false);
        var contrast = LocateCore(pixels, colours, width, height, scale, colourContrast: true);
        var candidate = dark is not null && (contrast is null ||
            GridContinuity(colours, width, height, scale, dark.Geometry) >=
            GridContinuity(colours, width, height, scale, contrast.Geometry)) ? dark : contrast;
        if (candidate is null) return null;
        var best = candidate.Geometry;
        var observation = BoardObservation.Read(pixels, best);
        foreach (var skin in BuiltInBoardSkins.All)
        {
            var read = skin.RecognizeForOrientation(observation);
            // Known artwork may refine sub-cell alignment after geometry has won;
            // unknown themes use the line-based result without a skin dependency.
            if (read.Error < .16 && read.Uncertain.Count < 16)
            {
                var refined = skin.RefineGeometry(pixels, best);
                var refinedRead = skin.RecognizeForOrientation(BoardObservation.Read(pixels, refined));
                // A lower average patch error can still move individual glyphs
                // off-centre. Keep established cells and a confidently read
                // position even when another alignment lowers aggregate error.
                if (refinedRead.Uncertain.Count <= read.Uncertain.Count &&
                    !(read.Confident && (!refinedRead.Confident || read.Fen != refinedRead.Fen))) best = refined;
                break;
            }
        }
        return best;
    }

    private static double GridContinuity(SKColor[] pixels, int width, int height, double scale,
        BoardCalibration geometry)
    {
        var left = geometry.Left * scale; var top = geometry.Top * scale;
        var dx = (geometry.Right - geometry.Left) * scale / 8;
        var dy = (geometry.Bottom - geometry.Top) * scale / 9;
        double At(double x, double y, bool horizontal)
        {
            x = Math.Clamp(x, 3, width - 5); y = Math.Clamp(y, 3, height - 5);
            int ix = (int)x, iy = (int)y, offset = horizontal ? width * 3 : 3;
            double fx = x - ix, fy = y - iy;
            float Contrast(int index) => LineContrast(pixels[index], pixels[index - offset], pixels[index + offset]);
            int index = iy * width + ix;
            return (Contrast(index) * (1 - fx) + Contrast(index + 1) * fx) * (1 - fy) +
                (Contrast(index + width) * (1 - fx) + Contrast(index + width + 1) * fx) * fy;
        }
        // Round pieces can hide intersections, so inspect the gaps between them.
        // A median requires support across most grid segments; a few bright piece
        // edges inside a smaller false grid cannot dominate an arithmetic mean.
        var horizontal = new double[240]; var vertical = new double[222];
        int h = 0, v = 0;
        for (int rank = 0; rank < 10; rank++)
            for (int file = 0; file < 8; file++)
                for (int sample = 0; sample < 3; sample++)
                    horizontal[h++] = At(left + (file + .44 + sample * .06) * dx, top + rank * dy, true);
        for (int file = 0; file < 9; file++)
            for (int rank = 0; rank < 9; rank++)
            {
                if (rank == 4 && file is > 0 and < 8) continue;
                for (int sample = 0; sample < 3; sample++)
                    vertical[v++] = At(left + file * dx, top + (rank + .44 + sample * .06) * dy, false);
            }
        Array.Sort(horizontal); Array.Sort(vertical);
        return (horizontal[119] + horizontal[120] + vertical[110] + vertical[111]) / 2;
    }

    private static GridCandidate? LocateCore(CapturedPixels source, SKColor[] pixels, int w, int h, double scale,
        bool colourContrast)
    {
        var luminance = colourContrast ? null : pixels.Select(p => (p.Red*.2126f+p.Green*.7152f+p.Blue*.0722f)/255).ToArray();
        var horizontal = new float[w * h]; var vertical = new float[w * h];
        for (int y = 3; y < h - 3; y++) for (int x = 3; x < w - 3; x++)
        {
            int i = y * w + x;
            if (colourContrast)
            {
                // Pale or equal-luminance coloured lines need chromatic contrast.
                horizontal[i] = LineContrast(pixels[i],pixels[i - w * 3],pixels[i + w * 3]);
                vertical[i] = LineContrast(pixels[i],pixels[i - 3],pixels[i + 3]);
            }
            else
            {
                // Keep a dark-line hypothesis separate from the mixed-polarity
                // map, where bright glyphs and highlights can add extra edges.
                horizontal[i] = Math.Max(0,Math.Min(luminance![i-w*3],luminance[i+w*3])-luminance[i]);
                vertical[i] = Math.Max(0,Math.Min(luminance[i-3],luminance[i+3])-luminance[i]);
            }
        }
        var rows = new double[h];
        for (int y = 3; y < h - 3; y++) for (int x = 3; x < w - 3; x++) rows[y] += horizontal[y * w + x] / w;
        // Every coarse candidate queries the same columns and parity-aligned
        // horizontal segments. Prefix sums preserve all candidates, samples and
        // thresholds while replacing repeated image-length scans with lookups.
        var columns = new double[w * (h + 1)];
        var horizontalSegments = new double[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int index = y * w + x;
            columns[index + w] = columns[index] + vertical[index];
            double value = Math.Max(horizontal[index], Math.Max(y > 0 ? horizontal[index - w] : 0,
                y + 1 < h ? horizontal[index + w] : 0));
            horizontalSegments[index] = value + (x >= 2 ? horizontalSegments[index - 2] : 0);
        }
        double Row(int y) => Math.Max(rows[y], Math.Max(rows[y - 1], rows[y + 1]));
        var options = new List<(int Top, int Step, double Score)>();
        for (int step = 14; step <= Math.Min(w / 8.8, h / 9.8); step++)
            for (int top = step / 2; top + step * 9 + step / 2 < h; top++)
            {
                double score = 0;
                for (int r = 1; r < 9; r++) score += Row(top + r * step);
                options.Add((top, step, score));
            }
        BoardCalibration? best = null; double bestScore = 0;
        foreach (var candidate in options.OrderByDescending(c => c.Score * Math.Sqrt(c.Step)).Take(100))
        {
            int top = candidate.Top, sy = candidate.Step;
            var cols = new double[w];
            int firstRow = top + sy / 2, lastRow = top + sy * 9 - sy / 2;
            for (int x = 3; x < w - 3; x++)
                cols[x] = (columns[lastRow * w + x] - columns[firstRow * w + x]) / (8 * sy);
            var columnPeaks = new double[w];
            for (int x = 1; x < w - 1; x++)
                columnPeaks[x] = Math.Max(cols[x], Math.Max(cols[x - 1], cols[x + 1]));
            for (int sx = Math.Max(14, (int)(sy * .85)); sx <= sy * 1.15 && 8.8 * sx < w; sx++)
                for (int left = sx / 2; left + sx * 8 + sx / 2 < w; left++)
                {
                    double v = 0, hor = 0;
                    for (int f = 0; f < 9; f++) v += columnPeaks[left + f * sx] / 9;
                    if (v < .025) continue;
                    for (int r = 1; r < 9; r++)
                    {
                        int row = (top + r * sy) * w;
                        hor += (horizontalSegments[row + left + sx * 8] - horizontalSegments[row + left - 2]) /
                            (8 * (4d * sx + 1));
                    }
                    var contrast = Math.Min(v, hor);
                    if (contrast < .035) continue;
                    // Both end ranks must connect to the next rank. Otherwise a
                    // colourful row of discs can shift an otherwise regular grid
                    // by one rank into the margin/toolbar.
                    double topLinks=0,bottomLinks=0; int linkSamples=0;
                    for(int f=0;f<9;f++) for(int t=0;t<4;t++)
                    {
                        int x=left+f*sx, upper=top+(int)(sy*(.55+t*.08)), lower=top+(int)(sy*(8.21+t*.08));
                        float At(int y) => Math.Max(vertical[y*w+x],Math.Max(vertical[y*w+x-1],vertical[y*w+x+1]));
                        topLinks+=At(upper);bottomLinks+=At(lower);linkSamples++;
                    }
                    if (Math.Min(topLinks,bottomLinks)/linkSamples < v*.12) continue;
                    // A small repeated motif inside one piece is not a full board.
                    var score = contrast * Math.Sqrt(sx * sy);
                    if (score <= bestScore) continue;
                    // Xiangqi's seven inner files stop at the river. A grid translated by
                    // one rank can otherwise score higher than the real board, especially
                    // when the back-rank discs hide its first/last horizontal lines.
                    double river = 0; int riverSamples = 0;
                    for (int f = 1; f < 8; f++)
                        for (int y = top + (int)(sy * 4.28); y <= top + (int)(sy * 4.72); y++)
                        {
                            int x = left + f * sx;
                            river += Math.Max(vertical[y * w + x], Math.Max(vertical[y * w + x - 1], vertical[y * w + x + 1]));
                            riverSamples++;
                        }
                    score *= 1 - Math.Min(.65, river / riverSamples / v * .5);
                    if (score <= bestScore) continue;
                    var geometry = new BoardCalibration(left / scale, top / scale, (left + sx * 8) / scale, (top + sy * 9) / scale, false);
                    try { geometry.Validate(source.Width, source.Height); } catch { continue; }
                    bestScore = score; best = geometry;
                }
        }
        if (best is null) return null;
        // Integer spacing is only a coarse search. At small mirror sizes a
        // half-pixel spacing error accumulates into several pixels at the ends.
        // Refine against grid segments between intersections, where round pieces
        // leave gaps, without using glyphs, colours or a learned board skin.
        var grid = best with { Left = best.Left * scale, Top = best.Top * scale,
            Right = best.Right * scale, Bottom = best.Bottom * scale };
        double LineAt(float[] values, double x, double y)
        {
            x = Math.Clamp(x, 1, w - 2); y = Math.Clamp(y, 1, h - 2);
            int ix = (int)x, iy = (int)y;
            double fx = x - ix, fy = y - iy;
            return (values[iy * w + ix] * (1 - fx) + values[iy * w + ix + 1] * fx) * (1 - fy) +
                (values[(iy + 1) * w + ix] * (1 - fx) + values[(iy + 1) * w + ix + 1] * fx) * fy;
        }
        double GridScore(BoardCalibration value)
        {
            double dx = (value.Right - value.Left) / 8, dy = (value.Bottom - value.Top) / 9;
            double across = 0, down = 0;
            for (int rank = 0; rank < 10; rank++)
                for (int file = 0; file < 8; file++)
                    for (int sample = 0; sample < 3; sample++)
                        across += LineAt(horizontal, value.Left + (file + .44 + sample * .06) * dx, value.Top + rank * dy);
            for (int file = 0; file < 9; file++)
                for (int rank = 0; rank < 9; rank++)
                {
                    if (rank == 4 && file is > 0 and < 8) continue;
                    for (int sample = 0; sample < 3; sample++)
                        down += LineAt(vertical, value.Left + file * dx, value.Top + (rank + .44 + sample * .06) * dy);
                }
            return across / 240 + down / 222;
        }
        (double Start, double End)? FitLines(bool ranks)
        {
            int count = ranks ? 10 : 9;
            double start = ranks ? grid.Top : grid.Left;
            double spacing = ranks ? (grid.Bottom - grid.Top) / 9 : (grid.Right - grid.Left) / 8;
            double dx = (grid.Right - grid.Left) / 8, dy = (grid.Bottom - grid.Top) / 9;
            double weightSum = 0, indexSum = 0, positionSum = 0, indexSquaredSum = 0, crossSum = 0;
            int supported = 0;
            for (int line = 0; line < count; line++)
            {
                double peak = 0, position = start + line * spacing;
                // Each peak belongs to this grid line; it cannot jump a rank or
                // follow a distant decorative edge outside the candidate cell.
                for (double offset = -spacing * .14; offset <= spacing * .14; offset += .25)
                {
                    double coordinate = start + line * spacing + offset, value = 0;
                    int samples = 0;
                    for (int segment = 0; segment < (ranks ? 8 : 9); segment++)
                    {
                        if (!ranks && segment == 4 && line is > 0 and < 8) continue;
                        for (int sample = 0; sample < 3; sample++)
                        {
                            double fraction = segment + .44 + sample * .06;
                            value += ranks ? LineAt(horizontal, grid.Left + fraction * dx, coordinate)
                                : LineAt(vertical, coordinate, grid.Top + fraction * dy);
                            samples++;
                        }
                    }
                    value /= samples;
                    if (value <= peak) continue;
                    peak = value; position = coordinate;
                }
                if (peak < .025) continue;
                double weight = Math.Min(1, peak * 4);
                weightSum += weight; indexSum += line * weight; positionSum += position * weight;
                indexSquaredSum += line * line * weight; crossSum += line * position * weight; supported++;
            }
            if (supported < count - 2) return null;
            double denominator = weightSum * indexSquaredSum - indexSum * indexSum;
            if (denominator <= 0) return null;
            double step = (weightSum * crossSum - indexSum * positionSum) / denominator;
            double first = (positionSum - step * indexSum) / weightSum, last = first + (count - 1) * step;
            if (Math.Abs(first - start) > spacing * .2 || Math.Abs(last - start - (count - 1) * spacing) > spacing * .2)
                return null;
            return (first, last);
        }
        var ranks = FitLines(ranks: true);
        var files = FitLines(ranks: false);
        if (ranks is { } fittedRanks) grid = grid with { Top = fittedRanks.Start, Bottom = fittedRanks.End };
        if (files is { } fittedFiles) grid = grid with { Left = fittedFiles.Start, Right = fittedFiles.End };
        double refinedScore = GridScore(grid);
        double refinementStep = (grid.Bottom - grid.Top) / 9 * .025;
        for (int pass = 0; pass < 6; pass++)
        {
            for (int axis = 0; axis < 4; axis++)
            {
                var origin = grid;
                foreach (int sign in new[] { -1, 1 })
                {
                    double delta = refinementStep * sign;
                    var adjusted = axis switch
                    {
                        0 => origin with { Left = origin.Left + delta },
                        1 => origin with { Right = origin.Right + delta },
                        2 => origin with { Top = origin.Top + delta },
                        _ => origin with { Bottom = origin.Bottom + delta }
                    };
                    double score = GridScore(adjusted);
                    if (score <= refinedScore) continue;
                    grid = adjusted; refinedScore = score;
                }
            }
            refinementStep *= .5;
        }
        var refined = grid with { Left = grid.Left / scale, Top = grid.Top / scale,
            Right = grid.Right / scale, Bottom = grid.Bottom / scale };
        try { refined.Validate(source.Width, source.Height); }
        catch (InvalidOperationException) { return new(best); }
        return new(refined);
    }

    private static float LineContrast(SKColor centre, SKColor a, SKColor b)
    {
        static int Channel(int c, int left, int right) => Math.Max(0,Math.Max(Math.Min(left,right)-c,c-Math.Max(left,right)));
        // Colour contrast also detects different hues with the same luminance.
        return Math.Max(Channel(centre.Red,a.Red,b.Red),Math.Max(Channel(centre.Green,a.Green,b.Green),
            Channel(centre.Blue,a.Blue,b.Blue)))/255f;
    }
}
