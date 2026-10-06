using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PaddiXiangqi.Core;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

[Collection("Desktop integration")]
public class ExternalWorkspaceLayoutTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(1200, 700)]
    [InlineData(1360, 880)]
    public async Task RecordAnalysisAndInputStatusStayTogetherWhenConfigurationScrolls(int width, int height)
    {
        await ExternalSessionTests.Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-unified-external-{Guid.NewGuid():N}.json");
            var previousPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            var service = new LlmServiceProfile { Name = "演示服务", BaseUrl = "https://api.example.invalid/v1",
                Models = ["demo-thinking-model"], EnabledModels = ["demo-thinking-model"] };
            new AppPreferences { AutoAnalyze = false, LlmProfilesMigrated = true, LlmServiceDirectoryMigrated = true,
                LlmServices = [service], RedLlmServiceId = service.Id, BlackLlmServiceId = service.Id,
                RedLlmModel = "demo-thinking-model", BlackLlmModel = "demo-thinking-model" }.Save();
            MainWindow? window = null;
            try
            {
                window = new MainWindow { Width = width, Height = height };
                var desktop = new ExternalSessionTests.FakeDesktop();
                Set(window, "_externalDesktop", desktop);
                var game = Get<XiangqiGame>(window, "_game");
                foreach (var move in new[] { "h2e2", "h9g7", "b0c2", "b9c7" })
                {
                    Assert.True(game.TryMoveUci(move, out _));
                    Assert.True(desktop.Game.TryMoveUci(move, out _));
                }
                window.Show();
                Click(window, "ExternalWorkspaceButton");
                await (Task<bool>)typeof(MainWindow).GetMethod("EnsureExternalPermissionsAsync", Private)!.Invoke(window, null)!;
                window.FindControl<ComboBox>("RedActiveModelList")!.SelectedIndex = 0;
                window.FindControl<ComboBox>("RedLlmReasoningBox")!.SelectedIndex = 3;
                window.FindControl<ComboBox>("ExternalControllerBox")!.SelectedIndex = 1;
                if (OperatingSystem.IsMacOS()) window.FindControl<ComboBox>("ExternalDeliveryBox")!.SelectedIndex = 1;
                window.FindControl<ComboBox>("ExternalWindowBox")!.ItemsSource = new[] { desktop.Target };
                window.FindControl<ComboBox>("ExternalWindowBox")!.SelectedIndex = 0;
                Set(window, "_externalFrame", await desktop.CaptureAsync(desktop.Target, default));
                Set(window, "_externalCalibration", new PaddiXiangqi.External.BoardCalibration(40, 40, 440, 490, false));
                Set(window, "_externalLinked", true);
                Set(window, "_externalTurnKnown", true);
                Set(window, "_externalPositionReady", true);
                Set(window, "_externalObserving", true);
                Refresh(window);
                window.FindControl<TextBlock>("LlmThinkingText")!.Text = string.Join('\n',
                    Enumerable.Range(1, 40).Select(i => $"第 {i} 手 · 保留模型说明与合法着法"));
                window.FindControl<TextBlock>("ScoreText")!.Text = "红 +123 分";
                window.FindControl<TextBlock>("DepthText")!.Text = "22";
                window.FindControl<TextBlock>("NodesText")!.Text = "1,280,000";
                window.FindControl<TextBlock>("NpsText")!.Text = "1,600,000";
                window.FindControl<TextBlock>("ExternalStatusText")!.Text =
                    "已连接 · 持续同步并准备着法，尚未发送输入。当前目标窗口处于后台，落子是否接收取决于目标应用。";
                window.UpdateLayout();

                var record = window.FindControl<Border>("RecordPane")!;
                var moves = window.FindControl<MoveHistoryView>("MoveScroll")!;
                var analysis = window.FindControl<ScrollViewer>("AnalysisScroll")!;
                var thinking = window.FindControl<Border>("LlmThinkingPanel")!;
                var setup = window.FindControl<ScrollViewer>("ExternalConfigurationScroll")!;
                var live = window.FindControl<StackPanel>("ExternalLiveControls")!;
                var operation = window.FindControl<Grid>("ExternalOperationPanel")!;
                Assert.True(record.IsVisible);
                Assert.False(window.FindControl<Border>("RecordEditorPanel")!.IsVisible);
                Assert.True(window.FindControl<Grid>("ExternalRecordActions")!.IsVisible);
                Assert.Same(analysis, window.FindControl<ContentControl>("ExternalAnalysisHost")!.Content);
                Assert.Contains(record, analysis.GetVisualAncestors());
                Assert.Contains(window.FindControl<Grid>("ExternalWorkspacePanel")!, thinking.GetVisualAncestors());
                Capture(window, $"unified-external-{width}");
                Assert.True(moves.Bounds.Height >= 110, $"实时棋谱视口只有 {moves.Bounds.Height}px。" +
                    $"棋谱块 {window.FindControl<Grid>("RecordContentPanel")!.Bounds.Height}px，" +
                    $"分析 {analysis.Bounds.Height}px，思考 {thinking.Bounds.Height}px，" +
                    $"中栏 {record.Bounds.Height}px，窗口 {window.ClientSize}");
                Assert.True(analysis.Bounds.Height >= 150, $"实时分析视口只有 {analysis.Bounds.Height}px。");
                AssertContained(moves, window);
                AssertContained(analysis, window);
                AssertContained(thinking, window);
                Assert.True(InWindow(moves, window).Right <= InWindow(analysis, window).Left,
                    "棋谱与分析应并排显示，分析不应压缩成另一条窄纵列。");
                Assert.True(InWindow(operation, window).Bottom <= InWindow(moves, window).Top);
                Assert.True(InWindow(analysis, window).Bottom <= InWindow(thinking, window).Top);
                Assert.Equal("4 手", window.FindControl<TextBlock>("MoveCountText")!.Text);
                Assert.NotNull(window.FindControl<ComboBox>("ExternalModelBox")!.SelectedItem);
                Assert.Equal(3, window.FindControl<ComboBox>("ExternalEffortBox")!.SelectedIndex);
                Assert.Equal(4, ((PaddiXiangqi.ViewModels.MoveHistoryViewModel)window.FindControl<MoveHistoryView>("MoveScroll")!.DataContext!).Moves.Count - 1);
                Assert.Empty(window.GetVisualDescendants().OfType<Expander>());

                string[] persistent = ["ExternalHistoryButton", "ExternalWindowBox", "ExternalConnectButton", "ExternalTurnBox",
                    "ExternalAutoTurnCheck", "ExternalSideBox", "ExternalControllerBox", "ExternalModelBox",
                    "ExternalEffortBox", "ExternalDeliveryBox", "ExternalInputBox", "ExternalOrientationBox",
                    "ExternalSpeedBox", "ExternalCalibrateButton", "ExternalStartButton", "ExternalStopButton",
                    "ExternalResyncButton", "ExternalScoreModeBox", "ExternalAnalysisCheck",
                    "ExternalMetricsText", "ExternalStatusText"];
                var before = persistent.ToDictionary(name => name,
                    name => InWindow(window.FindControl<Control>(name)!, window));
                foreach (var name in persistent)
                {
                    var control = window.FindControl<Control>(name)!;
                    Assert.DoesNotContain(setup, control.GetVisualAncestors());
                    AssertContained(control, window);
                }
                AssertContained(live, window);
                Assert.Null(window.FindControl<CheckBox>("ExternalPrimeWindowCheck"));
                Assert.False(window.FindControl<Grid>("BoardNavigation")!.IsVisible);
                AssertContained(operation, window);
                Assert.True(InWindow(live, window).Bottom <= InWindow(moves, window).Top);
                Assert.DoesNotContain(setup, window.FindControl<Button>("ExternalCalibrateButton")!.GetVisualAncestors());
                var reviewGrid = window.FindControl<Grid>("RecordSplitPanel")!;
                var reviewColumns = reviewGrid.ColumnDefinitions;
                var controlRows = window.FindControl<Grid>("ExternalOptionsGrid")!.RowDefinitions;
                reviewColumns[0].Width = new GridLength(.7, GridUnitType.Star);
                for (int i = 0; i < 5; i++) Refresh(window);
                Assert.Same(reviewColumns, reviewGrid.ColumnDefinitions);
                Assert.Same(controlRows, window.FindControl<Grid>("ExternalOptionsGrid")!.RowDefinitions);
                Assert.Equal(new GridLength(.7, GridUnitType.Star), reviewColumns[0].Width);
                Assert.True(window.FindControl<ScrollViewer>("AnalysisScroll")!.Bounds.Width >= 270);
                Assert.True(window.FindControl<Border>("BoardPane")!.Bounds.Width >= 500);
                foreach (var pair in new[] {
                    ("ExternalControllerBox", "ExternalModelBox"), ("ExternalModelBox", "ExternalEffortBox"),
                    ("ExternalSpeedBox", "ExternalScoreModeBox"), ("ExternalOrientationBox", "ExternalCalibrateButton"),
                    ("ExternalCalibrateButton", "ExternalAnalysisCheck"), ("ExternalInputBox", "ExternalResyncButton") })
                {
                    var left = InWindow(window.FindControl<Control>(pair.Item1)!, window);
                    var right = InWindow(window.FindControl<Control>(pair.Item2)!, window);
                    Assert.True(left.Right <= right.Left && Math.Abs(left.Top - right.Top) <= 6,
                        $"{pair.Item1} 与 {pair.Item2} 应在同一行相邻显示：{left} / {right}");
                }
                setup.Offset = new Vector(0, setup.Extent.Height);
                window.UpdateLayout();
                foreach (var name in persistent)
                    Assert.Equal(before[name], InWindow(window.FindControl<Control>(name)!, window));
                Assert.Equal("红 +123 分", window.FindControl<TextBlock>("ScoreText")!.Text);
                Assert.Equal(3, window.FindControl<TabControl>("MainTabs")!.SelectedIndex);
                Capture(window, $"unified-external-{width}");

                // Switching pages moves the existing views, preserving score, history and
                // their scroll state rather than recreating or launching another analysis.
                for (var repeat = 0; repeat < 3; repeat++)
                {
                    Click(window, "PlayWorkspaceButton"); window.UpdateLayout();
                    Assert.Same(analysis, window.FindControl<TabItem>("AnalysisTab")!.Content);
                    Assert.Contains(window.FindControl<Grid>("DetailsPane")!, thinking.GetVisualAncestors());
                    Assert.Null(window.FindControl<ContentControl>("ExternalAnalysisHost")!.Content);
                    Assert.True(window.FindControl<Border>("RecordEditorPanel")!.IsVisible);
                    Click(window, "ExternalWorkspaceButton"); window.UpdateLayout();
                    Assert.Same(analysis, window.FindControl<ContentControl>("ExternalAnalysisHost")!.Content);
                    Assert.Equal("红 +123 分", window.FindControl<TextBlock>("ScoreText")!.Text);
                    Assert.True(moves.Bounds.Height >= 110 && analysis.Bounds.Height >= 150);
                }

                // The armed UI must leave recovery, stop and scoring usable even though
                // the controller settings are disabled. This does not start an input worker.
                Set(window, "_externalRunning", true);
                typeof(MainWindow).GetMethod("RefreshExternalControls", Private)!.Invoke(window, null);
                foreach (var name in new[] { "ExternalResyncButton", "ExternalStopButton", "ExternalScoreModeBox", "ExternalAnalysisCheck" })
                    Assert.True(window.FindControl<Control>(name)!.IsEffectivelyEnabled,
                        $"接管时 {name} 不能继承已禁用配置组的状态。");
                foreach (var name in new[] { "ExternalControllerBox", "ExternalModelBox", "ExternalSideBox" })
                    Assert.False(window.FindControl<Control>(name)!.IsEffectivelyEnabled);
                Set(window, "_externalRunning", false);
            }
            finally
            {
                if (window is not null)
                {
                    Set(window, "_externalRunning", false);
                    Set(window, "_externalObserving", false);
                    Set(window, "_externalLinked", false);
                    window.Close();
                    await Get<Task>(window, "_cleanupTask");
                }
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", previousPath);
                if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, CancellationToken.None);
    }

    private static void Set(MainWindow window, string name, object value) =>
        typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static T Get<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Refresh(MainWindow window) =>
        typeof(MainWindow).GetMethod("RefreshUi", Private)!.Invoke(window, [false]);
    private static void Click(MainWindow window, string name) =>
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Rect InWindow(Control control, Window window) =>
        new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    private static void AssertContained(Control control, Window window)
    {
        var bounds = InWindow(control, window);
        Assert.True(bounds.Top >= 0 && bounds.Bottom <= window.ClientSize.Height + 1,
            $"{control.Name} 超出窗口高度：{bounds}");
        Assert.True(bounds.Left >= 0 && bounds.Right <= window.ClientSize.Width + 1,
            $"{control.Name} 超出窗口宽度：{bounds}");
    }
    private static void Capture(MainWindow window, string name)
    {
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/ui"));
        Directory.CreateDirectory(folder);
        using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(folder, name + ".png"));
    }
}
