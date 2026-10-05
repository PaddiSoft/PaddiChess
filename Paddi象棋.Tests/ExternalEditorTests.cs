using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    [Fact]
    public async Task PositionEditorRelocatesAndRecognizesInsideTheSameDialog()
    {
        await Session.Dispatch(async () =>
        {
            var path=Path.Combine(Path.GetTempPath(),$"paddi-inline-editor-{Guid.NewGuid():N}.json");
            var oldPath=Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",path);
            var window=new MainWindow();Task? editing=null;
            try
            {
                window.Show();window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked=false;
                var png=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","jj-rook-recapture.png"));
                var geometry=BoardLocator.Locate(png)!;
                var image=BoardObservation.Read(png,geometry);
                var frame=new ExternalFrame(new(500,500,"iPhone 镜像",0,0,image.Width,image.Height),png);
                var desktop=new EditorDesktop(frame);
                Set(window,"_externalDesktop",desktop);Set(window,"_externalFrame",frame);
                Set(window,"_externalCalibration",geometry with {Top=geometry.Top+(geometry.Bottom-geometry.Top)/9,Bottom=geometry.Bottom+(geometry.Bottom-geometry.Top)/9});
                editing=(Task)typeof(MainWindow).GetMethod("EditExternalPositionAsync",Private)!.Invoke(window,
                    new object?[]{"9/9/9/9/9/9/9/9/9/9 w - - 0 1","模拟定位错一行",null})!;
                var dialog=Assert.Single(window.OwnedWindows);
                T Control<T>(string name) where T:Control=>dialog.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
                var manual=Control<Button>("ExternalEditorManual");
                manual.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var calibration=Control<CalibrationView>("ExternalEditorCalibration");
                Assert.True(calibration.IsPicking);
                calibration.Set(geometry); // Same event as finishing the two corner clicks.
                var accept=Control<Button>("ExternalEditorAccept");
                var deadline=DateTime.UtcNow.AddSeconds(15);
                while(!accept.IsEnabled && DateTime.UtcNow<deadline)await Task.Delay(30);
                Assert.True(accept.IsEnabled,Control<TextBlock>("ExternalEditorStatus").Text);
                Assert.Equal(1,desktop.Captures);
                Assert.Same(dialog,Assert.Single(window.OwnedWindows)); // No second calibration dialog.
                Assert.Equal("2bakabn1/9/2n1c4/p1p1p1p1p/9/2P3P2/P3P1c1P/C3C1N2/9/1NBAKAB2 w - - 0 1",
                    PositionSetup.BuildFen(Control<BoardView>("ExternalEditorBoard").SetupBoard!,true));
                accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await editing;
                Assert.Empty(window.OwnedWindows);
                Assert.True(Get<bool>(window,"_externalPositionReady"));
                Assert.Equal(geometry,Get<BoardCalibration>(window,"_externalCalibration"));
                Assert.Equal(0,desktop.Moves);
            }
            finally
            {
                foreach(var dialog in window.OwnedWindows.ToArray())dialog.Close();
                if(editing!=null)await editing;
                window.Close();await Get<Task>(window,"_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",oldPath);if(File.Exists(path))File.Delete(path);
            }
            return true;
        },default);
    }

    [Fact]
    public async Task ClosingEditorDiscardsAnInFlightRefresh()
    {
        await Session.Dispatch(async () =>
        {
            var path=Path.Combine(Path.GetTempPath(),$"paddi-editor-cancel-{Guid.NewGuid():N}.json");
            var oldPath=Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",path);
            var window=new MainWindow();Task? editing=null;
            var delayed=new TaskCompletionSource<ExternalFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                window.Show();window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked=false;
                var png=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","jj-rook-recapture.png"));
                var geometry=BoardLocator.Locate(png)!;var image=BoardObservation.Read(png,geometry);
                var frame=new ExternalFrame(new(500,500,"iPhone 镜像",0,0,image.Width,image.Height),png);
                var desktop=new EditorDesktop(frame){DelayedCapture=delayed};
                Set(window,"_externalDesktop",desktop);Set(window,"_externalFrame",frame);Set(window,"_externalCalibration",geometry);
                editing=(Task)typeof(MainWindow).GetMethod("EditExternalPositionAsync",Private)!.Invoke(window,new object?[]{XiangqiGame.InitialFen,"取消识别测试",null})!;
                var dialog=Assert.Single(window.OwnedWindows);
                dialog.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="ExternalEditorRefresh").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1,desktop.Captures);
                dialog.Close();await editing;
                delayed.SetResult(frame);await Task.Delay(250);
                Assert.Equal(XiangqiGame.InitialFen,Get<XiangqiGame>(window,"_game").CurrentFen());
                Assert.Equal(geometry,Get<BoardCalibration>(window,"_externalCalibration"));Assert.Equal(0,desktop.Moves);
                Assert.Empty(window.OwnedWindows);
            }
            finally
            {
                delayed.TrySetCanceled();foreach(var dialog in window.OwnedWindows.ToArray())dialog.Close();if(editing!=null)await editing;
                window.Close();await Get<Task>(window,"_cleanupTask");Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",oldPath);
                if(File.Exists(path))File.Delete(path);
            }
            return true;
        },default);
    }

    private sealed class EditorDesktop(ExternalFrame frame) : IExternalDesktop
    {
        public int Captures; public int Moves;
        public TaskCompletionSource<ExternalFrame>? DelayedCapture;
        public bool EscapePressed()=>false;
        public Task<IReadOnlyList<ExternalWindow>> ListAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<ExternalWindow>>([frame.Window]);
        public Task<ExternalFrame> CaptureAsync(ExternalWindow window,CancellationToken ct){ct.ThrowIfCancellationRequested();Captures++;return DelayedCapture?.Task ?? Task.FromResult(frame);}
        public Task MoveAsync(ExternalWindow w,double a,double b,double c,double d,CancellationToken ct){Moves++;throw new InvalidOperationException("Editor must never send input");}
    }
}
