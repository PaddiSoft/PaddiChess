using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PaddiXiangqi.Engine;
using PaddiXiangqi.Services;
using PaddiXiangqi.ViewModels;

namespace PaddiXiangqi.Tests;

[Collection("Desktop integration")]
public class BundledEngineRuleUiTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task BundledRulesCanBeEditedSavedAndReloadedWithoutReplacingTheExternalDefault()
    {
        await ExternalSessionTests.Session.Dispatch(async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), $"paddi-bundled-rule-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            var previousPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", Path.Combine(folder, "settings.json"));
            var external = new EnginePlugin
            {
                Name = "保留的外部引擎", ExecutablePath = Path.Combine(folder, "external-engine"),
                EvalFilePath = Path.Combine(folder, "external.nnue"),
                RuleOptions = new() { ["Repetition Rule"] = "ChineseRule" }
            };
            File.WriteAllText(external.ExecutablePath, "UI fixture; never executed");
            File.WriteAllText(external.EvalFilePath, "UI fixture weights");
            new AppPreferences { AutoAnalyze = false, EnginePluginsMigrated = true,
                EnginePlugins = [EnginePlugin.Bundled(), external], DefaultEngineId = external.Id }.Save();
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                window.Show();
                var engine = (PikafishClient)typeof(MainWindow).GetField("_engine", Private)!.GetValue(window)!;
                var list = window.FindControl<ListBox>("EnginePluginList")!;
                list.SelectedItem = list.Items.OfType<EnginePlugin>().Single(plugin => plugin.IsBundled);
                Assert.True(window.FindControl<Button>("EnginePluginSaveButton")!.IsEffectivelyEnabled);
                Assert.False(window.FindControl<TextBox>("EnginePathBox")!.IsEffectivelyEnabled);
                Assert.False(window.FindControl<TextBox>("EngineEvalBox")!.IsEffectivelyEnabled);
                var panel = window.FindControl<ItemsControl>("EngineRuleOptionsPanel")!;
                Assert.True(panel.IsEffectivelyEnabled);
                var editors = panel.Items.OfType<EngineRuleOptionViewModel>().ToDictionary(editor => editor.Option.Name);
                var repetition = editors["Repetition Rule"];
                repetition.Choice = repetition.Choices.Single(choice => choice.Value == "SkyRule");
                editors["Sixty Move Rule"].Enabled = false;
                editors["Rule60MaxPly"].Number = 130;

                var edited = (EnginePlugin)typeof(MainWindow).GetMethod("ReadEnginePluginEditor", Private)!.Invoke(window, [true])!;
                Assert.True(edited.IsBundled);
                Assert.Equal("SkyRule", edited.RuleOptions["Repetition Rule"]);
                Assert.Equal("false", edited.RuleOptions["Sixty Move Rule"]);
                Assert.Equal("130", edited.RuleOptions["Rule60MaxPly"]);
                Assert.Empty(edited.ExecutablePath);
                Assert.Empty(edited.EvalFilePath);

                window.FindControl<Button>("EnginePluginSaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await ((PreferencesWriter)typeof(MainWindow).GetField("_settingsWriter", Private)!.GetValue(window)!).FlushAsync();
                var saved = AppPreferences.Load();
                var bundled = saved.EnginePlugins.Single(plugin => plugin.IsBundled);
                Assert.Equal(edited.RuleOptions, bundled.RuleOptions);
                Assert.Equal(external.Id, saved.DefaultEngineId);
                var savedExternal = saved.EnginePlugins.Single(plugin => plugin.Id == external.Id);
                Assert.Equal(external.ExecutablePath, savedExternal.ExecutablePath);
                Assert.Equal(external.EvalFilePath, savedExternal.EvalFilePath);
                Assert.Equal(external.RuleOptions, savedExternal.RuleOptions);
                Assert.Same(engine, typeof(MainWindow).GetField("_engine", Private)!.GetValue(window));
                Assert.Equal(external.ExecutablePath, engine.OverridePath);
                Assert.Equal(external.EvalFilePath, engine.OverrideEvalPath);
                Assert.Equal(external.RuleOptions, engine.OverrideRuleOptions);

                list.SelectedItem = list.Items.OfType<EnginePlugin>().Single(plugin => plugin.Id == external.Id);
                list.SelectedItem = list.Items.OfType<EnginePlugin>().Single(plugin => plugin.IsBundled);
                var reloaded = panel.Items.OfType<EngineRuleOptionViewModel>().ToDictionary(editor => editor.Option.Name);
                Assert.Equal("SkyRule", reloaded["Repetition Rule"].Choice!.Value);
                Assert.False(reloaded["Sixty Move Rule"].Enabled);
                Assert.Equal(130m, reloaded["Rule60MaxPly"].Number);
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
        }, default);
    }
}
