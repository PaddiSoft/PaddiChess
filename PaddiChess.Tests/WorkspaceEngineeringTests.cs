using System.Net;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PaddiXiangqi.Engine;
using PaddiXiangqi.Services;
using PaddiXiangqi.ViewModels;

namespace PaddiXiangqi.Tests;

[Collection("Desktop integration")]
public sealed class WorkspaceEngineeringTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void LargeModelDirectoryFilteringPreservesRowsAndEnabledSelections()
    {
        var changes = new List<(string, bool)>();
        var catalog = new ModelCatalogViewModel();
        catalog.Replace(Enumerable.Range(0, 5000).Select(i => $"model-{i:D4}"), ["model-4999"],
            (name, enabled) => changes.Add((name, enabled)));
        var selected = catalog.Items[^1];
        catalog.Filter = "4999";
        Assert.Same(selected, Assert.Single(catalog.Items));
        selected.Enabled = false;
        catalog.Filter = "";
        Assert.Equal(5000, catalog.Items.Count);
        Assert.Same(selected, catalog.Items[^1]);
        Assert.False(catalog.Items[^1].Enabled);
        Assert.Equal(("model-4999", false), Assert.Single(changes));
        Assert.Contains("已启用 0", catalog.Summary);
        catalog.Filter = "no-such-model";
        Assert.True(catalog.IsEmpty);
        Assert.Equal("没有匹配的模型。", catalog.EmptyText);
    }

    [Fact]
    public void RuleFormKeepsCanonicalVariantsAndDoesNotRoundFractionalInput()
    {
        var rule = new UciEngineOption("Repetition Rule", "combo", "AsianRule", null, null, ["AsianRule", "SkyRule"]);
        var form = new EngineRuleOptionViewModel(rule, "asianrule", value => value);
        Assert.Equal("AsianRule", form.ReadValue());
        var spin = new EngineRuleOptionViewModel(new("Rule60MaxPly", "spin", "120", 0, 1000, []), "120", value => value);
        spin.Number = 120.5m;
        Assert.Throws<ArgumentException>(() => spin.ReadValue());
    }

    [Fact]
    public async Task IndependentPagesKeepAutomationNamesActionsAndMaintenanceState()
    {
        await WithWindow(async window =>
        {
            var services = window.FindControl<ModelServicesView>("ModelServicesPage")!;
            var settings = window.FindControl<SettingsView>("SettingsPage")!;
            var plugins = window.FindControl<EnginePluginsView>("EnginePluginsPage")!;
            Assert.Same(services.Control<TextBox>("LlmProfileBaseUrlBox"), window.FindControl<TextBox>("LlmProfileBaseUrlBox"));
            Assert.Same(plugins.Control<TextBox>("EnginePathBox"), window.FindControl<TextBox>("EnginePathBox"));
            Assert.Same(settings.Control<NumericUpDown>("HashBox"), window.FindControl<NumericUpDown>("HashBox"));
            Assert.Same(window.FindControl<Grid>("ExternalConfigPanel"), settings.ExternalConfigurationSource);
            var profilePicker = window.FindControl<ComboBox>("LlmProfileList")!;
            var before = profilePicker.ItemCount;
            window.FindControl<Button>("LlmProfileAddButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(before + 1, profilePicker.ItemCount);
            Assert.Equal("API 服务 2", window.FindControl<TextBox>("LlmProfileNameBox")!.Text);
            await Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObsoleteModelDirectoryResponseCannotPopulateAnotherConnection(bool switchProfile)
    {
        await WithWindow(async window =>
        {
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var http = new HttpClient(new DelayedHandler(requested, response));
            typeof(MainWindow).GetField("_llmClient", Private)!.SetValue(window, new LlmChessClient(http));
            window.FindControl<TextBox>("LlmProfileBaseUrlBox")!.Text = "https://first.example.test/v1";
            window.FindControl<TextBox>("LlmProfileApiKeyBox")!.Text = "test-only-placeholder";
            var fetch = (Task)typeof(MainWindow).GetMethod("FetchLlmProfileModelsAsync", Private)!.Invoke(window, null)!;
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (switchProfile)
                window.FindControl<Button>("LlmProfileAddButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else
            {
                window.FindControl<TextBox>("LlmProfileBaseUrlBox")!.Text = "https://second.example.test/v1";
                typeof(MainWindow).GetMethod("SaveEditingLlmProfile", Private)!.Invoke(window, null);
            }
            response.SetResult(new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"obsolete-model\"}]}") });
            await fetch.WaitAsync(TimeSpan.FromSeconds(5));
            var preferences = (AppPreferences)typeof(MainWindow).GetField("_preferences", Private)!.GetValue(window)!;
            Assert.All(preferences.LlmServices, profile => Assert.DoesNotContain("obsolete-model", profile.Models));
            Assert.True(window.FindControl<Button>("LlmProfileFetchButton")!.IsEnabled);
            Assert.Empty(window.FindControl<ListBox>("LlmProfileModelsPanel")!.Items);
        });
    }

    private static async Task WithWindow(Func<MainWindow, Task> action)
    {
        await ExternalSessionTests.Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-workspace-{Guid.NewGuid():N}.json");
            var previous = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            new AppPreferences { AutoAnalyze = false }.Save();
            MainWindow? window = null;
            try { window = new MainWindow(); await action(window); }
            finally
            {
                if (window is not null)
                {
                    window.Close();
                    await (Task)typeof(MainWindow).GetField("_cleanupTask", Private)!.GetValue(window)!;
                }
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", previous);
                if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, CancellationToken.None);
    }

    private sealed class DelayedHandler(TaskCompletionSource requested, TaskCompletionSource<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requested.TrySetResult();
            return response.Task;
        }
    }
}
