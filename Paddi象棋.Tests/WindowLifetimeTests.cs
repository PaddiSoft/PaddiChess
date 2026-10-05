using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

public class WindowLifetimeTests
{
    [Fact]
    public async Task CloseWaitsForLatestSettingsFlushAndNativeCleanup()
    {
        var session = ExternalSessionTests.Session;
        await session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"paddi-close-{Guid.NewGuid():N}.json");
            var previous = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", path);
            var window = new MainWindow();
            try
            {
                window.Show();
                window.FindControl<CheckBox>("AutoAnalyzeCheck")!.IsChecked = false;
                window.FindControl<NumericUpDown>("HashBox")!.Value = 384;
                window.Close();
                Assert.True(window.IsVisible); // Last-window exit must not interrupt the pending atomic write.
                var cleanup = (Task)typeof(MainWindow).GetField("_cleanupTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                await cleanup;
                await Task.Yield();
                Assert.False(window.IsVisible);
                Assert.Equal(384, AppPreferences.Load().HashMb);
            }
            finally
            {
                window.Close();
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", previous);
                if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }, CancellationToken.None);
    }
}
