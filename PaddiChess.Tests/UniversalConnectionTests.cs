using Avalonia.Controls;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    [Fact]
    public async Task GenericConnectionPublishesDetectedOrientationAndLowerSideTogether()
    {
        await Session.Dispatch(async () =>
        {
            var folder=Path.Combine(Path.GetTempPath(),$"paddi-generic-connect-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            var old=Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",Path.Combine(folder,"settings.json"));
            var window=new MainWindow();
            try
            {
                window.Show();window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked=false;
                window.FindControl<CheckBox>("ExternalAutoSideCheck")!.IsChecked=true;
                window.FindControl<ComboBox>("ExternalOrientationBox")!.SelectedIndex=0;
                var bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","ocr-unseen-blue-green.png"));
                var frame=new ExternalFrame(new(555,555,"unknown blue-green client",0,0,620,680),bytes);
                typeof(MainWindow).GetField("_externalDesktop",Private)!.SetValue(window,null);
                Set(window,"_externalFrame",frame);
                Set(window,"_externalCalibration",new BoardCalibration(50,50,570,635,false));
                var read=typeof(MainWindow).GetMethod("RecognizeExternalFrameAsync",Private)!;
                Assert.True(await (Task<bool>)read.Invoke(window,[frame])!);
                Assert.Equal(XiangqiGame.InitialFen,Get<XiangqiGame>(window,"_game").CurrentFen());
                Assert.Equal(1,window.FindControl<ComboBox>("ExternalOrientationBox")!.SelectedIndex);
                Assert.Equal(1,window.FindControl<ComboBox>("ExternalSideBox")!.SelectedIndex);
                Assert.Equal(0,window.FindControl<ComboBox>("ExternalTurnBox")!.SelectedIndex);
                Assert.True(window.FindControl<BoardView>("Board")!.Flipped);
                Assert.NotNull(Get<BoardSkin>(window,"_externalSessionSkin"));
            }
            finally
            {
                window.Close();await Get<Task>(window,"_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",old);Directory.Delete(folder,true);
            }
            return true;
        },default);
    }
}
