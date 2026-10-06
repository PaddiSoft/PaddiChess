using System.Reflection;
using Avalonia.Controls;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

[Collection("Desktop integration")]
public class EngineDepthLimitTests
{
    [Fact]
    public async Task DepthLimitDefaultsOffAndExplicitChoiceSurvivesRestart()
    {
        await ExternalSessionTests.Session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-depth-policy-{Guid.NewGuid():N}.json");
            var old = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            MainWindow? window = null;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                // Existing profiles had a depth value but no explicit opt-in flag.
                new AppPreferences { Level = 20, MaxDepth = 2, AutoAnalyze = false }.Save();
                var read = typeof(MainWindow).GetMethod("ReadPlayingEngineSettings", flags)!;
                window = new MainWindow(); window.Show();
                Assert.False(window.FindControl<CheckBox>("DepthLimitCheck")!.IsChecked);
                Assert.False(window.FindControl<NumericUpDown>("DepthBox")!.IsEnabled);
                Assert.Null(((EngineSettings)read.Invoke(window, null)!).SearchDepthLimit);
                window.FindControl<CheckBox>("DepthLimitCheck")!.IsChecked = true;
                window.FindControl<NumericUpDown>("DepthBox")!.Value = 255;
                Assert.Equal(255, ((EngineSettings)read.Invoke(window, null)!).SearchDepthLimit);
                window.Close(); await (Task)typeof(MainWindow).GetField("_cleanupTask", flags)!.GetValue(window)!;
                window = new MainWindow(); window.Show();
                Assert.True(window.FindControl<CheckBox>("DepthLimitCheck")!.IsChecked);
                Assert.Equal(255, ((EngineSettings)read.Invoke(window, null)!).SearchDepthLimit);
                window.FindControl<CheckBox>("DepthLimitCheck")!.IsChecked = false;
                Assert.Null(((EngineSettings)read.Invoke(window, null)!).SearchDepthLimit);
                await ((PaddiXiangqi.Services.PreferencesWriter)typeof(MainWindow).GetField("_settingsWriter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!).FlushAsync();
                Assert.False(AppPreferences.Load().UseDepthLimit);
            }
            finally
            {
                if (window is not null)
                { window.Close(); await (Task)typeof(MainWindow).GetField("_cleanupTask", flags)!.GetValue(window)!; }
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", old);
                if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, default);
    }

    [Fact]
    public async Task RealEngineSearchExceedsConfiguredDepthOnlyWhenLimitIsDisabled()
    {
        await using var engine = new PikafishClient();
        var legal = new XiangqiGame().AllLegalMoves().Select(move => move.Uci).ToArray();
        var limited = new EngineSettings(20, 1, 16, 1, 1) { MoveTimeOverrideMs = 350, UseDepthLimit = true };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first = await engine.SearchAsync(XiangqiGame.InitialFen, "", limited, null, timeout.Token, legal);
        Assert.All(first.Candidates, candidate => Assert.InRange(candidate.Depth, 1, 1));
        Assert.Contains(first.BestMove, legal);
        var second = await engine.SearchAsync(XiangqiGame.InitialFen, "", limited with { UseDepthLimit = false }, null,
            timeout.Token, legal);
        Assert.Contains(second.Candidates, candidate => candidate.Depth > 1);
        Assert.Contains(second.BestMove, legal);
    }
}
