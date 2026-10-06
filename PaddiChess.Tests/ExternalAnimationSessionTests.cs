using Avalonia.Controls;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LiveSessionNeverCommitsRookAnimationWaypoints(bool flipped)
    {
        await Session.Dispatch(async () =>
        {
            const string initial = "4k4/9/9/9/4P4/9/9/9/9/2R1K4 w - - 0 1";
            var path = Path.Combine(Path.GetTempPath(), $"paddi-animation-live-{Guid.NewGuid():N}.json");
            var old = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            var window = new MainWindow();
            var before = new XiangqiGame(); before.LoadFen(initial);
            byte[] Frame(string move)
            {
                var game = new XiangqiGame(); game.LoadFen(initial);
                Assert.True(game.TryMoveUci(move, out _)); return ExternalBoardTests.Render(game, flipped);
            }
            var desktop = new AnimationFramesDesktop(ExternalBoardTests.Render(before, flipped),
                [Frame("c0c2"), Frame("c0c3"), Frame("c0c4"), Frame("c0c8")]);
            try
            {
                window.Show(); ConfigureAnimationSession(window, desktop, new(40,40,440,490,flipped));
                window.FindControl<TextBox>("ExternalFenBox")!.Text = initial;
                Set(window, "_externalFrame", await desktop.CaptureAsync(desktop.Target, default));
                Set(window, "_externalPositionReady", true);
                Click(window, "ExternalStartButton");
                while (!Get<bool>(window, "_externalRunning")) await Task.Delay(10);
                desktop.Begin = true;
                var game = Get<XiangqiGame>(window, "_game");
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (game.Ply < 1 && DateTime.UtcNow < deadline && Get<bool>(window, "_externalRunning"))
                    await Task.Delay(10);
                Assert.Equal("c0c8", Assert.Single(game.History).Uci);
                Assert.False(game.RedToMove);
                Assert.True(desktop.AnimationCaptures >= 5); // Arrival observed in at least two captures.
                Click(window, "ExternalStopButton"); await Get<Task>(window, "_externalTask");
                Assert.Equal("c0c8", Assert.Single(game.History).Uci);
                Assert.True(Get<bool>(window, "_externalLinked"));
            }
            finally
            {
                Get<CancellationTokenSource?>(window,"_externalCancellation")?.Cancel();
                window.Close(); await Get<Task>(window,"_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",old); if(File.Exists(path))File.Delete(path);
            }
            return true;
        }, default);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public async Task LiveSessionAutomaticallyRepairsReportedRookEndpointWithoutManualResync(int number)
    {
        await Session.Dispatch(async () =>
        {
            var path=Path.Combine(Path.GetTempPath(),$"paddi-auto-correct-{Guid.NewGuid():N}.json");
            var old=Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",path);
            var window=new MainWindow();
            var sample=WeChatMidgameFixtures.All.Single(x=>x.Number==number);
            var (png,geometry)=sample.Read();
            var observed=BoardObservation.Read(png,geometry);
            var desktop=new AnimationFramesDesktop(png,[]) {Target=new(88,88,"real JJ fixture",0,0,observed.Width,observed.Height)};
            try
            {
                window.Show(); ConfigureAnimationSession(window,desktop,geometry);
                var game=Get<XiangqiGame>(window,"_game"); game.LoadFen(sample.BeforeFen);
                var baseline=BoardObservation.Read(sample.ReconstructBefore(png,geometry),geometry);
                var checkpoint=new ExternalMoveRecovery(game,baseline,[sample.PrematureMove],controlledRed:false);
                Assert.True(game.TryMoveUci(sample.PrematureMove,out _));
                // Simulate an already recorded bad animation endpoint from the reported version.
                // A synthetic wrong reference makes the discrepancy explicit; recovery must use
                // the confirmed checkpoint and the real screenshot, not overwrite from a guessed FEN.
                var wrongReference=BoardObservation.Read(ExternalBoardTests.Render(game,true),new(40,40,440,490,true));
                Set(window,"_externalTracker",new ExternalBoardTracker(wrongReference,game));
                Set(window,"_externalMoveRecovery",checkpoint); Set(window,"_externalLinked",true);
                Set(window,"_externalFrame",await desktop.CaptureAsync(desktop.Target,default));
                Set(window,"_externalPositionReady",true);
                window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex=1;
                Click(window,"ExternalStartButton");
                var deadline=DateTime.UtcNow.AddSeconds(15);
                while(game.History[0].Uci!=sample.Move && DateTime.UtcNow<deadline && Get<bool>(window,"_externalRunning")) await Task.Delay(20);
                Assert.Equal(sample.Move,Assert.Single(game.History).Uci);
                Assert.Equal(sample.ActualFen.Split(' ')[0],game.CurrentFen().Split(' ')[0]);
                Assert.False(game.RedToMove);
                Assert.Equal(1,window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
                Assert.True(Get<bool>(window,"_externalRunning"),window.FindControl<TextBlock>("ExternalStatusText")!.Text);
                Assert.Empty(window.OwnedWindows); // No editor, no resync click, no permission prompt.
                Click(window,"ExternalStopButton");await Get<Task>(window,"_externalTask");
                Assert.Equal(sample.Move,Assert.Single(game.History).Uci);
            }
            finally
            {
                Get<CancellationTokenSource?>(window,"_externalCancellation")?.Cancel();
                window.Close();await Get<Task>(window,"_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",old);if(File.Exists(path))File.Delete(path);
            }
            return true;
        },default);
    }

    private static void ConfigureAnimationSession(MainWindow window,IExternalDesktop desktop,BoardCalibration geometry)
    {
        window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked=false;
        window.FindControl<Slider>("LevelSlider")!.Value=20;
        window.FindControl<NumericUpDown>("ThreadsBox")!.Value=1;
        window.FindControl<CheckBox>("DepthLimitCheck")!.IsChecked = true;
        window.FindControl<NumericUpDown>("DepthBox")!.Value=2;
        window.FindControl<NumericUpDown>("ThinkBox")!.Value=1;
        window.FindControl<ComboBox>("ExternalSideBox")!.SelectedIndex=1;
        window.FindControl<ComboBox>("ExternalOrientationBox")!.SelectedIndex=geometry.RedAtTop?1:0;
        Set(window,"_externalDesktop",desktop); Set(window, "_positionRecognizer", new SyntheticPositionRecognizer());Set(window,"_externalCalibration",geometry);
    }

    private sealed class AnimationFramesDesktop(byte[] initial,IReadOnlyList<byte[]> frames):IExternalDesktop
    {
        public ExternalWindow Target {get;init;}=new(88,88,"animation board",0,0,480,530);
        public bool Begin {get;set;}
        public int AnimationCaptures {get;private set;}
        public bool EscapePressed()=>false;
        public Task<IReadOnlyList<ExternalWindow>> ListAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<ExternalWindow>>([Target]);
        public async Task<ExternalFrame> CaptureAsync(ExternalWindow target,CancellationToken ct)
        {
            await Task.Delay(20,ct);
            return new(Target,Begin&&frames.Count>0?frames[Math.Min(AnimationCaptures++,frames.Count-1)]:initial);
        }
        public Task MoveAsync(ExternalWindow target,double fx,double fy,double tx,double ty,CancellationToken ct)=>Task.Delay(Timeout.Infinite,ct);
    }
}
