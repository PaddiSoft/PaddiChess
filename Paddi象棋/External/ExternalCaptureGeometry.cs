using SkiaSharp;

namespace PaddiXiangqi.External;

/// <summary>Relates screenshot pixels to a window's logical input coordinates.</summary>
public static class ExternalCaptureGeometry
{
    public static bool TryRebaseCalibration(ExternalFrame previous, ExternalFrame current,
        BoardCalibration calibration, out BoardCalibration mapped)
    {
        mapped = calibration;
        var before = previous.Window;
        var after = current.Window;
        // A backing-scale change does not move the board inside the logical
        // window. A real resize can reflow it, so it must be located again.
        if (before.Id != after.Id || before.Pid != after.Pid ||
            !double.IsFinite(before.Width) || !double.IsFinite(before.Height) ||
            !double.IsFinite(after.Width) || !double.IsFinite(after.Height) ||
            before.Width <= 0 || before.Height <= 0 ||
            Math.Abs(before.Width - after.Width) >= .01 || Math.Abs(before.Height - after.Height) >= .01)
            return false;
        if (!TryPixelSize(previous, out var oldWidth, out var oldHeight) ||
            !TryPixelSize(current, out var width, out var height)) return false;
        if (!double.IsFinite(calibration.Left) || !double.IsFinite(calibration.Top) ||
            !double.IsFinite(calibration.Right) || !double.IsFinite(calibration.Bottom)) return false;
        try
        {
            calibration.Validate(oldWidth, oldHeight);
            var resized = oldWidth == width && oldHeight == height ? calibration : calibration with
            {
                Left = calibration.Left * width / oldWidth,
                Right = calibration.Right * width / oldWidth,
                Top = calibration.Top * height / oldHeight,
                Bottom = calibration.Bottom * height / oldHeight
            };
            resized.Validate(width, height);
            mapped = resized;
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    public static (double X, double Y) ToWindowPoint(double pixelX, double pixelY,
        int pixelWidth, int pixelHeight, ExternalWindow window)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || !double.IsFinite(pixelX) || !double.IsFinite(pixelY) ||
            !double.IsFinite(window.Width) || !double.IsFinite(window.Height) || window.Width <= 0 || window.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), "截图或窗口尺寸无效");
        return (pixelX * window.Width / pixelWidth, pixelY * window.Height / pixelHeight);
    }

    private static bool TryPixelSize(ExternalFrame frame, out int width, out int height)
    {
        if (frame.Pixels is { } pixels)
        { width = pixels.Width; height = pixels.Height; return width > 0 && height > 0; }
        // Read only PNG metadata. Re-scaling calibration must not decode or
        // re-encode a multi-megabyte Retina image on the UI thread.
        using var stream = new SKMemoryStream(frame.Png);
        using var codec = SKCodec.Create(stream);
        width = codec?.Info.Width ?? 0; height = codec?.Info.Height ?? 0;
        return width > 0 && height > 0;
    }
}
