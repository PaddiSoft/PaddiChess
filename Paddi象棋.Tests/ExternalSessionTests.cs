using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

[Collection("Desktop integration")]
public partial class ExternalSessionTests
{
    internal static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(ExternalTestApplication));
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(object target, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(target,value);
    private static T Get<T>(object target, string name) => (T)typeof(MainWindow).GetField(name,Private)!.GetValue(target)!;
    private static void Click(MainWindow window, string name) => window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public async Task RealEngineSynchronizesConfirmedExternalMovesAndStopsWithoutExtraClicks()
    {
        await Session.Dispatch(async () =>
        {
            var settingsPath=Path.Combine(Path.GetTempPath(),$"paddi-session-test-{Guid.NewGuid():N}.json");
            var oldPath=Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",settingsPath);
            var window=new MainWindow();
            var fake=new FakeDesktop();
            try
            {
                window.Show();
                window.FindControl<Slider>("LevelSlider")!.Value=20;
                window.FindControl<NumericUpDown>("ThreadsBox")!.Value=1;
                window.FindControl<CheckBox>("DepthLimitCheck")!.IsChecked = true;
                window.FindControl<NumericUpDown>("DepthBox")!.Value=3;
                window.FindControl<NumericUpDown>("ThinkBox")!.Value=1;
                window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked=false;
                Set(window,"_externalDesktop",fake);
                Set(window,"_externalFrame",await fake.CaptureAsync(fake.Target,CancellationToken.None));
                Set(window,"_externalCalibration",new BoardCalibration(40,40,440,490,false));
                Set(window,"_externalPositionReady",true);
                var localEngine = Get<PikafishClient>(window, "_engine");
                await localEngine.PrepareAsync(new EngineSettings(20, 1, 16, 1, 3), default);
                Click(window,"ExternalStartButton");
                var deadline=DateTime.UtcNow.AddSeconds(20);
                while(Get<XiangqiGame>(window,"_game").Ply<4 && DateTime.UtcNow<deadline && Get<bool>(window,"_externalRunning"))
                    await Task.Delay(50);
                Click(window,"ExternalStopButton");
                await Get<Task>(window,"_externalTask");
                var game=Get<XiangqiGame>(window,"_game");
                Assert.True(game.Ply>=4,window.FindControl<TextBlock>("ExternalStatusText")!.Text);
                Assert.Equal(fake.Game.CurrentFen(),game.CurrentFen());
                var clicks=fake.MovesSent;
                await Task.Delay(600);
                Assert.Equal(clicks,fake.MovesSent);
                Assert.False(Get<bool>(window,"_externalRunning"));
                Assert.True(Get<bool>(window,"_externalLinked"));
                Assert.Null(typeof(PikafishClient).GetField("_process", Private)!.GetValue(localEngine));
                var warmEngine = Get<PikafishClient>(window, "_externalPlayingEngine");
                Assert.Equal(1, warmEngine.ProcessStartCount);
                // The paused board may be reviewed, then resume returns to the synchronized latest ply.
                typeof(MainWindow).GetMethod("NavigateToPly",Private)!.Invoke(window,[0]);
                Assert.Equal(0,game.Ply);
                Click(window,"ExternalStartButton");
                await Task.Delay(100);
                Click(window,"ExternalStopButton");
                await Get<Task>(window,"_externalTask");
                Assert.Same(warmEngine, Get<PikafishClient>(window, "_externalPlayingEngine"));
                Assert.Equal(1, warmEngine.ProcessStartCount);
                Assert.Equal(game.TotalPly,game.Ply);
                Click(window,"ExternalDisconnectButton");
                Assert.False(Get<bool>(window,"_externalLinked"));
                Assert.True(window.FindControl<Button>("NewButton")!.IsEnabled);
            }
            finally
            {
                Get<CancellationTokenSource?>(window,"_externalCancellation")?.Cancel();
                window.Close();
                await Get<Task>(window,"_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",oldPath);
                if(File.Exists(settingsPath))File.Delete(settingsPath);
            }
            return true;
        },CancellationToken.None);
    }
    [Fact]
    public async Task RecalibrationActuallySynchronizesMiddleGameAndArchivesPreviousRecord()
    {
        await Session.Dispatch(async () =>
        {
            var folder=Path.Combine(Path.GetTempPath(), $"paddi-resync-{Guid.NewGuid():N}"); Directory.CreateDirectory(folder);
            var oldPath=Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",Path.Combine(folder,"settings.json"));
            var window=new MainWindow();
            try
            {
                window.Show(); window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked=false;
                var bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets","BoardSkins","jj-classic.png"));
                Set(window,"_externalFrame",new ExternalFrame(new(50,50,"iPhone 镜像",0,0,604,660),bytes));
                Set(window,"_externalCalibration",BuiltInBoardSkins.JjReferenceGeometry);
                var recognize=typeof(MainWindow).GetMethod("RecognizeExternalFrameAsync",Private)!;
                await (Task)recognize.Invoke(window,[Get<ExternalFrame>(window,"_externalFrame")])!;
                var game=Get<XiangqiGame>(window,"_game");
                Assert.Equal(BuiltInBoardSkins.JjReferenceFen,game.CurrentFen());
                Assert.Equal(game.CurrentFen(),window.FindControl<TextBox>("ExternalFenBox")!.Text);
                Assert.True(Get<bool>(window,"_externalPositionReady"));
                Assert.Null(window.FindControl<CheckBox>("ExternalConfirmedCheck"));
                // Re-sync must replace the visible board, with the old move retained in a recovery record.
                Assert.True(game.TryMoveUci("e3e4",out _));
                await (Task)recognize.Invoke(window,[Get<ExternalFrame>(window,"_externalFrame")])!;
                Assert.Equal(BuiltInBoardSkins.JjReferenceFen,game.CurrentFen());
                var records=Directory.GetFiles(Path.Combine(folder,"Recovery"),"*.paddi.json"); Assert.Single(records);
                var saved=System.Text.Json.JsonSerializer.Deserialize<GameRecord>(File.ReadAllText(records[0]))!;
                Assert.Equal("e3e4",Assert.Single(saved.Moves));
            }
            finally
            {
                window.Close(); await Get<Task>(window,"_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",oldPath); Directory.Delete(folder,true);
            }
            return true;
        },CancellationToken.None);
    }
    [Fact]
    public async Task PermissionsGateBlocksActionsAndKeepsCalibrationAcrossGrantAndRevocation()
    {
        await Session.Dispatch(async () =>
        {
            var path=Path.Combine(Path.GetTempPath(),$"paddi-permission-{Guid.NewGuid():N}.json");
            var previousPath=Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",path);
            var window=new MainWindow(); var desktop=new FakeDesktop {Permissions=new(false,false)};
            try
            {
                window.Show();Set(window,"_externalDesktop",desktop);
                window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked=false;
                window.FindControl<TabControl>("MainTabs")!.SelectedIndex=3;
                Assert.True(window.FindControl<Border>("ExternalPermissionGate")!.IsVisible);
                Assert.False(window.FindControl<Grid>("ExternalWorkspacePanel")!.IsVisible);
                Click(window,"ExternalStartButton");Click(window,"ExternalRefreshButton");
                Assert.Equal(0,desktop.Captures);Assert.Equal(0,desktop.MovesSent);Assert.Equal(0,desktop.PermissionPrompts);
                Click(window,"ExternalScreenPermissionButton"); Assert.Equal(1,desktop.PermissionPrompts);
                desktop.Permissions=new(true,true);Click(window,"ExternalPermissionRetryButton");
                Assert.True(window.FindControl<Grid>("ExternalWorkspacePanel")!.IsVisible);
                var frame=await desktop.CaptureAsync(desktop.Target,default);
                Set(window,"_externalFrame",frame);Set(window,"_externalCalibration",new BoardCalibration(40,40,440,490,false));
                Set(window,"_externalPositionReady",true);
                window.FindControl<Slider>("LevelSlider")!.Value=20;
                window.FindControl<NumericUpDown>("ThreadsBox")!.Value=1;
                window.FindControl<CheckBox>("DepthLimitCheck")!.IsChecked = true;
                window.FindControl<NumericUpDown>("DepthBox")!.Value=2;
                window.FindControl<NumericUpDown>("ThinkBox")!.Value=1;
                desktop.DenyInputOnce=true;Click(window,"ExternalStartButton");
                await Get<Task>(window,"_externalTask").WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(0,desktop.MovesSent);Assert.Null(Get<string?>(window,"_externalPendingMove"));
                Assert.True(window.FindControl<Border>("ExternalPermissionGate")!.IsVisible);
                Assert.Same(frame,Get<ExternalFrame>(window,"_externalFrame"));
                desktop.Permissions=new(true,true);Click(window,"ExternalPermissionRetryButton");Click(window,"ExternalStartButton");
                var deadline=DateTime.UtcNow.AddSeconds(15);
                while(Get<XiangqiGame>(window,"_game").Ply<4 && Get<bool>(window,"_externalRunning") && DateTime.UtcNow<deadline)await Task.Delay(40);
                Click(window,"ExternalStopButton");await Get<Task>(window,"_externalTask");
                Assert.Equal(4,Get<XiangqiGame>(window,"_game").Ply);
                Assert.Equal(1,desktop.PermissionPrompts); // Never auto-request again when resuming.
            }
            finally
            {
                Get<CancellationTokenSource?>(window,"_externalCancellation")?.Cancel();window.Close();await Get<Task>(window,"_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",previousPath);if(File.Exists(path))File.Delete(path);
            }
            return true;
        },default);
    }
    [Fact]
    public async Task ResyncingUnchangedSelectedBoardKeepsHistoryAndNeedsNoCheckbox()
    {
        await Session.Dispatch(async () =>
        {
            var folder=Path.Combine(Path.GetTempPath(),$"paddi-noop-resync-{Guid.NewGuid():N}");Directory.CreateDirectory(folder);
            var oldPath=Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",Path.Combine(folder,"settings.json"));
            var window=new MainWindow();
            try
            {
                window.Show();window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked=false;
                var game=Get<XiangqiGame>(window,"_game");
                game.LoadFen("2bakabr1/9/2n1c1n2/p1p1p1p1p/9/2P3P2/P3P1c1P/C3C1N2/9/1NBAKABR1 w - - 0 1");
                Assert.True(game.TryMoveUci("h0h9",out _));Assert.True(game.TryMoveUci("g7h9",out _));
                var png=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","jj-rook-recapture.png"));
                var geometry=BoardLocator.Locate(png)!;
                var observation=BoardObservation.Read(png,geometry);
                var frame=new ExternalFrame(new(50,50,"iPhone 镜像",0,0,observation.Width,observation.Height),png);
                Set(window,"_externalFrame",frame);Set(window,"_externalCalibration",geometry);
                Set(window,"_externalTracker",new ExternalBoardTracker(observation,game));Set(window,"_externalLinked",true);
                var fen=game.CurrentFen();
                var recognize=typeof(MainWindow).GetMethod("RecognizeExternalFrameAsync",Private)!;
                Assert.True(await (Task<bool>)recognize.Invoke(window,[frame])!);
                Assert.Equal(fen,game.CurrentFen());Assert.Equal(2,game.TotalPly);
                Assert.True(Get<bool>(window,"_externalLinked"));
                Assert.True(Get<bool>(window,"_externalPositionReady"));
                Assert.Null(window.FindControl<CheckBox>("ExternalConfirmedCheck"));
                var pending=game.AllLegalMoves()[0].Uci;
                Set(window,"_externalPendingMove",pending);
                await (Task)typeof(MainWindow).GetMethod("ApplyExternalPositionAsync",Private)!.Invoke(window,[fen, CancellationToken.None])!;
                Assert.Equal(2,game.TotalPly);Assert.Equal(pending,Get<string>(window,"_externalPendingMove"));
                Assert.False(Directory.Exists(Path.Combine(folder,"Recovery")));
            }
            finally
            {
                window.Close();await Get<Task>(window,"_cleanupTask");Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",oldPath);
                Directory.Delete(folder,true);
            }
            return true;
        },default);
    }
    [Theory]
    [InlineData("obstruction", 4300)]
    [InlineData("animation", 11000)]
    [InlineData("delayed_ack", 8500)]
    [InlineData("capture_failure", 1200)]
    public async Task TemporaryVisualOrCaptureProblemsRecoverWithoutResyncOrDuplicateInput(string fault, int waitMs)
    {
        await Session.Dispatch(async () =>
        {
            var path=Path.Combine(Path.GetTempPath(),$"paddi-recovery-{Guid.NewGuid():N}.json");
            var oldPath=Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",path);
            var window=new MainWindow();var desktop=new FakeDesktop();
            try
            {
                window.Show();window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked=false;
                window.FindControl<Slider>("LevelSlider")!.Value=20;
                window.FindControl<NumericUpDown>("ThreadsBox")!.Value=1;
                window.FindControl<CheckBox>("DepthLimitCheck")!.IsChecked = true;
                window.FindControl<NumericUpDown>("DepthBox")!.Value=2;
                window.FindControl<NumericUpDown>("ThinkBox")!.Value=1;
                Set(window,"_externalDesktop",desktop);
                Set(window,"_externalFrame",await desktop.CaptureAsync(desktop.Target,default));
                Set(window,"_externalCalibration",new BoardCalibration(40,40,440,490,false));
                Set(window,"_externalPositionReady",true);
                desktop.Obstruct=fault=="obstruction";desktop.Animate=fault=="animation";
                desktop.HideMoves=fault=="delayed_ack";
                if(fault=="capture_failure")desktop.CaptureFailures=4;
                Click(window,"ExternalStartButton");
                await Task.Delay(waitMs);
                Assert.True(Get<bool>(window,"_externalRunning"),window.FindControl<TextBlock>("ExternalStatusText")!.Text);
                Assert.True(Get<bool>(window,"_externalLinked"));
                Assert.Equal(fault=="delayed_ack"?1:0,desktop.MovesSent);
                Assert.Equal(0,Get<XiangqiGame>(window,"_game").Ply);
                desktop.Obstruct=desktop.Animate=desktop.HideMoves=false;desktop.CaptureFailures=0;
                var deadline=DateTime.UtcNow.AddSeconds(15);
                while(Get<XiangqiGame>(window,"_game").Ply<4 && Get<bool>(window,"_externalRunning") && DateTime.UtcNow<deadline)await Task.Delay(40);
                Click(window,"ExternalStopButton");await Get<Task>(window,"_externalTask");
                Assert.Equal(4,Get<XiangqiGame>(window,"_game").Ply);
                Assert.Equal(2,desktop.MovesSent); // Only two intended submissions; no repeat after an uncertain acknowledgement.
                Assert.Equal(desktop.Game.CurrentFen(),Get<XiangqiGame>(window,"_game").CurrentFen());
                Assert.Equal(0,desktop.PermissionPrompts);
            }
            finally
            {
                Get<CancellationTokenSource?>(window,"_externalCancellation")?.Cancel();window.Close();await Get<Task>(window,"_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH",oldPath);if(File.Exists(path))File.Delete(path);
            }
            return true;
        },default);
    }
    internal sealed class FakeDesktop : IExternalDesktop
    {
        public ExternalWindow Target {get;}=new(100,100,"independent board",0,0,480,530);
        public XiangqiGame Game {get;}=new();
        public int MovesSent {get;private set;}
        public int Captures {get;private set;}
        public bool DenyInputOnce {get;set;}
        public int BlockBeforeCount {get;set;}
        public bool BlockAfterMoveOnce {get;set;}
        public bool BlockPartialOnce {get;set;}
        public int InputAttempts {get;private set;}
        public void CompletePartial(string uci)
        {
            Assert.True(Game.TryMoveUci(uci,out _));MovesSent++;
            if(Game.Result==GameResult.Ongoing)Assert.True(Game.TryMoveUci(Game.AllLegalMoves()[0].Uci,out _));
        }
        public bool Obstruct {get;set;}
        public bool Animate {get;set;}
        public bool HideMoves {get;set;}
        public int CaptureFailures {get;set;}
        private byte[]? _initialPng;
        public ExternalPermissions Permissions {get;set;}=new(true,true);
        public int PermissionPrompts {get;private set;}
        public Task<ExternalPermissions> GetPermissionsAsync(CancellationToken ct)=>Task.FromResult(Permissions);
        public Task RequestPermissionAsync(ExternalPermission permission,CancellationToken ct) {PermissionPrompts++;return Task.CompletedTask;}
        public bool EscapePressed()=>false;
        public Task<IReadOnlyList<ExternalWindow>> ListAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<ExternalWindow>>([Target]);
        public Task<ExternalFrame> CaptureAsync(ExternalWindow target,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();Captures++;
            if(CaptureFailures>0){CaptureFailures--;throw new IOException("temporary screenshot outage");}
            var png=ExternalBoardTests.Render(Game,obstruction: Obstruct || (Animate && Captures%2==0));
            _initialPng??=png;
            return Task.FromResult(new ExternalFrame(Target,HideMoves?_initialPng:png));
        }
        public async Task MoveAsync(ExternalWindow target,double fx,double fy,double tx,double ty,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            InputAttempts++;
            if(BlockBeforeCount>0){BlockBeforeCount--;throw new ExternalInputBlockedException(false,"temporary input overlay");}
            if(BlockPartialOnce){BlockPartialOnce=false;throw new ExternalInputBlockedException(true,"overlay after source click");}
            if (DenyInputOnce) {DenyInputOnce=false;Permissions=new(true,false);throw new ExternalPermissionException(ExternalPermission.Accessibility);}
            if (MovesSent >= 2) await Task.Delay(Timeout.Infinite,ct);
            var from=new Square((int)Math.Round((fx-40)/50),(int)Math.Round((fy-40)/50));
            var to=new Square((int)Math.Round((tx-40)/50),(int)Math.Round((ty-40)/50));
            Assert.True(Game.TryMove(from,to,out _)); MovesSent++;
            // Reply immediately to ensure the state machine handles missed intermediate screenshots.
            if(Game.Result==GameResult.Ongoing)Assert.True(Game.TryMoveUci(Game.AllLegalMoves()[0].Uci,out _));
            if(BlockAfterMoveOnce){BlockAfterMoveOnce=false;throw new ExternalInputBlockedException(true,"overlay after input");}
        }
    }
}
public static class ExternalTestApplication
{
    public static AppBuilder BuildAvaloniaApp()=>AppBuilder.Configure<App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).WithInterFont();
}

[CollectionDefinition("Desktop integration", DisableParallelization = true)]
public class DesktopIntegrationCollection { }
