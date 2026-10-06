using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using SkiaSharp;

namespace PaddiXiangqi.Tests;

public class ExternalBoardTests
{
    private static readonly BoardCalibration Geometry = new(40, 40, 440, 490, false);
    public static byte[] Render(XiangqiGame game, bool flipped = false, int width = 480, bool obstruction = false)
    {
        using var bmp = new SKBitmap(width, 530);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(232, 201, 151));
        using var grid = new SKPaint { Color = new SKColor(128, 92, 52), StrokeWidth = 1.3f, IsAntialias = true };
        for (int i = 0; i < 9; i++) canvas.DrawLine(40+i*50,40,40+i*50,490,grid);
        for (int i = 0; i < 10; i++) canvas.DrawLine(40,40+i*50,440,40+i*50,grid);
        using var piece = new SKPaint { IsAntialias = true };
        using var text = new SKPaint { IsAntialias = true, Color = SKColors.White };
        using var font = new SKFont(SKTypeface.Default, 26);
        for (int r = 0; r < 10; r++) for (int f = 0; f < 9; f++)
        {
            var c = game.Board[r,f]; if (c == '\0') continue;
            var (x,y) = (Geometry with { RedAtTop = flipped }).Point(new Square(f,r));
            piece.Color = char.IsUpper(c) ? new SKColor(170, 42, 50) : new SKColor(32, 40, 45);
            canvas.DrawCircle((float)x,(float)y,22,piece);
            canvas.DrawText(char.ToUpper(c).ToString(), (float)x, (float)y+9, SKTextAlign.Center, font, text);
        }
        if (obstruction) { piece.Color = SKColors.White; canvas.DrawRect(150, 150, 200, 120, piece); }
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
    private static BoardObservation Observe(XiangqiGame game, bool flipped = false) => BoardObservation.Read(Render(game,flipped),Geometry with { RedAtTop=flipped });

    [Theory]
    [InlineData(false, 40, 490)]
    [InlineData(true, 440, 40)]
    public void CoordinatesRespectOrientation(bool redAtTop, double x, double y)
    {
        var point = (Geometry with { RedAtTop=redAtTop }).Point(new Square(0,9));
        Assert.Equal(x,point.X); Assert.Equal(y,point.Y);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetectsOrientationAndTracksLegalMove(bool flipped)
    {
        var game = new XiangqiGame(); var start = Observe(game,flipped);
        Assert.Equal(flipped,BoardObservation.DetectRedAtTop(Render(game,flipped),Geometry));
        var tracker = new ExternalBoardTracker(start,game);
        Assert.True(game.TryMoveUci("h2e2",out _));
        var next = Observe(game,flipped);
        game.Undo();
        var match = tracker.Match(next,game);
        Assert.True(match.Recognized,$"{match.Error}: {match.Message}");
        Assert.Equal(new[]{"h2e2"},match.Moves);
    }
    [Fact]
    public void UnchangedBoardNeverProducesMove()
    {
        var game=new XiangqiGame(); var view=Observe(game);
        var match=new ExternalBoardTracker(view,game).Match(view,game);
        Assert.True(match.Recognized); Assert.Empty(match.Moves);
    }
    [Fact]
    public void AcceptsFastOpponentReplyOnlyWhenOurMoveIsPending()
    {
        var game=new XiangqiGame(); var tracker=new ExternalBoardTracker(Observe(game),game);
        game.TryMoveUci("h2e2",out _); game.TryMoveUci("h9g7",out _);
        var next=Observe(game); game.Undo(); game.Undo();
        var match=tracker.Match(next,game,"h2e2");
        Assert.True(match.Recognized,$"{match.Error}: {match.Message}");
        Assert.Equal(new[]{"h2e2","h9g7"},match.Moves);
        Assert.False(tracker.Match(next,game).Recognized);
    }
    [Fact]
    public void WrongPendingMoveCannotBeConfirmed()
    {
        var game=new XiangqiGame(); var tracker=new ExternalBoardTracker(Observe(game),game);
        game.TryMoveUci("h2e2",out _); var next=Observe(game); game.Undo();
        Assert.False(tracker.Match(next,game,"b0c2").Recognized);
    }
    [Fact]
    public void TracksCaptureAcrossAcceptedFrames()
    {
        var game=new XiangqiGame(); var tracker=new ExternalBoardTracker(Observe(game),game);
        foreach (var uci in new[]{"h2e2","h9g7","e2e6"})
        {
            Assert.True(game.TryMoveUci(uci,out _)); var after=Observe(game); game.Undo();
            var match=tracker.Match(after,game);
            Assert.True(match.Recognized,$"{uci}, {match.Error}: {match.Message}");
            Assert.Equal(uci,Assert.Single(match.Moves));
            game.TryMoveUci(uci,out _); tracker.Accept(after,game);
        }
    }
    [Fact]
    public void OcclusionAndResizeAreNotInterpretedAsMoves()
    {
        var game=new XiangqiGame(); var tracker=new ExternalBoardTracker(Observe(game),game);
        var hidden=BoardObservation.Read(Render(game,obstruction:true),Geometry);
        Assert.False(tracker.Match(hidden,game).Recognized);
        var resized=BoardObservation.Read(Render(game,width:500),Geometry);
        Assert.False(tracker.Match(resized,game).Recognized);
    }
    [Fact]
    public void InvalidCalibrationRejected()
    {
        Assert.Throws<InvalidOperationException>(() => new BoardCalibration(0,0,20,20,false).Validate(480,530));
        Assert.Throws<InvalidOperationException>(() => new BoardCalibration(440,490,40,40,false).Validate(480,530));
    }
    [Fact]
    public void WindowIdentityIncludesPidAndBounds()
    {
        var window=new ExternalWindow(1,10,"board",20,30,600,700);
        Assert.True(window.SameBounds(window with{Title="new title"}));
        Assert.False(window.SameBounds(window with{Pid=11}));
        Assert.False(window.SameBounds(window with{X=22}));
    }
}
