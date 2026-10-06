using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PaddiXiangqi.Engine;

namespace PaddiXiangqi;

public sealed class AdvantageChart : Control
{
    private static readonly IBrush RedBrush = new SolidColorBrush(Color.Parse("#B4484F"));
    private static readonly IBrush BlackBrush = new SolidColorBrush(Color.Parse("#344354"));
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#F9FBFE"));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#E7ECF3")), 1);
    private static readonly Pen ScorePen = new(RedBrush, 2.5);
    private static readonly Pen CursorPen = new(new SolidColorBrush(Color.Parse("#75A1DE")), 1.5);
    private static readonly Typeface LabelTypeface = new(FontFamily.Default);
    private static readonly FormattedText RedLabel = CreateText("红优", RedBrush);
    private static readonly FormattedText BlackLabel = CreateText("黑优", BlackBrush);
    private KeyValuePair<int, double>[] _points = [];
    private FormattedText? _scoreLabel;
    private string? _scoreLabelValue;
    private bool _scoreLabelIsRed;
    private int _plottedMaxPly = int.MinValue;
    private int _plottedCurrentPly = int.MinValue;
    private string? _plottedLabelOverride;

    public IReadOnlyDictionary<int, double> Scores { get; set; } = new Dictionary<int, double>();
    public IReadOnlyDictionary<int, string> ScoreLabels { get; set; } = new Dictionary<int, string>();
    public int MaxPly { get; set; }
    public int CurrentPly { get; set; }
    public string? ScoreLabelOverride { get; set; }
    public event Action<int>? PlySelected;

    public AdvantageChart()
    {
        Height = 86;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public void Refresh()
    {
        // Status/model updates often leave the score series unchanged. Compare without
        // allocating another sorted array or scheduling another paint for those updates.
        var visibleCount = 0;
        foreach (var pair in Scores)
            if (pair.Key <= MaxPly) visibleCount++;
        var seriesChanged = visibleCount != _points.Length;
        for (var i = 0; !seriesChanged && i < _points.Length; i++)
        {
            var point = _points[i];
            seriesChanged = point.Key > MaxPly || !Scores.TryGetValue(point.Key, out var value) || value != point.Value;
        }
        var labelOverride = ScoreLabelOverride ?? ScoreLabels.GetValueOrDefault(CurrentPly);
        if (!seriesChanged && _plottedMaxPly == MaxPly && _plottedCurrentPly == CurrentPly && _plottedLabelOverride == labelOverride) return;
        if (seriesChanged)
            _points = Scores.Where(pair => pair.Key <= MaxPly).OrderBy(pair => pair.Key).ToArray();
        _plottedMaxPly = MaxPly;
        _plottedCurrentPly = CurrentPly;
        _plottedLabelOverride = labelOverride;
        if (Scores.TryGetValue(CurrentPly, out var current))
        {
            var value = labelOverride ?? AnalysisFormatter.ScoreForRed(current);
            var isRed = current >= 0;
            if (_scoreLabelValue != value || _scoreLabelIsRed != isRed)
            {
                _scoreLabel = CreateText(value, isRed ? RedBrush : BlackBrush);
                _scoreLabelValue = value;
                _scoreLabelIsRed = isRed;
            }
        }
        else
        {
            _scoreLabel = null;
            _scoreLabelValue = null;
        }
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var plotWidth = Math.Max(1, Bounds.Width - 72);
        var fraction = Math.Clamp((e.GetPosition(this).X - 48) / plotWidth, 0, 1);
        PlySelected?.Invoke((int)Math.Round(fraction * MaxPly));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        if (width < 100) return;
        var height = Bounds.Height;
        var left = 48.0;
        var right = width - 24;
        var center = height / 2;
        context.DrawRectangle(BackgroundBrush, null,
            new Rect(0, 0, width, height), 10, 10);
        context.DrawLine(GridPen, new Point(left, 18), new Point(right, 18));
        context.DrawLine(GridPen, new Point(left, center), new Point(right, center));
        context.DrawLine(GridPen, new Point(left, height - 18), new Point(right, height - 18));
        context.DrawText(RedLabel, new Point(8, 6));
        context.DrawText(BlackLabel, new Point(8, height - 22));

        double X(int ply) => left + (right - left) * ply / Math.Max(1, MaxPly);
        double Y(double score) => center - Math.Tanh(score / 300.0) * Math.Max(12, center - 18);
        for (var i = 1; i < _points.Length; i++)
            context.DrawLine(ScorePen,
                new Point(X(_points[i - 1].Key), Y(_points[i - 1].Value)),
                new Point(X(_points[i].Key), Y(_points[i].Value)));
        foreach (var pair in _points)
        {
            var point = new Point(X(pair.Key), Y(pair.Value));
            context.DrawEllipse(pair.Value >= 0 ? RedBrush : BlackBrush, null, point, 3.5, 3.5);
        }
        var cursorX = X(Math.Clamp(CurrentPly, 0, MaxPly));
        context.DrawLine(CursorPen,
            new Point(cursorX, 7), new Point(cursorX, height - 7));
        if (_scoreLabel is not null)
            context.DrawText(_scoreLabel, new Point(Math.Max(left, right - _scoreLabel.Width), 5));
    }

    private static FormattedText CreateText(string text, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelTypeface, 11, brush);
}
