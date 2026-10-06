namespace PaddiXiangqi.External;

/// <summary>Visible grid strokes are additional empty-square evidence, never a piece classifier.</summary>
internal static class BoardGridEvidence
{
    public static bool HasVisibleGrid(CapturedPixels pixels, BoardCalibration geometry, int displayIndex)
    {
        int file = displayIndex % 9, rank = displayIndex / 9;
        double dx = (geometry.Right - geometry.Left) / 8, dy = (geometry.Bottom - geometry.Top) / 9;
        double cx = geometry.Left + file * dx, cy = geometry.Top + rank * dy;
        double horizontal = 0, vertical = 0;
        foreach (int sign in new[] { -1, 1 })
        {
            if (file + sign is >= 0 and <= 8)
                horizontal = Math.Max(horizontal, Arm(pixels, cx, cy, dx, dy, horizontal: true, sign));
            bool crossesRiver = file is > 0 and < 8 && (rank == 4 && sign == 1 || rank == 5 && sign == -1);
            if (rank + sign is >= 0 and <= 9 && !crossesRiver)
                vertical = Math.Max(vertical, Arm(pixels, cx, cy, dx, dy, horizontal: false, sign));
        }
        // Both axes must have a visible arm. This works at board edges and river
        // boundaries without assuming a particular line colour or polarity.
        return Math.Min(horizontal, vertical) >= .03;
    }

    private static double Arm(CapturedPixels pixels, double cx, double cy, double dx, double dy, bool horizontal, int sign)
    {
        int Pixel(double x, double y)
        {
            int px = Math.Clamp((int)Math.Round(x), 0, pixels.Width - 1);
            int py = Math.Clamp((int)Math.Round(y), 0, pixels.Height - 1);
            return py * pixels.RowBytes + px * 4;
        }
        double Contrast(int centre, int before, int after)
        {
            int best = 0;
            for (int channel = 0; channel < 3; channel++)
            {
                int a = pixels.Bgra[before + channel], b = pixels.Bgra[after + channel], c = pixels.Bgra[centre + channel];
                best = Math.Max(best, Math.Max(Math.Min(a, b) - c, c - Math.Max(a, b)));
            }
            return best / 255d;
        }
        Span<double> samples = stackalloc double[9];
        double best = 0;
        for (int offset = -4; offset <= 4; offset++)
        {
            // One offset must explain the whole arm. Independently shifting every
            // sample could join unrelated texture edges into an invented grid line.
            double shift = offset * .015;
            for (int k = 0; k < samples.Length; k++)
            {
                double along = sign * (.12 + k * .03);
                double x = cx + (horizontal ? along : shift) * dx;
                double y = cy + (horizontal ? shift : along) * dy;
                double shoulderX = horizontal ? 0 : .075 * dx;
                double shoulderY = horizontal ? .075 * dy : 0;
                samples[k] = Contrast(Pixel(x, y), Pixel(x - shoulderX, y - shoulderY), Pixel(x + shoulderX, y + shoulderY));
            }
            samples.Sort();
            best = Math.Max(best, samples[samples.Length / 2]);
        }
        return best;
    }
}
