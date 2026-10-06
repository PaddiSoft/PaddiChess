using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PaddiXiangqi.Core;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

[Collection("Desktop integration")]
public class ExternalTurnLayoutTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(1200, 700)]
    [InlineData(1360, 880)]
    public async Task TurnControlsAreVisibleWithoutOpeningMenusOrScrolling(int width, int height)
    {
        await ExternalSessionTests.Session.Dispatch(async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), $"paddi-turn-layout-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            var old = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", Path.Combine(folder, "settings.json"));
            new AppPreferences { AutoAnalyze = false }.Save();
            MainWindow? window = null;
            try
            {
                window = new MainWindow { Width = width, Height = height };
                typeof(MainWindow).GetField("_externalDesktop", Private)!.SetValue(window, new ExternalSessionTests.FakeDesktop());
                window.Show();
                window.FindControl<Button>("ExternalWorkspaceButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await (Task<bool>)typeof(MainWindow).GetMethod("EnsureExternalPermissionsAsync", Private)!.Invoke(window, null)!;
                window.UpdateLayout();

                var panel = window.FindControl<Grid>("ExternalTurnPanel")!;
                var turn = window.FindControl<ComboBox>("ExternalTurnBox")!;
                var automatic = window.FindControl<CheckBox>("ExternalAutoTurnCheck")!;
                var config = window.FindControl<Grid>("ExternalConfigPanel")!;
                Assert.Contains(panel, turn.GetVisualAncestors());
                Assert.Contains(panel, automatic.GetVisualAncestors());
                Assert.DoesNotContain(config, panel.GetVisualAncestors());
                Assert.Empty(panel.GetVisualAncestors().OfType<Expander>());
                Assert.True(window.FindControl<Grid>("ExternalWorkspacePanel")!.IsVisible);
                Assert.True(turn.Bounds.Width >= 70);
                Assert.True(automatic.Bounds.Width >= 80);

                var turnOrigin = turn.TranslatePoint(default, window)!.Value;
                var automaticOrigin = automatic.TranslatePoint(default, window)!.Value;
                var bottom = turn.TranslatePoint(new Point(0, turn.Bounds.Height), window)!.Value.Y;
                var live = window.FindControl<StackPanel>("ExternalLiveControls")!;
                var liveBottom = live.TranslatePoint(new Point(0, live.Bounds.Height), window)!.Value.Y;
                Assert.True(turnOrigin.Y > 0);
                Assert.True(bottom < liveBottom && liveBottom <= window.ClientSize.Height,
                    $"当前轮次控件必须显示在常用操作区：{bottom}, 操作区 {liveBottom}");
                Assert.True(automaticOrigin.X >= turnOrigin.X + turn.Bounds.Width);
                Assert.True(automaticOrigin.Y + automatic.Bounds.Height <= liveBottom);
            }
            finally
            {
                if (window is not null)
                {
                    window.Close();
                    await (Task)typeof(MainWindow).GetField("_cleanupTask", Private)!.GetValue(window)!;
                }
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", old);
                Directory.Delete(folder, true);
            }
            return true;
        }, CancellationToken.None);
    }
}
