using Avalonia.Controls;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("partial")]
    public async Task InputOcclusionRecoversWithoutPhantomOrDuplicateMoves(string stage)
    {
        await Session.Dispatch(async () =>
        {
            var path=Path.Combine(Path.GetTempPath(),$"paddi-input-recovery-{Guid.NewGuid():N}.json");
            var oldPath=Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",path);
            var window=new MainWindow();var desktop=new FakeDesktop();
            try
            {
                window.Show();window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked=false;
                window.FindControl<Slider>("LevelSlider")!.Value=20;
                window.FindControl<NumericUpDown>("ThreadsBox")!.Value=1;
                window.FindControl<CheckBox>("DepthLimitCheck")!.IsChecked = true;
                window.FindControl<NumericUpDown>("DepthBox")!.Value=2;
                window.FindControl<NumericUpDown>("ThinkBox")!.Value=1;
                Set(window,"_externalDesktop",desktop); Set(window, "_positionRecognizer", new SyntheticPositionRecognizer());
                Set(window,"_externalFrame",await desktop.CaptureAsync(desktop.Target,default));
                Set(window,"_externalCalibration",new BoardCalibration(40,40,440,490,false));Set(window,"_externalPositionReady",true);
                desktop.BlockBeforeCount=stage=="before"?2:0;
                desktop.BlockAfterMoveOnce=stage=="after";
                desktop.BlockPartialOnce=stage=="partial";
                Click(window,"ExternalStartButton");
                if(stage=="partial")
                {
                    var waitUntil=DateTime.UtcNow.AddSeconds(10);
                    while(desktop.InputAttempts==0 && DateTime.UtcNow<waitUntil)await Task.Delay(30);
                    await Task.Delay(1000);
                    Assert.True(Get<bool>(window,"_externalRunning"));
                    Assert.Equal(1,desktop.InputAttempts);Assert.Equal(0,desktop.MovesSent);
                    var pending=Get<string>(window,"_externalPendingMove");Assert.NotNull(pending);
                    Click(window,"ExternalStopButton");await Get<Task>(window,"_externalTask");
                    await Task.Delay(300);Assert.Equal(1,desktop.InputAttempts);
                    Click(window,"ExternalStartButton");await Task.Delay(700);
                    Assert.Equal(1,desktop.InputAttempts); // Resume must not replay an uncertain source click.
                    desktop.CompletePartial(pending);
                }
                var deadline=DateTime.UtcNow.AddSeconds(20);
                while(Get<XiangqiGame>(window,"_game").Ply<4 && Get<bool>(window,"_externalRunning") && DateTime.UtcNow<deadline)await Task.Delay(40);
                Click(window,"ExternalStopButton");await Get<Task>(window,"_externalTask");
                Assert.Equal(4,Get<XiangqiGame>(window,"_game").Ply);
                Assert.Equal(2,desktop.MovesSent);
                Assert.Equal(desktop.Game.CurrentFen(),Get<XiangqiGame>(window,"_game").CurrentFen());
                Assert.True(Get<bool>(window,"_externalLinked"));
                // Stop may interrupt the next legitimate search/input after the fourth confirmed ply.
                if(Get<string?>(window,"_externalPendingMove") is { } next)
                    Assert.Contains(Get<XiangqiGame>(window,"_game").AllLegalMoves(),move=>move.Uci==next);
            }
            finally
            {
                Get<CancellationTokenSource?>(window,"_externalCancellation")?.Cancel();window.Close();await Get<Task>(window,"_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",oldPath);if(File.Exists(path))File.Delete(path);
            }
            return true;
        },default);
    }
}
