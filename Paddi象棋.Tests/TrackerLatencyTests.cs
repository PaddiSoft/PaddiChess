using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public class TrackerLatencyTests
{
    private static readonly BoardCalibration Geometry = new(40,40,440,490,false);
    private static BoardObservation Observe(XiangqiGame game, bool flipped=false) =>
        BoardObservation.Read(ExternalBoardTests.Render(game,flipped),Geometry with { RedAtTop=flipped });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void IncrementalRecognitionPreservesBothPliesAndRejectsWrongPendingMove(bool flipped)
    {
        var game=new XiangqiGame();var before=Observe(game,flipped);
        var skin=BoardSkin.Learn("synthetic",before,game);var tracker=new ExternalBoardTracker(before,game);
        Assert.True(game.TryMoveUci("h2e2",out _));Assert.True(game.TryMoveUci("h9g7",out _));
        var after=Observe(game,flipped);var expected=game.CurrentFen().Split(' ')[0];game.Undo();game.Undo();
        var read=tracker.RecognizeChanges(after,game,[skin]);
        Assert.NotNull(read);Assert.True(read.Confident,read.Problem);
        Assert.Equal(expected,read.Fen.Split(' ')[0]);
        var match=tracker.MatchRecognizedPosition(read,game,"h2e2",after);
        Assert.True(match.Recognized,match.Message);Assert.Equal(new[]{"h2e2","h9g7"},match.Moves);
        Assert.False(tracker.MatchRecognizedPosition(read,game,"b0c2",after).Recognized);
        // Alternating fallback paths must never narrow the cache to another pending move.
        Assert.False(tracker.Match(after,game).Recognized);
        Assert.Equal(match.Moves,tracker.Match(after,game,recoverMissedPair:true).Moves);
        Assert.Equal(match.Moves,tracker.Match(after,game,"h2e2").Moves);
        foreach(var move in match.Moves) Assert.True(game.TryMoveUci(move,out _));
        tracker.Accept(after,game);
        Assert.Empty(tracker.MatchRecognizedPosition(tracker.RecognizeChanges(after,game,[skin])!,game,frame:after).Moves);
        Assert.True(game.RedToMove);
    }

    [Fact]
    public void IncrementalRecognitionDoesNotReuseIdentitiesUnderAnObstruction()
    {
        var game=new XiangqiGame();var before=Observe(game);var skin=BoardSkin.Learn("synthetic",before,game);
        var tracker=new ExternalBoardTracker(before,game);
        var after=BoardObservation.Read(ExternalBoardTests.Render(game,obstruction:true),Geometry);
        var read=tracker.RecognizeChanges(after,game,[skin]);
        Assert.NotNull(read);Assert.False(read.Confident);
        Assert.False(tracker.MatchRecognizedPosition(read,game,frame:after).Recognized);
    }

    [Fact]
    public void CandidateHistoryRecordsFifthCheckAcrossCacheFallbacks()
    {
        var game=new XiangqiGame();game.LoadFen("3k5/9/2R6/9/9/4P4/9/9/9/4K4 w - - 0 1");
        foreach(var move in new[]{"c7d7","d9e9","d7e7","e9f9","e7f7","f9e9","f7e7","e9d9"})
            Assert.True(game.TryMoveUci(move,out _));
        var tracker=new ExternalBoardTracker(Observe(game),game);
        // Screenshot tracking validates geometry; the target adjudicates history.
        var noHistory=new XiangqiGame();noHistory.LoadFen(game.CurrentFen());
        Assert.True(noHistory.TryMoveUci("e7d7",out _));var observed=Observe(noHistory);
        var read=new SkinRecognition(noHistory.CurrentFen(),[],0,null);
        Assert.True(tracker.Match(observed,game).Recognized);
        Assert.True(tracker.Match(observed,game,recoverMissedPair:true).Recognized);
        Assert.True(tracker.MatchRecognizedPosition(read,game,"e7d7",observed).Recognized);
        Assert.True(tracker.MatchRecognizedPosition(read,game,frame:observed).Recognized);
        Assert.Equal(8,game.Ply);
    }

    [Fact]
    public void VectorGlyphDistanceKeepsTheOriginalTranslationMetric()
    {
        var rng=new Random(315);var game=new XiangqiGame();var view=Observe(game);
        foreach(var index in new[]{0,1,2,3,4,19,27,54,64,89})
        {
            var reference=view.Cells[index];var observed=(float[])reference.Clone();
            for(int i=0;i<observed.Length;i++) observed[i]=Math.Clamp(observed[i]+(float)(rng.NextDouble()-.5)*.08f,0,1);
            Assert.InRange(Math.Abs(ScalarDistance(reference,observed)-BoardSkin.GlyphDistance(reference,observed)),0,1e-6);
        }
    }

    private static double ScalarDistance(float[] reference,float[] observed)
    {
        var original=BoardObservation.Distance(reference,observed);if(original<.004)return original;
        double best=1;
        for(int sy=-3;sy<=3;sy++)for(int sx=-3;sx<=3;sx++)
        {
            double sum=0;int count=0;
            for(int y=4;y<20;y++)for(int x=4;x<20;x++)
            {
                if((x-11.5)*(x-11.5)+(y-11.5)*(y-11.5)>60)continue;
                int a=(y*24+x)*3,b=((y+sy)*24+x+sx)*3;
                sum+=Math.Abs(reference[a]-observed[b])+Math.Abs(reference[a+1]-observed[b+1])+Math.Abs(reference[a+2]-observed[b+2]);count+=3;
            }
            best=Math.Min(best,sum/count*.66+(Math.Abs(sx)+Math.Abs(sy))*.001);
        }
        return best;
    }

    [Theory]
    [InlineData(XiangqiGame.InitialFen,"h2e2","h9g7")]
    [InlineData("2bakabr1/9/2n1c1n2/p1p1p1p1p/9/2P3P2/P3P1c1P/C3C1N2/9/1NBAKABR1 w - - 0 1","h0h9","g7h9")]
    public void ChangedSquareScoringMatchesFullBoardForFastReplies(string fen,string first,string reply)
    {
        var game=new XiangqiGame();game.LoadFen(fen);var before=Observe(game);
        var empty=Enumerable.Range(0,90).Where(i=>game.Board[i/9,i%9]=='\0').ToArray();
        var tracker=new ExternalBoardTracker(before,game);
        Assert.True(game.TryMoveUci(first,out var move));Assert.True(game.TryMoveUci(reply,out var answer));
        var after=Observe(game);game.Undo();game.Undo();
        // Include small changing background pixels so unchanged intersections still contribute.
        for(int i=0;i<90;i++)for(int p=0;p<after.Cells[i].Length;p++) after.Cells[i][p]=Math.Min(1,after.Cells[i][p]+.0003f);
        var sources=Enumerable.Range(0,90).ToArray();
        foreach(var m in new[]{move,answer})
        { int from=m.From.Rank*9+m.From.File,to=m.To.Rank*9+m.To.File;sources[to]=sources[from];sources[from]=-1; }
        var distances=Enumerable.Range(0,90).Select(i=>sources[i]==i
            ? (empty.Contains(i)?BoardObservation.EmptyDistance(before.Cells[i],after.Cells[i]):BoardObservation.Distance(before.Cells[i],after.Cells[i]))
            : sources[i]<0?empty.Min(e=>BoardObservation.EmptyDistance(before.Cells[e],after.Cells[i]))
            : BoardObservation.Distance(before.Cells[sources[i]],after.Cells[i])).ToArray();
        var match=tracker.Match(after,game,first);
        Assert.True(match.Recognized,match.Message);Assert.Equal(new[]{first,reply},match.Moves);
        Assert.InRange(Math.Abs(distances.Average()+distances.Max()*.6-match.Error),0,1e-9);
    }

    [Fact]
    public void ResultRemainsCorrectAcrossRepetitionNavigationBranchAndNewGame()
    {
        var game=new XiangqiGame();
        foreach(var move in new[]{"b0c2","b9c7","c2b0","c7b9","b0c2","b9c7","c2b0","c7b9"})
        { Assert.Equal(GameResult.Ongoing,game.Result);Assert.True(game.TryMoveUci(move,out _)); }
        Assert.Equal(GameResult.Ongoing,game.Result);Assert.Equal(GameResult.Ongoing,game.Result);
        Assert.True(game.Undo());Assert.Equal(GameResult.Ongoing,game.Result);
        Assert.True(game.Redo());Assert.Equal(GameResult.Ongoing,game.Result);
        Assert.True(game.Undo());Assert.True(game.TryMoveUci("h9g7",out _));Assert.Equal(GameResult.Ongoing,game.Result);
        game.NewGame();Assert.Equal(GameResult.Ongoing,game.Result);
    }
}
