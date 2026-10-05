using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

[Collection("Desktop integration")]
public class FlatWorkspaceLayoutTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly (string Name, int Index)[] Pages =
    [
        ("PlayWorkspaceButton", 0), ("ExternalWorkspaceButton", 3),
        ("SettingsWorkspaceButton", 1), ("ConfigurationWorkspaceButton", 4),
        ("ModelServicesWorkspaceButton", 2)
    ];

    [Theory]
    [InlineData(1200, 700)]
    [InlineData(1360, 880)]
    public async Task AllPagesHaveDirectNavigationAndExpandedControlsRemainUsable(int width, int height)
    {
        await ExternalSessionTests.Session.Dispatch(async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), $"paddi-flat-layout-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            var previousPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", Path.Combine(folder, "settings.json"));
            var service = new LlmServiceProfile { Name = "布局测试", BaseUrl = "https://api.example.com/v1",
                Models = ["example-model"], EnabledModels = ["example-model"] };
            new AppPreferences { AutoAnalyze = false, LlmProfilesMigrated = true,
                LlmServiceDirectoryMigrated = true, LlmServices = [service],
                RedLlmServiceId = service.Id, BlackLlmServiceId = service.Id,
                RedLlmModel = "example-model", BlackLlmModel = "example-model" }.Save();
            MainWindow? window = null;
            try
            {
                window = new MainWindow { Width = width, Height = height };
                typeof(MainWindow).GetField("_externalDesktop", Private)!.SetValue(window, new ExternalSessionTests.FakeDesktop());
                window.Show(); window.UpdateLayout();
                var tabs = window.FindControl<TabControl>("MainTabs")!;
                var navigation = Pages.Select(page => window.FindControl<Button>(page.Name)!).ToArray();
                for (var i = 0; i < navigation.Length; i++)
                {
                    var bounds = InWindow(navigation[i], window);
                    Assert.True(bounds.Width > 35 && bounds.Height >= 25);
                    AssertContained(bounds, window, navigation[i].Name!);
                    if (i > 0) Assert.True(InWindow(navigation[i - 1], window).Right <= bounds.Left,
                        "一级导航按钮不得互相覆盖。");
                }
                Assert.True(InWindow(navigation[^1], window).Right <= InWindow(window.FindControl<Button>("NewButton")!, window).Left);

                foreach (var page in Pages)
                {
                    Click(window, page.Name); window.UpdateLayout();
                    Assert.Equal(page.Index, tabs.SelectedIndex);
                    Assert.Contains("active", window.FindControl<Button>(page.Name)!.Classes);
                    Assert.Empty(window.GetVisualDescendants().OfType<Expander>());
                    Assert.True(tabs.Bounds.Height >= 150, $"{page.Name} 内容没有可用高度。");
                }

                var tests = window.FindControl<Grid>("LlmSideTestsPanel")!;
                var modelScroll = window.FindControl<ScrollViewer>("ModelServicesScroll")!;
                modelScroll.Offset = new Vector(0, modelScroll.Extent.Height);
                window.UpdateLayout();
                var red = window.FindControl<Button>("RedLlmTestButton")!;
                var black = window.FindControl<Button>("BlackLlmTestButton")!;
                Assert.Contains(tests, red.GetVisualAncestors());
                Assert.Contains(tests, black.GetVisualAncestors());
                var redBounds = InWindow(red, window); var blackBounds = InWindow(black, window);
                AssertContained(redBounds, window, red.Name!);
                AssertContained(blackBounds, window, black.Name!);
                Assert.True(redBounds.Right <= blackBounds.Left);
                Assert.True(Math.Abs(redBounds.Top - blackBounds.Top) < 1,
                    "红黑模型测试应并排呈现，不应切换子页才能访问。");
                Capture(window, $"flat-model-tests-{width}");

                Click(window, "PlayWorkspaceButton"); window.UpdateLayout();
                var moveScroll = window.FindControl<MoveHistoryView>("MoveScroll")!;
                var editor = window.FindControl<Border>("RecordEditorPanel")!;
                Assert.True(moveScroll.Bounds.Height >= 150, "展开注释后仍应保留可用棋谱视口。");
                AssertContained(InWindow(editor, window), window, editor.Name!);
                Assert.True(InWindow(moveScroll, window).Bottom <= InWindow(editor, window).Top);
                Assert.True(window.FindControl<TextBox>("AnnotationBox")!.Bounds.Height >= 50);
                Assert.True(window.FindControl<TextBox>("RecordTitleBox")!.Bounds.Width >= 140);

                typeof(MainWindow).GetField("_redLlm", Private)!.SetValue(window, true);
                typeof(MainWindow).GetField("_blackLlm", Private)!.SetValue(window, true);
                typeof(MainWindow).GetField("_ready", Private)!.SetValue(window, false);
                window.FindControl<CheckBox>("RedLlmCheck")!.IsChecked = true;
                window.FindControl<CheckBox>("BlackLlmCheck")!.IsChecked = true;
                typeof(MainWindow).GetField("_ready", Private)!.SetValue(window, true);
                typeof(MainWindow).GetField("_enginePaused", Private)!.SetValue(window, true);
                typeof(MainWindow).GetMethod("RefreshUi", Private)!.Invoke(window, [false]);
                window.FindControl<TextBlock>("LlmThinkingText")!.Text = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"第 {i} 手 · 模型摘要与合法着法确认"));
                window.UpdateLayout();
                var thought = window.FindControl<ScrollViewer>("ThinkingScroll")!;
                var analysis = window.FindControl<ScrollViewer>("AnalysisScroll")!;
                Assert.True(window.FindControl<Grid>("LocalModelPicker")!.IsVisible);
                Assert.True(window.FindControl<Border>("LlmThinkingPanel")!.IsVisible);
                Capture(window, $"flat-play-models-{width}");
                Assert.True(analysis.Bounds.Height >= 100, $"双方模型与思考历史挤压了评分视口：{analysis.Bounds.Height}；" +
                    $"Status={window.FindControl<Border>("LocalStatusCard")!.Bounds.Height}, " +
                    $"Thinking={window.FindControl<Border>("LlmThinkingPanel")!.Bounds.Height}, " +
                    $"Details={window.FindControl<Grid>("DetailsPane")!.Bounds.Height}, Tabs={tabs.Bounds.Height}");
                Assert.True(thought.Bounds.Height <= (height < 800 ? 80 : 170) + 1);
                AssertContained(InWindow(analysis, window), window, analysis.Name!);

                Click(window, "ExternalWorkspaceButton");
                await (Task<bool>)typeof(MainWindow).GetMethod("EnsureExternalPermissionsAsync", Private)!.Invoke(window, null)!;
                window.UpdateLayout();
                var turn = window.FindControl<ComboBox>("ExternalTurnBox")!;
                var start = window.FindControl<Button>("ExternalStartButton")!;
                var stop = window.FindControl<Button>("ExternalStopButton")!;
                AssertContained(InWindow(turn, window), window, turn.Name!);
                AssertContained(InWindow(start, window), window, start.Name!);
                AssertContained(InWindow(stop, window), window, stop.Name!);
                Assert.Contains(window.FindControl<StackPanel>("ExternalLiveControls")!, turn.GetVisualAncestors());
                Assert.Contains(window.FindControl<StackPanel>("ExternalLiveControls")!, start.GetVisualAncestors());
                var connect = window.FindControl<Button>("ExternalConnectButton")!;
                Assert.True(InWindow(connect, window).Right <= InWindow(start, window).Left);
                Assert.True(InWindow(stop, window).Bottom <= InWindow(start, window).Top);
                Assert.True(window.FindControl<Border>("RecordPane")!.IsVisible);
                Assert.Same(analysis, window.FindControl<ContentControl>("ExternalAnalysisHost")!.Content);
                AssertContained(InWindow(analysis, window), window, analysis.Name!);
                Assert.True(analysis.Bounds.Height >= 150);
                Capture(window, $"flat-external-{width}");
            }
            finally
            {
                if (window is not null)
                {
                    window.Close();
                    await (Task)typeof(MainWindow).GetField("_cleanupTask", Private)!.GetValue(window)!;
                }
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", previousPath);
                Directory.Delete(folder, true);
            }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ExpandingWindowRestoresThoughtHistorySpaceWithoutChangingSelectedPage()
    {
        await ExternalSessionTests.Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-density-{Guid.NewGuid():N}.json");
            var previousPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            new AppPreferences { AutoAnalyze = false }.Save();
            MainWindow? window = null;
            try
            {
                window = new MainWindow { Width = 1200, Height = 700 };
                window.Show(); window.UpdateLayout();
                var thought = window.FindControl<ScrollViewer>("ThinkingScroll")!;
                Assert.Equal(80, thought.MaxHeight);
                window.Height = 880;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                Assert.True(window.ClientSize.Height >= 800, $"窗口变更尚未到达客户端：{window.ClientSize}");
                Assert.Equal(170, thought.MaxHeight);
                Assert.Equal(0, window.FindControl<TabControl>("MainTabs")!.SelectedIndex);
            }
            finally
            {
                if (window is not null)
                {
                    window.Close();
                    await (Task)typeof(MainWindow).GetField("_cleanupTask", Private)!.GetValue(window)!;
                }
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", previousPath);
                if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task NewModelSelectionsAreAccessibleBeforeEnablingAndMissingKeyShowsVisibleStatus()
    {
        await ExternalSessionTests.Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-first-model-{Guid.NewGuid():N}.json");
            var previousPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            var service = new LlmServiceProfile { Name = "新服务", BaseUrl = "https://api.example.com/v1",
                Models = ["new-model"], EnabledModels = ["new-model"] };
            new AppPreferences { AutoAnalyze = false, LlmProfilesMigrated = true,
                LlmServiceDirectoryMigrated = true, LlmServices = [service] }.Save();
            MainWindow? window = null;
            try
            {
                window = new MainWindow { Width = 1200, Height = 700 };
                window.Show(); window.UpdateLayout();
                var picker = window.FindControl<Grid>("LocalModelPicker")!;
                var red = window.FindControl<ComboBox>("RedActiveModelList")!;
                var black = window.FindControl<ComboBox>("BlackActiveModelList")!;
                var effort = window.FindControl<ComboBox>("RedLlmReasoningBox")!;
                Assert.True(picker.IsVisible);
                Assert.Null(red.SelectedItem); Assert.Null(black.SelectedItem);
                AssertContained(InWindow(red, window), window, red.Name!);
                AssertContained(InWindow(black, window), window, black.Name!);
                Assert.True(red.Bounds.Width >= 120 && black.Bounds.Width >= 120);
                red.SelectedIndex = 0; black.SelectedIndex = 0; effort.SelectedIndex = 3;
                window.FindControl<CheckBox>("RedLlmCheck")!.IsChecked = true;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.Equal(2, window.FindControl<TabControl>("MainTabs")!.SelectedIndex);
                Assert.False(window.FindControl<CheckBox>("RedLlmCheck")!.IsChecked);
                Assert.Equal(3, effort.SelectedIndex);
                var status = window.FindControl<TextBlock>("RedLlmConfigStatusText")!;
                Assert.Contains("API Key", status.Text);
                AssertContained(InWindow(status, window), window, status.Name!);
                Capture(window, "flat-first-model-missing-key-1200");
                Click(window, "PlayWorkspaceButton"); window.UpdateLayout();
                Assert.Equal(0, red.SelectedIndex); Assert.Equal(0, black.SelectedIndex);
                Assert.Equal(3, effort.SelectedIndex);
                Assert.True(picker.IsVisible);
            }
            finally
            {
                if (window is not null)
                {
                    window.Close();
                    await (Task)typeof(MainWindow).GetField("_cleanupTask", Private)!.GetValue(window)!;
                }
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", previousPath);
                if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, CancellationToken.None);
    }

    private static void Click(MainWindow window, string name) =>
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Rect InWindow(Control control, Window window) =>
        new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    private static void AssertContained(Rect bounds, Window window, string name)
    {
        Assert.True(bounds.Top >= 0 && bounds.Bottom <= window.ClientSize.Height + 1,
            $"{name} 超出窗口高度：{bounds}");
        Assert.True(bounds.Left >= 0 && bounds.Right <= window.ClientSize.Width + 1,
            $"{name} 超出窗口宽度：{bounds}");
    }
    private static void Capture(MainWindow window, string name)
    {
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/ui"));
        Directory.CreateDirectory(folder);
        using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(folder, name + ".png"));
    }
}
