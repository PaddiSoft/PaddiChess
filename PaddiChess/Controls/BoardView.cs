using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using PaddiXiangqi.Core;

namespace PaddiXiangqi;

public readonly record struct BoardArrow(Square From, Square To, bool Opponent, int Order);

public sealed class BoardView : Control
{
    private const double Step = 55;
    private const double Left = 50;
    private const double Top = 52;
    private static readonly string[] ChineseFiles = ["一", "二", "三", "四", "五", "六", "七", "八", "九"];
    private static readonly string[] ArabicFiles = Enumerable.Range(1, 9)
        .Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray();
    private static readonly Lazy<StreamGeometry> GridGeometry = new(BuildGridGeometry);
    private static readonly Lazy<StreamGeometry> MarkerGeometry = new(BuildMarkerGeometry);
    private static readonly Dictionary<string, IBrush> Brushes = [];
    private static readonly Dictionary<(string Color, double Width), Pen> Pens = [];
    private static readonly Dictionary<(string Text, double Size, string Color, bool Bold), FormattedText> Texts = [];
    private static readonly Dictionary<char, string> PieceLabels = "RNBAKCP rnbakcp".Where(c => c != ' ')
        .ToDictionary(c => c, c => XiangqiGame.DisplayBoardPiece(c).ToString());
    private static readonly Pen RecommendationArrowPen = CreateArrowPen("#C73179C6", 3.2);
    private static readonly Pen ResponseArrowPen = CreateArrowPen("#C7C6555B", 3.2);
    private static readonly Pen SecondaryRecommendationArrowPen = CreateArrowPen("#833179C6", 2.2);
    private static readonly Pen SecondaryResponseArrowPen = CreateArrowPen("#83C6555B", 2.2);
    private readonly DispatcherTimer _timer;
    private sealed record AnimationFrame(XiangqiGame Game, int Ply, char[,] Board,
        bool RedToMove, ChessMove Move);
    private readonly Queue<AnimationFrame> _animationQueue = new();
    private AnimationFrame? _animation;
    private AnimationFrame? _latestAnimation;
    private long _animationStart;
    private double _animationStartProgress;
    private double _animationSegmentMs;
    private double _queuedAnimationMs;
    private bool _showCoordinates = true;
    private int _animationDurationMs = 230;
    private XiangqiGame? _checkedGame;
    private char[,]? _checkedBoard;
    private bool _checkedRedToMove;
    private Square? _checkedKing;

