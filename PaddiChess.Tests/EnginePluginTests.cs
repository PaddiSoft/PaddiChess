using System.Reflection;
using System.Text.Json;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

[Collection("Desktop integration")]
public class EnginePluginTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    [Fact]
    public async Task ReleasedEngineRestoresSkillWhenRestarted()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new FakeEngine();
        await using var client = new PikafishClient { OverridePath = fixture.Executable };
        var settings = new EngineSettings(7, 1, 16, 1, 1);
        await client.PrepareAsync(settings, default);
        await client.ReleaseResourcesAsync();
        await client.PrepareAsync(settings, default);
        Assert.Equal(2, File.ReadAllLines(fixture.Log).Count(line => line == "setoption name Skill Level value 7"));
    }
    [Fact]
    public void LegacyEnginePathMigratesOnceWithoutReplacingAChosenDefault()
    {
        var preferences = new AppPreferences { EnginePath = "/engines/old-pikafish" };
        preferences.NormalizeEnginePlugins();
        var migrated = preferences.EnginePlugins.Single(plugin => !plugin.IsBundled);
        Assert.Equal(migrated.Id, preferences.DefaultEngineId);
        Assert.Equal("/engines/old-pikafish", migrated.ExecutablePath);
        var second = new EnginePlugin { Name = "第二版", ExecutablePath = "/engines/second" };
        preferences.EnginePlugins.Add(second); preferences.DefaultEngineId = second.Id;
        preferences.NormalizeEnginePlugins();
        Assert.Equal(second.Id, preferences.DefaultEngineId);
        Assert.Equal(3, preferences.EnginePlugins.Count);
        Assert.Equal(second.ExecutablePath, preferences.EnginePath);
    }
    [Fact]
    public void PluginManifestRoundTripResolvesPathsRelativeToManifest()
    {
        using var fixture = new FakeEngine();
        var plugin = new EnginePlugin { Name = "独立版本", ExecutablePath = fixture.Executable, EvalFilePath = fixture.Weights,
            RuleOptions = new() { ["Repetition Rule"] = "SkyRule", ["Sixty Move Rule"] = "false" } };
        var manifest = Path.Combine(fixture.Folder, "plugin.paddi-engine.json"); plugin.Export(manifest);
        Assert.DoesNotContain(fixture.Folder, File.ReadAllText(manifest));
        var loaded = EnginePlugin.Import(manifest);
        Assert.Equal(plugin.Name, loaded.Name);
        Assert.Equal(plugin.ExecutablePath, loaded.ExecutablePath);
        Assert.Equal(plugin.EvalFilePath, loaded.EvalFilePath);
        Assert.Equal(plugin.RuleOptions, loaded.RuleOptions);
        Assert.NotEqual(plugin.Id, loaded.Id);
    }
    [Fact]
    public void UciOptionsRetainNamesBoundsAndVariantValues()
    {
        var option = UciEngineOption.Parse("option name Example Option type spin default 20 min -5 max 255")!;
        Assert.Equal(("Example Option", "spin", "20", -5, 255), (option.Name, option.Type, option.DefaultValue, option.Minimum, option.Maximum));
        var variant = UciEngineOption.Parse("option name UCI_Variant type combo default chess var chess var xiangqi")!;
        Assert.Equal(new[] { "chess", "xiangqi" }, variant.Variants);
        Assert.Equal("own version.nnue", UciEngineOption.Parse("option name EvalFile type string default own version.nnue")!.DefaultValue);
    }
    [Fact]
    public async Task ExternalEngineUsesOwnWeightsAndAdvertisedLimits()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new FakeEngine();
        await using var client = new PikafishClient { OverridePath = fixture.Executable };
        var result = await client.SearchAsync(XiangqiGame.InitialFen, "",
            new EngineSettings(20, 32, 4096, 1, 1) { MultiPvOverride = 4 }, null, default);
        Assert.True(new XiangqiGame().TryMoveUci(result.BestMove, out _));
        Assert.Equal(fixture.Weights, client.ResolvedEvalPath);
        var log = File.ReadAllText(fixture.Log);
        Assert.Contains("setoption name Threads value 2", log);
        Assert.Contains("setoption name Hash value 32", log);
        Assert.Contains("setoption name MultiPV value 1", log);
        Assert.Contains("setoption name UCI_Variant value xiangqi", log);
        Assert.Contains("setoption name EvalFile value " + fixture.Weights, log);
        Assert.DoesNotContain(Path.Combine(AppContext.BaseDirectory, "Engine", "pikafish.nnue"), log);
    }
    [Fact]
    public async Task MinimalUciEngineIsNotSentPikafishOnlyOptionsOrWeights()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new FakeEngine(options: false);
        await using var client = new PikafishClient { OverridePath = fixture.Executable };
        var identity = await client.ProbeAsync(default);
        Assert.Equal("Fixture Xiangqi", identity.Name);
        Assert.Empty(identity.Options);
        Assert.Null(identity.EvalFile);
        Assert.DoesNotContain("setoption", File.ReadAllText(fixture.Log));
    }
    [Fact]
    public async Task PluginRulesAreAdvertisedValidatedAndAppliedBeforeSearching()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new FakeEngine(rules: true);
        var plugin = new EnginePlugin { ExecutablePath = fixture.Executable, RuleOptions = new()
        { ["Repetition Rule"] = "skyrule", ["Sixty Move Rule"] = "False", ["Rule60MaxPly"] = "130" } };
        await using var client = plugin.CreateClient();
        var identity = await client.ProbeAsync(default);
        Assert.Equal(3, identity.Options.Count(option => option.IsRuleOption));
        var log = File.ReadAllLines(fixture.Log);
        var search = Array.FindIndex(log, line => line.StartsWith("go "));
        foreach (var command in new[] { "setoption name Repetition Rule value SkyRule",
            "setoption name Sixty Move Rule value false", "setoption name Rule60MaxPly value 130" })
            Assert.InRange(Array.IndexOf(log, command), 0, search - 1);
    }
    [Theory]
    [InlineData("Repetition Rule", "UnknownRule")]
    [InlineData("Repetition Rule", "AsianRule\nquit")]
    [InlineData("Rule60MaxPly", "151")]
    [InlineData("Sixty Move Rule", "yes")]
    public async Task UnsupportedRuleValuesFailBeforeMoveSearch(string name, string value)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new FakeEngine(rules: true);
        await using var client = new PikafishClient { OverridePath = fixture.Executable,
            OverrideRuleOptions = new Dictionary<string, string> { [name] = value } };
        await Assert.ThrowsAsync<ArgumentException>(() => client.ProbeAsync(default));
        Assert.DoesNotContain("go ", File.ReadAllText(fixture.Log));
    }
    [Fact]
    public async Task RuleUiUsesDeclaredChoicesAndKeepsAnUnprobedManifest()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new FakeEngine(rules: true);
        await using var probe = new PikafishClient { OverridePath = fixture.Executable };
        var identity = await probe.ProbeAsync(default);
        await ExternalSessionTests.Session.Dispatch(async () =>
        {
            var oldPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", Path.Combine(fixture.Folder, "settings.json"));
            var plugin = new EnginePlugin { ExecutablePath = fixture.Executable, Name = "规则测试引擎",
                RuleOptions = new() { ["Repetition Rule"] = "SkyRule" } };
            new AppPreferences { AutoAnalyze = false, EnginePluginsMigrated = true,
                EnginePlugins = [plugin] }.Save();
            MainWindow? window = null;
            try
            {
                window = new MainWindow(); window.Show();
                window.FindControl<ListBox>("EnginePluginList")!.SelectedIndex = 1;
                var read = typeof(MainWindow).GetMethod("ReadEnginePluginEditor", Private)!;
                Assert.Equal("SkyRule", ((EnginePlugin)read.Invoke(window, [true])!).RuleOptions["Repetition Rule"]);
                typeof(MainWindow).GetMethod("RenderEngineRuleOptions", Private)!.Invoke(window,
                    [plugin, identity.Options, true]);
                var panel = window.FindControl<ItemsControl>("EngineRuleOptionsPanel")!;
                Assert.Equal(3, panel.ItemCount);
                window.UpdateLayout();
                var option = Assert.IsType<PaddiXiangqi.ViewModels.EngineRuleOptionViewModel>(panel.Items[0]);
                Assert.Equal(3, option.Choices.Length);
                Assert.Contains("SkyRule", option.Choice!.Value);
                option.Choice = option.Choices[2];
                var selected = (EnginePlugin)read.Invoke(window, [true])!;
                Assert.Equal("NoJudgement", selected.RuleOptions["Repetition Rule"]);
                await (Task)typeof(MainWindow).GetMethod("ActivateEnginePluginAsync", Private)!.Invoke(window, [selected])!;
                var saved = AppPreferences.Load().EnginePlugins.Single(p => p.Id == plugin.Id);
                Assert.Equal("NoJudgement", saved.RuleOptions["Repetition Rule"]);
                Assert.Equal(3, saved.DetectedRuleOptions.Count);
                window.FindControl<Button>("ConfigurationWorkspaceButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/ui"));
                Directory.CreateDirectory(folder);
                using var image = window.CaptureRenderedFrame(); image?.Save(Path.Combine(folder, "engine-rules.png"));
            }
            finally
            {
                if (window is not null) { window.Close(); await (Task)typeof(MainWindow).GetField("_cleanupTask", Private)!.GetValue(window)!; }
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", oldPath);
            }
            return true;
        }, default);
    }
    [Fact]
    public async Task ExplicitPluginWeightsAreKeptSeparateFromAutoDetectedWeights()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new FakeEngine();
        var chosen = Path.Combine(fixture.Folder, "selected.nnue"); File.WriteAllText(chosen, "selected weights");
        await using var client = new PikafishClient { OverridePath = fixture.Executable, OverrideEvalPath = chosen };
        Assert.Equal(chosen, (await client.ProbeAsync(default)).EvalFile);
        Assert.Contains("setoption name EvalFile value " + chosen, File.ReadAllText(fixture.Log));
    }
    [Fact]
    public async Task PluginDeclaringOnlyChessIsRejectedBeforeAnyMoveSearch()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new FakeEngine(xiangqi: false);
        await using var client = new PikafishClient { OverridePath = fixture.Executable };
        await Assert.ThrowsAsync<NotSupportedException>(() => client.ProbeAsync(default));
        Assert.DoesNotContain("go ", File.ReadAllText(fixture.Log));
    }
    [Fact]
    public async Task SelectingPluginDoesNotSwitchUntilDefaultIsAppliedAndPersistsWeights()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new FakeEngine();
        await ExternalSessionTests.Session.Dispatch(async () =>
        {
            var oldPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", Path.Combine(fixture.Folder, "settings.json"));
            MainWindow? window = null;
            try
            {
                var first = new EnginePlugin { Name = "版本甲", ExecutablePath = fixture.Executable, EvalFilePath = fixture.Weights };
                var second = new EnginePlugin { Name = "版本乙", ExecutablePath = fixture.Executable, EvalFilePath = fixture.Weights };
                new AppPreferences { AutoAnalyze = false, EnginePluginsMigrated = true, EnginePlugins = [first, second], DefaultEngineId = first.Id }.Save();
                window = new MainWindow(); window.Show();
                var list = window.FindControl<ListBox>("EnginePluginList")!;
                Assert.Equal(3, list.ItemCount);
                list.SelectedIndex = 2;
                Assert.Equal(first.Id, AppPreferences.Load().DefaultEngineId);
                Assert.Equal("当前引擎：版本甲", window.FindControl<TextBlock>("ActiveEngineText")!.Text);
                var activate = typeof(MainWindow).GetMethod("ActivateEnginePluginAsync", Private)!;
                await (Task)activate.Invoke(window, [list.SelectedItem])!;
                var saved = AppPreferences.Load();
                Assert.Equal(second.Id, saved.DefaultEngineId);
                Assert.Equal(second.EvalFilePath, saved.EnginePlugins.Single(plugin => plugin.Id == second.Id).EvalFilePath);
                var engine = (PikafishClient)typeof(MainWindow).GetField("_engine", Private)!.GetValue(window)!;
                Assert.Equal(second.EvalFilePath, engine.OverrideEvalPath);
                Assert.Equal("当前引擎：版本乙", window.FindControl<TextBlock>("ActiveEngineText")!.Text);
                await Assert.ThrowsAsync<FileNotFoundException>(() => (Task)activate.Invoke(window,
                    [new EnginePlugin { ExecutablePath = Path.Combine(fixture.Folder, "missing-engine") }])!);
                Assert.Equal(second.Id, AppPreferences.Load().DefaultEngineId);
                Assert.Same(engine, typeof(MainWindow).GetField("_engine", Private)!.GetValue(window));
            }
            finally
            {
                if (window is not null) { window.Close(); await (Task)typeof(MainWindow).GetField("_cleanupTask", Private)!.GetValue(window)!; }
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", oldPath);
            }
            return true;
        }, default);
    }
    [Theory]
    [InlineData(1200, 700)]
    [InlineData(1360, 880)]
    public async Task WorkspaceNavigationKeepsGameAndWidthsAndConfigurationControlsFit(int width, int height)
    {
        using var fixture = new FakeEngine();
        await ExternalSessionTests.Session.Dispatch(async () =>
        {
            var oldPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", Path.Combine(fixture.Folder, "layout-settings.json"));
            MainWindow? window = null;
            try
            {
                var service = new LlmServiceProfile { Name = "示例 API 服务", BaseUrl = "https://api.example.com/v1",
                    Models = ["example-chat", "example-reasoning", "example-reasoning-high"], EnabledModels = ["example-reasoning"] };
                new AppPreferences { AutoAnalyze = false, EnginePluginsMigrated = true,
                    EnginePlugins = [new EnginePlugin { Name = "自定义皮卡鱼版本", ExecutablePath = fixture.Executable, EvalFilePath = fixture.Weights }],
                    LlmProfilesMigrated = true, LlmServiceDirectoryMigrated = true,
                    LlmServices = [service], RedLlmServiceId = service.Id, RedLlmModel = "example-reasoning" }.Save();
                window = new MainWindow { Width = width, Height = height };
                typeof(MainWindow).GetField("_externalDesktop", Private)!.SetValue(window, new ExternalSessionTests.FakeDesktop());
                window.Show();
                var tabs = window.FindControl<TabControl>("MainTabs")!;
                var game = (XiangqiGame)typeof(MainWindow).GetField("_game", Private)!.GetValue(window)!;
                Assert.True(game.TryMoveUci("h2e2", out _));
                typeof(MainWindow).GetMethod("RefreshUi", Private)!.Invoke(window, [false]);
                var fen = game.CurrentFen();
                var grid = window.FindControl<Grid>("WorkspaceGrid")!;
                grid.ColumnDefinitions[2].Width = new GridLength(205);
                var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
                var folder = Path.Combine(root, "artifacts", "ui"); Directory.CreateDirectory(folder);
                void Capture(string name)
                {
                    window.UpdateLayout();
                    using var frame = window.CaptureRenderedFrame();
                    frame?.Save(Path.Combine(folder, $"{name}-{width}.png"));
                }
                void Click(string name) => window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Capture("play-workspace");
                var chart = window.FindControl<AdvantageChart>("Chart")!;
                var chartScores = Enumerable.Range(0, 101).ToDictionary(ply => ply, ply => (double)(ply % 9 * 12));
                chart.Scores = chartScores; chart.MaxPly = chart.CurrentPly = 100; chart.Refresh();
                using var originalChartFrame = window.CaptureRenderedFrame();
                using var originalChartBytes = new MemoryStream(); originalChartFrame!.Save(originalChartBytes);
                for (var i = 0; i < 20; i++) chart.Refresh();
                var allocated = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
                for (var i = 0; i < 1000; i++) chart.Refresh();
                timer.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                var perfFolder = Path.Combine(root, "artifacts", "performance"); Directory.CreateDirectory(perfFolder);
                File.WriteAllText(Path.Combine(perfFolder, $"unchanged-chart-{width}.json"), JsonSerializer.Serialize(new
                { width, height, points = 101, refreshes = 1000, milliseconds = timer.Elapsed.TotalMilliseconds, allocatedBytes = allocated }, new JsonSerializerOptions { WriteIndented = true }));
                chartScores[100] = -450; chart.Refresh();
                using var updatedChartFrame = window.CaptureRenderedFrame();
                using var updatedChartBytes = new MemoryStream(); updatedChartFrame!.Save(updatedChartBytes);
                Assert.False(originalChartBytes.ToArray().SequenceEqual(updatedChartBytes.ToArray()));
                typeof(MainWindow).GetMethod("RefreshUi", Private)!.Invoke(window, [false]);
                Click("ConfigurationWorkspaceButton");
                window.UpdateLayout();
                Assert.Equal(4, tabs.SelectedIndex);
                Assert.False(window.FindControl<Border>("BoardPane")!.IsVisible);
                Assert.False(window.FindControl<Border>("RecordPane")!.IsVisible);
                Assert.True(window.FindControl<TextBox>("EnginePathBox")!.Bounds.Width > 500);
                Assert.True(window.FindControl<Grid>("DetailsPane")!.Bounds.Width > width - 75);
                var apply = window.FindControl<Button>("EnginePluginDefaultButton")!;
                Assert.True(apply.Bounds.Height > 25);
                Assert.True(apply.TranslatePoint(new Point(0, apply.Bounds.Height), window)!.Value.Y <= window.ClientSize.Height);
                window.FindControl<ListBox>("EnginePluginList")!.SelectedIndex = 1;
                Capture("engine-plugins");
                Click("ModelServicesWorkspaceButton"); window.UpdateLayout();
                Assert.Equal(2, tabs.SelectedIndex);
                Assert.True(window.FindControl<TextBox>("LlmProfileBaseUrlBox")!.Bounds.Width >= 300);
                Assert.True(window.FindControl<TextBox>("LlmModelFilterBox")!.Bounds.Width > 500);
                Capture("model-services");
                Click("SettingsWorkspaceButton"); window.UpdateLayout();
                Assert.Equal(1, tabs.SelectedIndex);
                Assert.True(window.FindControl<NumericUpDown>("HashBox")!.Bounds.Width >= 150);
                Capture("search-settings");
                Click("PlayWorkspaceButton");
                Assert.Equal(0, tabs.SelectedIndex);
                Assert.Equal(new GridLength(205), grid.ColumnDefinitions[2].Width);
                Assert.Equal(fen, game.CurrentFen());
                Click("ExternalWorkspaceButton");
                Assert.Equal(3, tabs.SelectedIndex);
                Assert.Equal(2, Grid.GetColumn(window.FindControl<Grid>("DetailsPane")!));
                Assert.True(window.FindControl<Border>("RecordPane")!.IsVisible);
                Assert.Same(window.FindControl<Border>("RecordPane"),
                    window.FindControl<ContentControl>("ExternalReviewHost")!.Content);
                Assert.Equal(new GridLength(0), grid.ColumnDefinitions[3].Width);
                Assert.Equal(new GridLength(0), grid.ColumnDefinitions[4].Width);
                grid.ColumnDefinitions[0].Width = new GridLength(0.9, GridUnitType.Star);
                Capture("external-workspace");
                Click("ConfigurationWorkspaceButton");
                Assert.Equal(4, tabs.SelectedIndex);
                Click("ExternalWorkspaceButton");
                Assert.Equal(new GridLength(0.9, GridUnitType.Star), grid.ColumnDefinitions[0].Width);
                Click("PlayWorkspaceButton");
                Assert.Equal(new GridLength(205), grid.ColumnDefinitions[2].Width);
                Assert.Equal(4, Grid.GetColumn(window.FindControl<Grid>("DetailsPane")!));
                Assert.Equal(fen, game.CurrentFen());
                Assert.True(window.FindControl<Border>("RecordPane")!.IsVisible);
                Assert.Contains("炮", ((PaddiXiangqi.ViewModels.MoveHistoryViewModel)window.FindControl<MoveHistoryView>("MoveScroll")!.DataContext!).Rows[1].Red!.Label);
                typeof(MainWindow).GetField("_redLlm", Private)!.SetValue(window, true);
                typeof(MainWindow).GetMethod("RefreshUi", Private)!.Invoke(window, [false]);
                Assert.True(window.FindControl<Grid>("LocalModelPicker")!.IsVisible);
                Assert.True(window.FindControl<Border>("LlmThinkingPanel")!.IsVisible);
                Click("ConfigurationWorkspaceButton");
                Assert.False(window.FindControl<Border>("LlmThinkingPanel")!.IsVisible);
                Click("PlayWorkspaceButton");
                Assert.True(window.FindControl<Border>("LlmThinkingPanel")!.IsVisible);
                Click("ConfigurationWorkspaceButton");
                Click("NewRecordButton");
                Assert.Equal(0, tabs.SelectedIndex);
                Assert.True(window.FindControl<Border>("SetupPanel")!.IsVisible);
                Assert.True(window.FindControl<Border>("BoardPane")!.IsVisible);
                window.UpdateLayout();
            }
            finally
            {
                if (window is not null) { window.Close(); await (Task)typeof(MainWindow).GetField("_cleanupTask", Private)!.GetValue(window)!; }
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", oldPath);
            }
            return true;
        }, default);
    }
    private sealed class FakeEngine : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "paddi-plugin-test-" + Guid.NewGuid().ToString("N"));
        public string Executable => Path.Combine(Folder, "fixture-engine.py");
        public string Weights => Path.Combine(Folder, "own version.nnue");
        public string Log => Path.Combine(Folder, "uci.log");
        public FakeEngine(bool options = true, bool xiangqi = true, bool rules = false)
        {
            Directory.CreateDirectory(Folder); File.WriteAllText(Weights, "fixture own weights");
            File.WriteAllText(Executable, $$"""
                #!/usr/bin/env python3
                import sys
                with open({{JsonSerializer.Serialize(Log)}}, 'a', buffering=1) as log:
                    for value in sys.stdin:
                        command=value.strip(); log.write(command+'\n')
                        if command=='uci':
                            print('id name Fixture Xiangqi'); print('id author Regression Fixture')
                            if {{(options ? "True" : "False")}}:
                                print('option name Threads type spin default 1 min 1 max 2')
                                print('option name Hash type spin default 16 min 16 max 32')
                                print('option name MultiPV type spin default 1 min 1 max 1')
                                print('option name Skill Level type spin default 20 min 0 max 20')
                                print('option name EvalFile type string default own version.nnue')
                                print('option name UCI_Variant type combo default chess var chess{{(xiangqi ? " var xiangqi" : "")}}')
                            if {{(rules ? "True" : "False")}}:
                                print('option name Repetition Rule type combo default AsianRule var AsianRule var SkyRule var NoJudgement')
                                print('option name Sixty Move Rule type check default true')
                                print('option name Rule60MaxPly type spin default 120 min 1 max 150')
                            print('uciok',flush=True)
                        elif command=='isready': print('readyok',flush=True)
                        elif command.startswith('go '):
                            print('info depth 1 score cp 0 nodes 1 pv a3a4')
                            print('bestmove a3a4',flush=True)
                        elif command=='quit': break
                """);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public void Dispose() { try { Directory.Delete(Folder, true); } catch (IOException) { } }
    }
}
