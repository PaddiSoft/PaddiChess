using Avalonia;
using Avalonia.Controls;
using PaddiXiangqi.External;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    [Fact]
    public async Task TakeoverWorkflowShowsConnectionBeforeStartAndKeepsActionsInSyncWithSessionState()
    {
        await Session.Dispatch(async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), $"paddi-external-workflow-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            var previousPath = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", Path.Combine(folder, "settings.json"));
            new AppPreferences { AutoAnalyze = false }.Save();
            var window = new MainWindow { Width = 1200, Height = 700 };
            var desktop = new FakeDesktop();
            Set(window, "_externalDesktop", desktop);
            try
            {
                window.Show();
                Click(window, "ExternalWorkspaceButton");
                await (Task<bool>)typeof(MainWindow).GetMethod("EnsureExternalPermissionsAsync", Private)!.Invoke(window, null)!;
                var targets = window.FindControl<ComboBox>("ExternalWindowBox")!;
                var connect = window.FindControl<Button>("ExternalConnectButton")!;
                var start = window.FindControl<Button>("ExternalStartButton")!;
                var stop = window.FindControl<Button>("ExternalStopButton")!;
                var disconnect = window.FindControl<Button>("ExternalDisconnectButton")!;
                var badge = window.FindControl<Border>("ExternalConnectionBadge")!;
                var hint = window.FindControl<TextBlock>("ExternalFlowHint")!;

                void Refresh() => typeof(MainWindow).GetMethod("RefreshExternalControls", Private)!.Invoke(window, null);
                void AssertState(bool canConnect, bool canStart, string connectionState)
                {
                    Assert.Equal(canConnect, connect.IsEffectivelyEnabled);
                    Assert.Equal(canStart, start.IsEffectivelyEnabled);
                    Assert.Equal(connectionState, window.FindControl<TextBlock>("ExternalConnectionText")!.Text);
                    Assert.True(hint.IsVisible);
                    Assert.False(string.IsNullOrWhiteSpace(hint.Text));
                }
                Rect InWindow(Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);

                targets.ItemsSource = new[] { desktop.Target };
                targets.SelectedIndex = -1;
                Set(window, "_externalPositionReady", false);
                Refresh();
                AssertState(false, false, "未连接");
                Assert.Contains("primary", connect.Classes);
                Assert.DoesNotContain("primary", start.Classes);
                Assert.Equal("① 连接并同步", connect.Content);
                Assert.False(stop.IsEffectivelyEnabled);
                Assert.False(disconnect.IsVisible);

                window.UpdateLayout();
                var refresh = window.FindControl<Button>("ExternalRefreshButton")!;
                Control[] flow = [targets, refresh, connect, start];
                for (var i = 0; i < flow.Length - 1; i++)
                {
                    var before = InWindow(flow[i]);
                    var after = InWindow(flow[i + 1]);
                    Assert.True(before.Width > 0 && before.Right <= after.Left,
                        $"{flow[i].Name} 应位于 {flow[i + 1].Name} 左侧，且互不遮挡。");
                    Assert.True(Math.Abs(before.Center.Y - after.Center.Y) <= 2,
                        "窗口选择、连接和接管应沿同一行依次显示。");
                }
                Assert.True(InWindow(start).Right <= window.ClientSize.Width);

                // Selection changes must immediately enable or disable connection.
                targets.SelectedIndex = 0;
                AssertState(true, false, "未连接");
                targets.SelectedIndex = -1;
                AssertState(false, false, "未连接");
                targets.SelectedIndex = 0;

                Set(window, "_externalCalibrating", true);
                Refresh();
                AssertState(false, false, "同步中");
                Assert.Equal("① 同步中…", connect.Content);
                Assert.True(stop.IsEffectivelyEnabled);
                Set(window, "_externalCalibrating", false);

                // Retaining a screenshot and geometry alone is not a verified position.
                var frame = await desktop.CaptureAsync(desktop.Target, default);
                var calibration = new BoardCalibration(40, 40, 440, 490, false);
                Set(window, "_externalFrame", frame);
                Set(window, "_externalCalibration", calibration);
                Refresh();
                Assert.False(start.IsEffectivelyEnabled);

                // First-time manual calibration is ready before a live link exists.
                Set(window, "_externalPositionReady", true);
                Refresh();
                Assert.True(start.IsEffectivelyEnabled);
                Assert.Equal("② 开始接管", start.Content);

                Set(window, "_externalLinked", true);
                Set(window, "_externalObserving", true);
                Refresh();
                AssertState(false, true, "准备就绪");
                Assert.Equal("① 已连接", connect.Content);
                Assert.Contains("primary", start.Classes);
                Assert.DoesNotContain("primary", connect.Classes);
                Assert.Contains("connected", badge.Classes);
                Assert.True(disconnect.IsVisible);
                Assert.True(stop.IsEffectivelyEnabled);

                Set(window, "_externalRunning", true);
                Refresh();
                AssertState(false, false, "接管中");
                Assert.Equal("接管中", start.Content);
                Assert.Contains("running", badge.Classes);
                Assert.DoesNotContain("connected", badge.Classes);

                Set(window, "_externalRunning", false);
                Set(window, "_externalObserving", false);
                Set(window, "_externalPositionReady", false);
                Refresh();
                AssertState(false, true, "已暂停");
                Assert.Equal("② 继续接管", start.Content);
                Assert.Contains("waiting", badge.Classes);
                Assert.False(stop.IsEffectivelyEnabled);

                // A finished round retains its target so the next round can reconnect.
                Set(window, "_externalLinked", false);
                Set(window, "_externalCompleted", true);
                Refresh();
                Assert.True(start.IsEffectivelyEnabled);
                Assert.Equal("接管下一局", start.Content);
                Assert.Equal("已结束", window.FindControl<TextBlock>("ExternalConnectionText")!.Text);
                Assert.DoesNotContain("running", badge.Classes);
                Assert.DoesNotContain("waiting", badge.Classes);

                // Exercise the real disconnect handler from a paused connection.
                Set(window, "_externalCompleted", false);
                Set(window, "_externalLinked", true);
                Refresh();
                Click(window, "ExternalDisconnectButton");
                AssertState(true, false, "未连接");
                Assert.Equal("① 连接并同步", connect.Content);
                Assert.Equal("② 开始接管", start.Content);
                Assert.Contains("primary", connect.Classes);
                Assert.DoesNotContain("primary", start.Classes);
                Assert.False(disconnect.IsVisible);
                Assert.False(window.FindControl<Button>("ExternalResyncButton")!.IsVisible);
                Assert.Equal(0, desktop.InputAttempts);
                Assert.Equal(0, desktop.MovesSent);
            }
            finally
            {
                window.Close();
                await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", previousPath);
                Directory.Delete(folder, true);
            }
            return true;
        }, default);
    }
}
