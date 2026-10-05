using System.Reflection;
using Avalonia.Controls;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

[Collection("Desktop integration")]
public class EnginePhaseUiTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task LargeUserChosenHashSurvivesRestartAndReachesPlayingSettings()
    {
        await ExternalSessionTests.Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-hash-{Guid.NewGuid():N}.json");
            var oldPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            MainWindow? window = null;
            try
            {
                new AppPreferences { AutoAnalyze = false, HashMb = 8192 }.Save();
                window = new MainWindow();
                window.Show();
                var hash = window.FindControl<NumericUpDown>("HashBox")!;
                Assert.Equal(8192m, hash.Value);
                Assert.Equal(EngineSettings.MaxHashMb, hash.Maximum);
                var playing = typeof(MainWindow).GetMethod("ReadPlayingEngineSettings", Private)!;
                Assert.Equal(8192, ((EngineSettings)playing.Invoke(window, null)!).HashMb);
                hash.Value = 4096;
                window.Close();
                await (Task)typeof(MainWindow).GetField("_cleanupTask", Private)!.GetValue(window)!;
                window = new MainWindow();
                window.Show();
                Assert.Equal(4096m, window.FindControl<NumericUpDown>("HashBox")!.Value);
                Assert.Equal(4096, ((EngineSettings)playing.Invoke(window, null)!).HashMb);
            }
            finally
            {
                if (window is not null)
                {
                    window.Close();
                    await (Task)typeof(MainWindow).GetField("_cleanupTask", Private)!.GetValue(window)!;
                }
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", oldPath);
                if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task UserChosenPhaseTimesPersistAndApplyToPlayingOnly()
    {
        await ExternalSessionTests.Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-phases-{Guid.NewGuid():N}.json");
            var oldPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            var window = new MainWindow();
            try
            {
                window.Show();
                window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked = false;
                window.FindControl<Slider>("LevelSlider")!.Value = 20;
                window.FindControl<NumericUpDown>("ThinkBox")!.Value = 2;
                window.FindControl<NumericUpDown>("OpeningTimeBox")!.Value = .5m;
                window.FindControl<NumericUpDown>("MiddleTimeBox")!.Value = 3.5m;
                window.FindControl<NumericUpDown>("EndgameTimeBox")!.Value = 9;
                window.FindControl<NumericUpDown>("EndgameAttackersBox")!.Value = 7;
                window.FindControl<CheckBox>("PhaseTimeCheck")!.IsChecked = true;
                var playing = typeof(MainWindow).GetMethod("ReadPlayingEngineSettings", Private)!;
                var analysis = typeof(MainWindow).GetMethod("ReadEngineSettings", Private)!;
                Assert.Equal(500, ((EngineSettings)playing.Invoke(window, null)!).MoveTimeMs);
                Assert.Equal(2000, ((EngineSettings)analysis.Invoke(window, null)!).MoveTimeMs);

                var afterOpening = new XiangqiGame();
                foreach (var move in new[] { "h2e2", "h9g7", "b0c2", "b9c7" })
                    Assert.True(afterOpening.TryMoveUci(move, out _));
                var game = (XiangqiGame)typeof(MainWindow).GetField("_game", Private)!.GetValue(window)!;
                game.LoadFen(afterOpening.CurrentFen());
                typeof(MainWindow).GetMethod("RefreshEngineSettingsSummary", Private)!.Invoke(window, null);
                Assert.Equal(3500, ((EngineSettings)playing.Invoke(window, null)!).MoveTimeMs);
                Assert.Contains("中局", window.FindControl<TextBlock>("CurrentPhaseText")!.Text);
                Assert.Contains("3.5", window.FindControl<TextBlock>("EffectiveSettingsText")!.Text);
                await ((PaddiXiangqi.Services.PreferencesWriter)typeof(MainWindow).GetField("_settingsWriter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!).FlushAsync();
                var saved = AppPreferences.Load().EnginePhases;
                Assert.True(saved.Enabled);
                Assert.Equal((500, 3500, 9000), (saved.OpeningTimeMs, saved.MiddleTimeMs, saved.EndgameTimeMs));
                Assert.Equal(7, saved.EndgameMaterialPoints);

                window.FindControl<Slider>("LevelSlider")!.Value = 10;
                Assert.False(window.FindControl<StackPanel>("PhaseTimeControls")!.IsEnabled);
                Assert.Equal(new EngineSettings(10, 1, 128, 2, 30).MoveTimeMs,
                    ((EngineSettings)playing.Invoke(window, null)!).MoveTimeMs);
            }
            finally
            {
                window.Close();
                await (Task)typeof(MainWindow).GetField("_cleanupTask", Private)!.GetValue(window)!;
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", oldPath);
                if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, CancellationToken.None);
    }
}
