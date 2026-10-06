namespace PaddiXiangqi.External;

/// <summary>Radial disc evidence, independent of side colours, type labels and game history.</summary>
internal static class PieceSilhouette
{
    // More than half the circumference must have an edge whose normal points
    // outwards. Board lettering and texture may have contrast on many rays,
    // but their edges do not normally follow a circle around the intersection.
    public static bool HasDisc(int rays) => rays >= 14;

    public static bool HasDisc(CapturedPixels pixels, BoardCalibration geometry, int displayIndex)
        => HasDisc(DiscRays(pixels, geometry, displayIndex));

    public static int DiscRays(CapturedPixels pixels, BoardCalibration geometry, int displayIndex)
    {
        double dx = (geometry.Right - geometry.Left) / 8, dy = (geometry.Bottom - geometry.Top) / 9;
        double cx = geometry.Left + displayIndex % 9 * dx, cy = geometry.Top + displayIndex / 9 * dy;
        int Difference(int a, int b)
        {
            int sum = 0;
            for (int channel = 0; channel < 3; channel++)
            { int difference = pixels.Bgra[a + channel] - pixels.Bgra[b + channel]; sum += difference * difference; }
            return sum;
        }
        int best = 0;
        // Raised pieces put their face slightly above the grid. Bounded centre
        // search also tolerates subpixel calibration error on small captures.
        ReadOnlySpan<double> verticalOffsets = [-.12, -.06, 0];
        ReadOnlySpan<double> horizontalOffsets = [-.04, 0, .04];
        ReadOnlySpan<double> radii = [.30, .34, .38, .42, .46];
        foreach (double oy in verticalOffsets)
        foreach (double ox in horizontalOffsets)
        {
            int count = 0;
            for (int ray = 0; ray < 24; ray++)
            {
                double angle = (ray + .5) * Math.PI / 12, cos = Math.Cos(angle), sin = Math.Sin(angle);
                int Pixel(double radius, double radial, double tangent)
                {
                    int x = Math.Clamp((int)Math.Round(cx + (ox + cos * (radius + radial) - sin * tangent) * dx), 0, pixels.Width - 1);
                    int y = Math.Clamp((int)Math.Round(cy + (oy + sin * (radius + radial) + cos * tangent) * dy), 0, pixels.Height - 1);
                    return y * pixels.RowBytes + x * 4;
                }
                foreach (double radius in radii)
                {
                    int radial = Difference(Pixel(radius, -.025, 0), Pixel(radius, .025, 0));
                    int tangent = Difference(Pixel(radius, 0, -.025), Pixel(radius, 0, .025));
                    if (radial > 3 * 255 * 255 * .08 * .08 && radial > tangent * 1.8 * 1.8)
                    { count++; break; }
                }
            }
            best = Math.Max(best, count);
            if (best == 24) return best;
        }
        return best;
    }
}
