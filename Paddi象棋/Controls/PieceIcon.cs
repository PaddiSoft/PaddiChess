using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PaddiXiangqi.Core;

namespace PaddiXiangqi;

/// <summary>A scalable board-style piece for palette buttons. Layout remains in AXAML.</summary>
public sealed class PieceIcon : Control
{
    public static readonly StyledProperty<char> PieceProperty = AvaloniaProperty.Register<PieceIcon, char>(nameof(Piece));
    private static readonly Dictionary<char, FormattedText> Labels = [];
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#9F3338"));
    private static readonly IBrush Black = new SolidColorBrush(Color.Parse("#232D34"));
    private static readonly Pen RedRing = new(new SolidColorBrush(Color.Parse("#E59A8D")), 1.2);
    private static readonly Pen BlackRing = new(new SolidColorBrush(Color.Parse("#778087")), 1.2);
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#FFF9EE"));
    public char Piece { get => GetValue(PieceProperty); set => SetValue(PieceProperty, value); }
    static PieceIcon() => AffectsRender<PieceIcon>(PieceProperty);
    public override void Render(DrawingContext context)
    {
        if (PositionSetup.MaximumCount(Piece) == 0) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - 1;
        var red = char.IsUpper(Piece);
        context.DrawEllipse(red ? Red : Black, null, center, radius, radius);
        context.DrawEllipse(null, red ? RedRing : BlackRing, center, radius - 3.5, radius - 3.5);
        if (!Labels.TryGetValue(Piece, out var text))
            Labels[Piece] = text = new FormattedText(XiangqiGame.DisplayBoardPiece(Piece).ToString(), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 25, Ink);
        context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2 - 1));
    }
}
