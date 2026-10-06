using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PaddiXiangqi.External;

public sealed class CalibrationView : Control, IDisposable
{
    private Bitmap? _bitmap;
    public bool IsPicking { get; set; } = true;
    public Point? First { get; private set; }
    public Point? Last { get; private set; }
    public event Action? Changed;
    public int PixelWidth => _bitmap?.PixelSize.Width ?? 0;
    public int PixelHeight => _bitmap?.PixelSize.Height ?? 0;
    public CalibrationView() { ClipToBounds = true; }
    public CalibrationView(byte[] png) : this() => SetImage(png);
    public void SetImage(byte[] png)
    {
        var next = new Bitmap(new MemoryStream(png));
        var previous = _bitmap; _bitmap = next; previous?.Dispose();
        InvalidateVisual();
    }
    private double Scale => _bitmap == null ? 0 : Math.Min(Bounds.Width / PixelWidth, Bounds.Height / PixelHeight);
    private Point Origin => new((Bounds.Width - PixelWidth * Scale) / 2, (Bounds.Height - PixelHeight * Scale) / 2);
    public void Set(BoardCalibration geometry)
    {
        First = new(geometry.Left, geometry.Top); Last = new(geometry.Right, geometry.Bottom);
        InvalidateVisual(); Changed?.Invoke();
    }
    public void Reset() { First = Last = null; InvalidateVisual(); Changed?.Invoke(); }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsPicking || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Scale <= 0) return;
        var p = e.GetPosition(this); var origin = Origin;
        var pixel = new Point((p.X - origin.X) / Scale, (p.Y - origin.Y) / Scale);
        if (pixel.X < 0 || pixel.Y < 0 || pixel.X >= PixelWidth || pixel.Y >= PixelHeight) return;
        if (First == null || Last != null) { First = pixel; Last = null; }
        else Last = pixel;
        InvalidateVisual(); Changed?.Invoke();
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_bitmap == null || Scale <= 0) return;
        context.DrawImage(_bitmap, new Rect(_bitmap.Size), new Rect(Origin, new Size(PixelWidth * Scale, PixelHeight * Scale)));
        Point Screen(Point p) => new(Origin.X + p.X * Scale, Origin.Y + p.Y * Scale);
        var pen = new Pen(Brushes.DeepSkyBlue, 1.5);
        if (First is { } first)
        {
            context.DrawEllipse(null, new Pen(Brushes.Red, 2), Screen(first), 6, 6);
            if (Last is { } last)
            {
                context.DrawEllipse(null, new Pen(Brushes.Red, 2), Screen(last), 6, 6);
                for (int f = 0; f < 9; f++) for (int r = 0; r < 10; r++)
                    context.DrawEllipse(null, pen, Screen(new Point(first.X + (last.X-first.X)*f/8, first.Y+(last.Y-first.Y)*r/9)), 2.5, 2.5);
            }
        }
    }
    public void Dispose() { _bitmap?.Dispose(); _bitmap = null; }
}
