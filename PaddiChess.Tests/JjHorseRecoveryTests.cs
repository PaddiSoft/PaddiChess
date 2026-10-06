using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using SkiaSharp;

namespace PaddiXiangqi.Tests;

public class JjHorseRecoveryTests
{
    private const string Expected = "rnbakab1r/9/1c4nc1/p1p1p1p1p/9/9/P1P1P1P1P/1C2C4/9/RNBAKABNR";
    private static byte[] ActualScreenshot() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","jj-horse-reply.png"));
    private static byte[] ReconstructBefore(byte[] after, BoardCalibration g)
    {
        // Use the supplied real AFTER screenshot; reconstruct BEFORE by moving just the two pieces back.
        // This is not claimed to be a second real screenshot.
        using var source=SKBitmap.Decode(after); using var before=source.Copy();using var canvas=new SKCanvas(before);
        var size=(float)((g.Right-g.Left)/8*.85);
        SKRect Area(Square s) { var p=g.Point(s);return SKRect.Create((float)p.X-size/2,(float)p.Y-size/2,size,size); }
        canvas.DrawBitmap(source,Area(new(4,7)),Area(new(7,7)));
        canvas.DrawBitmap(source,Area(new(5,7)),Area(new(4,7)));
        canvas.DrawBitmap(source,Area(new(6,2)),Area(new(7,0)));
        canvas.DrawBitmap(source,Area(new(5,2)),Area(new(6,2)));
        using var png=before.Encode(SKEncodedImageFormat.Png,100);return png.ToArray();
    }
    [Theory]
    [InlineData(1)] [InlineData(.5)]
    public void ActualHorseReplyRecoversBothPliesWithoutRaisingPixelThreshold(double scale)
    {
        var bytes=ActualScreenshot();
        using var source=SKBitmap.Decode(bytes);
        using var resized=source.Resize(new SKImageInfo((int)(source.Width*scale),(int)(source.Height*scale)),new SKSamplingOptions(SKFilterMode.Linear));
        using var png=resized.Encode(SKEncodedImageFormat.Png,100);bytes=png.ToArray();
        var geometry=BoardLocator.Locate(bytes);Assert.NotNull(geometry);
        Assert.True(geometry.Right-geometry.Left>source.Width*scale*.7,"Must locate the complete board, not a small repeated motif");
        var after=BoardObservation.Read(bytes,geometry);
        var read=BuiltInBoardSkins.All[0].Recognize(after,true);
        Assert.Equal(Expected,read.Fen.Split(' ')[0]);
        var game=new XiangqiGame();var tracker=new ExternalBoardTracker(BoardObservation.Read(ReconstructBefore(bytes,geometry),geometry),game);
        var match=tracker.MatchRecognizedPosition(read,game,"h2e2",after);
        Assert.True(match.Recognized,$"{match.Message}; uncertain: {string.Join(',',read.Uncertain)}");
        Assert.Equal(new[]{"h2e2","h9g7"},match.Moves);
        Assert.False(tracker.MatchRecognizedPosition(read,game,"b0c2",after).Recognized);
        foreach(var move in match.Moves)Assert.True(game.TryMoveUci(move,out _));
        Assert.Equal(Expected,game.CurrentFen().Split(' ')[0]);Assert.True(game.RedToMove);
    }
    [Fact]
    public void UncertainChangedPieceCannotBeReusedFromPreviousPosition()
    {
        var bytes=ActualScreenshot();var g=BoardLocator.Locate(bytes)!;var after=BoardObservation.Read(bytes,g);
        var read=BuiltInBoardSkins.All[0].Recognize(after,true) with { Uncertain=[new Square(6,2)] };
        var game=new XiangqiGame();var tracker=new ExternalBoardTracker(BoardObservation.Read(ReconstructBefore(bytes,g),g),game);
        Assert.False(tracker.MatchRecognizedPosition(read,game,"h2e2",after).Recognized);
    }
}