    public BoardView()
    {
        Width = 540;
        Height = 600;
        Cursor = new Cursor(StandardCursorType.Hand);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            AdvanceAnimation();
            InvalidateVisual();
        });
    }

    public XiangqiGame? Game { get; set; }
    public char[,]? SetupBoard { get; set; }
    public bool Flipped { get; set; }
    public bool LiveSync { get; set; }
    private int EffectiveAnimationMs => LiveSync ? Math.Min(_animationDurationMs, 65) : _animationDurationMs;
    public bool ShowCoordinates
    {
        get => _showCoordinates;
        set
        {
            if (_showCoordinates == value) return;
            _showCoordinates = value;
            InvalidateVisual();
        }
    }
    public int AnimationDurationMs
    {
        get => _animationDurationMs;
        set
        {
            var duration = Math.Max(0, value);
            if (_animationDurationMs == duration) return;
            _animationDurationMs = duration;
            if (duration != 0) return;
            ClearAnimations();
            InvalidateVisual();
        }
    }
    public Square? Selected { get; set; }
    public Square? HintFrom { get; set; }
    public Square? HintTo { get; set; }
    public IReadOnlyList<BoardArrow> AnalysisArrows { get; set; } = [];
    public IReadOnlyList<ChessMove> LegalMoves { get; set; } = [];
    public event Action<Square>? SquareClicked;

    public void Refresh() => InvalidateVisual();

    public void Animate(ChessMove move)
    {
        if (_animationDurationMs == 0 || Game is not { } game || SetupBoard is not null)
        {
            ClearAnimations();
            InvalidateVisual();
            return;
        }
        // Positions own immutable board arrays. Keep the confirmed intermediate board
        // even when two externally observed plies arrive in the same dispatcher turn.
        var frame = new AnimationFrame(game, game.Ply, game.Board, game.RedToMove, move);
        if (_animation is not null && _latestAnimation is { } latest &&
            ReferenceEquals(latest.Game, game) && frame.Ply == latest.Ply + 1 &&
            latest.Ply > 0 && game.History[latest.Ply - 1] == latest.Move)
        {
            _animationQueue.Enqueue(frame);
            _latestAnimation = frame;
            // Catch up within 350 ms without holding up capture, search or input.
            // Rebase progress so shortening an active animation does not jump backwards.
            _animationStartProgress = AnimationProgress;
            _queuedAnimationMs = Math.Min(EffectiveAnimationMs, (LiveSync ? 100d : 350d) / (_animationQueue.Count + 1));
            _animationSegmentMs = Math.Min(_queuedAnimationMs,
                Math.Max(1, _animationSegmentMs - Stopwatch.GetElapsedTime(_animationStart).TotalMilliseconds));
            _animationStart = Stopwatch.GetTimestamp();
        }
        else
        {
            ClearAnimations();
            _latestAnimation = frame;
            _queuedAnimationMs = EffectiveAnimationMs;
            StartAnimation(frame);
        }
        _timer.Start();
        InvalidateVisual();
    }

    private void StartAnimation(AnimationFrame frame)
    {
        _animation = frame;
        _animationStart = Stopwatch.GetTimestamp();
        _animationStartProgress = 0;
        _animationSegmentMs = _queuedAnimationMs;
    }

    private void AdvanceAnimation()
    {
        if (_latestAnimation is { } latest &&
            (SetupBoard is not null || Game is not { } game || !ReferenceEquals(game, latest.Game) ||
             game.Ply != latest.Ply || !ReferenceEquals(game.Board, latest.Board)))
        {
            ClearAnimations();
            return;
        }
        if (AnimationProgress < 1) return;
        if (_animationQueue.TryDequeue(out var next)) StartAnimation(next);
        else ClearAnimations();
    }

    private void ClearAnimations()
    {
        _timer.Stop();
        _animation = _latestAnimation = null;
        _animationQueue.Clear();
    }

    private double AnimationProgress => _animation is null ? 1 : _animationStartProgress +
        (1 - _animationStartProgress) * Math.Clamp(
            Stopwatch.GetElapsedTime(_animationStart).TotalMilliseconds / Math.Max(1, _animationSegmentMs), 0, 1);

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ClearAnimations();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        var file = (int)Math.Round((point.X - Left) / Step);
        var rank = (int)Math.Round((point.Y - Top) / Step);
        if (file is < 0 or > 8 || rank is < 0 or > 9) return;
        var center = new Point(Left + file * Step, Top + rank * Step);
        var dx = center.X - point.X;
        var dy = center.Y - point.Y;
        if (dx * dx + dy * dy > 29 * 29) return;
        SquareClicked?.Invoke(Flipped ? new Square(8 - file, 9 - rank) : new Square(file, rank));
    }

    private Point Center(Square square)
    {
        var file = Flipped ? 8 - square.File : square.File;
        var rank = Flipped ? 9 - square.Rank : square.Rank;
        return new Point(Left + file * Step, Top + rank * Step);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        AdvanceAnimation();
        var wood = Brush("#F1D3A2");
        var line = CachedPen("#8E6B48", 1.45);
        var subtle = CachedPen("#B79161", 0.7);
        context.DrawRectangle(Brush("#D6B181"), null, new Rect(3, 3, 534, 594), 14, 14);
        context.DrawRectangle(wood, CachedPen("#C69E6E", 2), new Rect(7, 7, 526, 586), 11, 11);
        using (context.PushClip(new RoundedRect(new Rect(8, 8, 524, 584), 10)))
        using (context.PushOpacity(0.42))
            context.DrawImage(BrandAssets.BoardTexture, new Rect(8, 8, 524, 584));
        context.DrawGeometry(null, line, GridGeometry.Value);
        context.DrawGeometry(null, subtle, MarkerGeometry.Value);

        var riverY = Top + 4.5 * Step;
        DrawCentered(context, "楚 河", new Point(Left + 1.9 * Step, riverY), 26, "#886341", false);
        DrawCentered(context, "汉 界", new Point(Left + 6.1 * Step, riverY), 26, "#886341", false);
        DrawCentered(context, "Paddi象棋", new Point(Left + 4 * Step, riverY), 12, "#A45B4B", true);

        if (_showCoordinates)
        {
            for (var file = 0; file < 9; file++)
            {
                DrawCentered(context, Flipped ? ChineseFiles[file] : ArabicFiles[file],
                    new Point(Left + file * Step, 24), 13, "#845F40", true);
                DrawCentered(context, Flipped ? ArabicFiles[8 - file] : ChineseFiles[8 - file],
                    new Point(Left + file * Step, 579), 12, "#845F40", true);
            }
        }

        var board = SetupBoard ?? _animation?.Board ?? Game?.Board;
        if (board is null) return;
        var lastMove = _animation?.Move ?? Game?.LastMove;
        var displayingCurrentPosition = _animation is null || ReferenceEquals(_animation.Board, Game?.Board);
        if (SetupBoard is null && lastMove is { } last)
        {
            var from = Center(last.From);
            var to = Center(last.To);
            context.DrawEllipse(Brush("#55FFFFFF"), null, from, 15, 15);
            context.DrawEllipse(Brush("#FFFFFF"), CachedPen("#C1A275", 1.5), from, 8, 8);
            context.DrawEllipse(null, CachedPen("#F5C35E", 3.5), to, 29, 29);
        }
        if (SetupBoard is null && displayingCurrentPosition && AnalysisArrows.Count == 0
            && HintFrom is { } hintFrom && HintTo is { } hintTo)
        {
            context.DrawEllipse(null, CachedPen("#3687E6", 3), Center(hintFrom), 28, 28);
            context.DrawEllipse(Brush("#703687E6"), CachedPen("#3687E6", 2), Center(hintTo), 14, 14);
        }
        if (SetupBoard is null && displayingCurrentPosition)
        {
            foreach (var move in LegalMoves)
                context.DrawEllipse(Brush("#A04D91CF"), null, Center(move.To), 9, 9);
        }

        var progress = SetupBoard is null ? AnimationProgress : 1;
        for (var rank = 0; rank < 10; rank++)
        for (var file = 0; file < 9; file++)
        {
            var piece = board[rank, file];
            if (piece == '\0') continue;
            var square = new Square(file, rank);
            if (SetupBoard is null && _animation is { Move: var animation } && progress < 1 && square == animation.To && piece == animation.Piece) continue;
            DrawPiece(context, Center(square), piece, Selected == square);
        }
        if (SetupBoard is null && _animation is { Move: var moving } && progress < 1)
        {
            var start = Center(moving.From);
            var end = Center(moving.To);
            var eased = 1 - Math.Pow(1 - progress, 3);
            if (!LiveSync && moving.Captured != '\0' && progress < .7)
            {
                using (context.PushOpacity(1 - progress / .7))
                    DrawPiece(context, end, moving.Captured, false);
            }
            // A small lift and landing make local moves readable. Live capture
            // keeps its short, flat trajectory so presentation never delays play.
            var lift = LiveSync ? 0 : Math.Sin(Math.PI * progress) * 5;
            DrawPiece(context, new Point(start.X + (end.X - start.X) * eased,
                start.Y + (end.Y - start.Y) * eased - lift), moving.Piece, false, lift);
        }
        if (SetupBoard is null && displayingCurrentPosition)
        {
            // Show the thin route over the piece rims so one-step moves remain visible.
            // Draw weaker later moves first; the immediate recommendation stays clear.
            for (var i = AnalysisArrows.Count - 1; i >= 0; i--)
            {
                var arrow = AnalysisArrows[i];
                var sharedRoute = 0;
                for (var prior = 0; prior < i; prior++)
                {
                    var other = AnalysisArrows[prior];
                    if (other.From == arrow.From ||
                        (other.From == arrow.To && other.To == arrow.From)) sharedRoute++;
                }
                DrawArrow(context, arrow, sharedRoute);
            }
        }
        if (SetupBoard is null && Game is { } game)
        {
            var redToMove = _animation?.RedToMove ?? game.RedToMove;
            // Xiangqi positions own immutable board arrays, so repeated analysis redraws
            // can reuse the check result until the position changes.
            if (!ReferenceEquals(_checkedGame, game) || !ReferenceEquals(_checkedBoard, board)
                || _checkedRedToMove != redToMove)
            {
                _checkedGame = game;
                _checkedBoard = board;
                _checkedRedToMove = redToMove;
                _checkedKing = XiangqiGame.IsInCheck(board, redToMove) ? XiangqiGame.FindKing(board, redToMove) : null;
            }
            if (_checkedKing is { } king)
            {
                var center = Center(king);
                context.DrawEllipse(null, CachedPen("#E53D57", 4), center, 32, 32);
            }
        }
    }

    private static StreamGeometry BuildGridGeometry()
    {
        var geometry = new StreamGeometry();
        using var path = geometry.Open();
        for (var rank = 0; rank < 10; rank++)
            AddSegment(path, new Point(Left, Top + rank * Step),
                new Point(Left + 8 * Step, Top + rank * Step));
        // The two outside files cross the river; the seven inside files do not.
        for (var file = 0; file < 9; file++)
        {
            var x = Left + file * Step;
            if (file is 0 or 8)
                AddSegment(path, new Point(x, Top), new Point(x, Top + 9 * Step));
            else
            {
                AddSegment(path, new Point(x, Top), new Point(x, Top + 4 * Step));
                AddSegment(path, new Point(x, Top + 5 * Step), new Point(x, Top + 9 * Step));
            }
        }
        AddPalace(path, 0);
        AddPalace(path, 7);
        return geometry;
    }

    private static StreamGeometry BuildMarkerGeometry()
    {
        var geometry = new StreamGeometry();
        using var path = geometry.Open();
        foreach (var rank in new[] { 2, 7 })
        foreach (var file in new[] { 1, 7 }) AddCross(path, new Point(Left + file * Step, Top + rank * Step));
        foreach (var rank in new[] { 3, 6 })
        foreach (var file in new[] { 0, 2, 4, 6, 8 }) AddCross(path, new Point(Left + file * Step, Top + rank * Step));
        return geometry;
    }

    private static void AddPalace(StreamGeometryContext path, int topRank)
    {
        AddSegment(path, new Point(Left + 3 * Step, Top + topRank * Step),
            new Point(Left + 5 * Step, Top + (topRank + 2) * Step));
        AddSegment(path, new Point(Left + 5 * Step, Top + topRank * Step),
            new Point(Left + 3 * Step, Top + (topRank + 2) * Step));
    }

    private static void AddCross(StreamGeometryContext path, Point center)
    {
        for (var sx = -1; sx <= 1; sx += 2)
        for (var sy = -1; sy <= 1; sy += 2)
        {
            if (center.X + sx * 10 < Left || center.X + sx * 10 > Left + 8 * Step) continue;
            AddSegment(path, new Point(center.X + sx * 5, center.Y + sy * 5),
                new Point(center.X + sx * 12, center.Y + sy * 5));
            AddSegment(path, new Point(center.X + sx * 5, center.Y + sy * 5),
                new Point(center.X + sx * 5, center.Y + sy * 12));
        }
    }

    private static void AddSegment(StreamGeometryContext path, Point start, Point finish)
    {
        path.BeginFigure(start, false);
        path.LineTo(finish);
        path.EndFigure(false);
    }

    private void DrawArrow(DrawingContext context, BoardArrow arrow, int sharedRoute)
    {
        var start = Center(arrow.From);
        var finish = Center(arrow.To);
        var dx = finish.X - start.X;
        var dy = finish.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 35) return;
        var ux = dx / length;
        var uy = dy / length;
        var nx = -uy;
        var ny = ux;
        var lane = sharedRoute == 0 ? 0 :
            Math.Min(12, ((sharedRoute + 1) / 2) * 5.5) * (sharedRoute % 2 == 1 ? 1 : -1);
        var startInset = Math.Min(25, length * 0.35);
        var endInset = Math.Min(26, length * 0.35);
        var source = new Point(start.X + ux * startInset + nx * lane,
            start.Y + uy * startInset + ny * lane);
        var tip = new Point(finish.X - ux * endInset + nx * lane,
            finish.Y - uy * endInset + ny * lane);
        var prominent = arrow.Order < 2;
        var pen = arrow.Opponent
            ? prominent ? ResponseArrowPen : SecondaryResponseArrowPen
            : prominent ? RecommendationArrowPen : SecondaryRecommendationArrowPen;
        context.DrawLine(pen, source, tip);
        var headLength = Math.Min(8.5, (length - startInset - endInset) * 0.47);
        var headWidth = prominent ? 4.8 : 3.5;
        var wingA = new Point(tip.X - ux * headLength + nx * headWidth,
            tip.Y - uy * headLength + ny * headWidth);
        var wingB = new Point(tip.X - ux * headLength - nx * headWidth,
            tip.Y - uy * headLength - ny * headWidth);
        context.DrawLine(pen, wingA, tip);
        context.DrawLine(pen, wingB, tip);
    }

    private static Pen CreateArrowPen(string color, double width) => new(Brush(color), width)
    {
        LineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round
    };

    private static void DrawPiece(DrawingContext context, Point center, char piece, bool selected, double lift = 0)
    {
        var red = XiangqiGame.IsRed(piece);
        context.DrawEllipse(Brush("#39553B29"), null, new Point(center.X + 2, center.Y + 4 + lift), 26 + lift * .25, 26 + lift * .25);
        context.DrawEllipse(Brush(red ? "#9F3338" : "#232D34"),
            CachedPen(red ? "#7D252A" : "#11191E", 1.7), center, 25, 25);
        context.DrawEllipse(null, CachedPen(red ? "#E59A8D" : "#778087", 1.3), center, 20.5, 20.5);
        DrawCentered(context, PieceLabels[piece],
            new Point(center.X, center.Y - 1), 30, "#FFF9EE", true);
        if (selected) context.DrawEllipse(null, CachedPen("#3F9EFF", 3.5), center, 29, 29);
    }

    private static void DrawCentered(DrawingContext context, string text, Point center,
        double size, string color, bool bold)
    {
        var key = (text, size, color, bold);
        if (!Texts.TryGetValue(key, out var formatted))
        {
            formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal),
                size, Brush(color));
            Texts[key] = formatted;
        }
        context.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    private static IBrush Brush(string color)
    {
        if (!Brushes.TryGetValue(color, out var brush)) Brushes[color] = brush = new SolidColorBrush(Color.Parse(color));
        return brush;
    }

    private static Pen CachedPen(string color, double width)
    {
        var key = (color, width);
        if (!Pens.TryGetValue(key, out var pen)) Pens[key] = pen = new Pen(Brush(color), width);
        return pen;
    }
}
