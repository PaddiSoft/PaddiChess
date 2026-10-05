using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using SkiaSharp;
using System.Diagnostics;
using Xunit.Abstractions;

namespace PaddiXiangqi.Tests;

public class ExternalRecognitionTests(ITestOutputHelper output)
{
    private static readonly BoardCalibration Geometry = new(40, 40, 440, 490, false);
    [Fact]
    public void FindsBoardGridInLargerWindow()
    {
        using var board = SKBitmap.Decode(ExternalBoardTests.Render(new XiangqiGame()));
        using var window = new SKBitmap(1050, 700); using var canvas = new SKCanvas(window);
        canvas.Clear(SKColors.White); canvas.DrawBitmap(board, 73, 61);
        using var png = window.Encode(SKEncodedImageFormat.Png, 100);
        var found = BoardLocator.Locate(png.ToArray());
        Assert.NotNull(found);
        Assert.InRange(found.Left, 110, 116); Assert.InRange(found.Top, 98, 104);
        Assert.InRange(found.Right, 510, 516); Assert.InRange(found.Bottom, 548, 554);
    }
    [Fact]
    public void BlankImageDoesNotProduceCalibration()
    {
        using var image = new SKBitmap(600, 700); image.Erase(SKColors.White);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        Assert.Null(BoardLocator.Locate(png.ToArray()));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LearnedSkinRecognizesMiddleGameAndOrientation(bool flipped)
    {
        var game = new XiangqiGame(); var geometry = Geometry with { RedAtTop = flipped };
        BoardObservation Observe() => BoardObservation.Read(ExternalBoardTests.Render(game, flipped), geometry);
        var skin = BoardSkin.Learn("test", Observe(), game);
        foreach (var move in new[] { "h2e2", "h9g7", "e2e6", "a9a8" }) Assert.True(game.TryMoveUci(move, out _));
        var read = skin.Recognize(Observe(), game.RedToMove);
        Assert.True(read.Confident, $"{string.Join(',',read.Uncertain)} {read.Problem}");
        Assert.Equal(game.CurrentFen().Split(' ')[0], read.Fen.Split(' ')[0]);
    }
    [Fact]
    public void SkinRejectsOcclusionAndSurvivesSaveReload()
    {
        var game = new XiangqiGame();
        var skin = BoardSkin.Learn("test", BoardObservation.Read(ExternalBoardTests.Render(game), Geometry), game);
        var path = Path.Combine(Path.GetTempPath(), $"paddi-skin-{Guid.NewGuid():N}.json");
        try
        {
            skin.Save(path); var loaded = BoardSkin.Load(path);
            Assert.Equal("test", loaded.Name);
            Assert.False(loaded.Recognize(BoardObservation.Read(ExternalBoardTests.Render(game, obstruction:true), Geometry), true).Confident);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void ExplicitRecoveryFindsTwoMissedPlies()
    {
        var game=new XiangqiGame(); var tracker=new ExternalBoardTracker(BoardObservation.Read(ExternalBoardTests.Render(game),Geometry),game);
        game.TryMoveUci("h2e2",out _);game.TryMoveUci("h9g7",out _);
        var after=BoardObservation.Read(ExternalBoardTests.Render(game),Geometry);game.Undo();game.Undo();
        var result=tracker.Match(after,game,recoverMissedPair:true);
        Assert.True(result.Recognized,$"{result.Error}: {result.Message}");Assert.Equal(new[]{"h2e2","h9g7"},result.Moves);
    }
    [Fact]
    public void VectorDistanceAgreesWithScalar()
    {
        var rng = new Random(8);
        var a = Enumerable.Range(0,1728).Select(_=>(float)rng.NextDouble()).ToArray();
        var b = Enumerable.Range(0,1728).Select(_=>(float)rng.NextDouble()).ToArray();
        var expected = a.Select((x,i)=>(double)Math.Abs(x-b[i])).Average();
        Assert.InRange(Math.Abs(expected-BoardObservation.Distance(a,b)),0,1e-6);
    }
    [Fact]
    public void CachedRecognitionBenchmark()
    {
        var game = new XiangqiGame(); var tracker = new ExternalBoardTracker(BoardObservation.Read(ExternalBoardTests.Render(game),Geometry),game);
        game.TryMoveUci("h2e2",out _); game.TryMoveUci("h9g7",out _);
        var after=BoardObservation.Read(ExternalBoardTests.Render(game),Geometry); game.Undo(); game.Undo();
        tracker.Match(after,game,"h2e2");
        var clock=Stopwatch.StartNew();
        for(int i=0;i<100;i++) Assert.True(tracker.Match(after,game,"h2e2").Recognized);
        output.WriteLine($"Cached two-ply recognition: {clock.Elapsed.TotalMilliseconds/100:F3} ms/frame (100 frames)");
        // Generous regression ceiling, not a hardware-sensitive microbenchmark gate.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }
}

public class IphoneMirroringRegressionTests
{
    private static string Reference => Path.Combine(AppContext.BaseDirectory,"Assets","BoardSkins","jj-classic.png");
    [Theory]
    [InlineData(1)] [InlineData(.5)] [InlineData(.75)]
    public void RecognizesUserReportedMiddleGameAtMirrorScales(double scale)
    {
        using var source=SKBitmap.Decode(Reference);
        using var image=source.Resize(new SKImageInfo((int)(source.Width*scale),(int)(source.Height*scale)),new SKSamplingOptions(SKFilterMode.Linear));
        using var png=image.Encode(SKEncodedImageFormat.Png,100);
        var g=BuiltInBoardSkins.JjReferenceGeometry;
        g=new(g.Left*scale,g.Top*scale,g.Right*scale,g.Bottom*scale,false);
        var read=BuiltInBoardSkins.All[0].Recognize(BoardObservation.Read(png.ToArray(),g),true);
        Assert.Equal(BuiltInBoardSkins.JjReferenceFen,read.Fen);
        Assert.True(read.Confident,$"uncertain {string.Join(',',read.Uncertain)} error {read.Error:F4} {read.Problem}");
    }
    [Fact]
    public void FindsReportedIphoneBoard()
    {
        var found=BoardLocator.Locate(File.ReadAllBytes(Reference)); Assert.NotNull(found);
        var expected=BuiltInBoardSkins.JjReferenceGeometry;
        Assert.InRange(Math.Abs(found.Left-expected.Left),0,4);
        Assert.InRange(Math.Abs(found.Top-expected.Top),0,4);
        Assert.InRange(Math.Abs(found.Right-expected.Right),0,4);
        Assert.InRange(Math.Abs(found.Bottom-expected.Bottom),0,8);
        // The useful acceptance criterion is the correct position at detected geometry,
        // including the slight perspective/raised discs of this 3D-styled board.
        var read=BuiltInBoardSkins.All[0].Recognize(BoardObservation.Read(File.ReadAllBytes(Reference),found),true);
        Assert.True(read.Confident); Assert.Equal(BuiltInBoardSkins.JjReferenceFen,read.Fen);
    }
    [Fact]
    public void TracksBlackHorseWithWhiteOriginMarkOnJjSkin()
    {
        var geometry=BuiltInBoardSkins.JjReferenceGeometry;
        var bytes=File.ReadAllBytes(Reference);
        var game=new XiangqiGame(); game.LoadFen(BuiltInBoardSkins.JjReferenceFen.Replace(" w "," b "));
        var tracker=new ExternalBoardTracker(BoardObservation.Read(bytes,geometry),game);
        Assert.Contains(game.AllLegalMoves(),m=>m.Uci=="c7b9");
        using var source=SKBitmap.Decode(bytes); using var after=source.Copy(); using var canvas=new SKCanvas(after);
        var from=geometry.Point(new Square(2,2));var to=geometry.Point(new Square(1,0)); var empty=geometry.Point(new Square(1,1));
        SKRect Area((double X,double Y) p)=>SKRect.Create((float)p.X-30,(float)p.Y-30,60,60);
        canvas.DrawBitmap(source,Area(empty),Area(from)); canvas.DrawBitmap(source,Area(from),Area(to));
        using var dot=new SKPaint{Color=SKColors.White,Style=SKPaintStyle.Stroke,StrokeWidth=2,IsAntialias=true};
        canvas.DrawCircle((float)from.X,(float)from.Y,7,dot);
        using var png=after.Encode(SKEncodedImageFormat.Png,100);
        var match=tracker.Match(BoardObservation.Read(png.ToArray(),geometry),game);
        Assert.True(match.Recognized,$"{match.Error}: {match.Message}"); Assert.Equal("c7b9",Assert.Single(match.Moves));
    }
}
