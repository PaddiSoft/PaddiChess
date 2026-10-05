using Avalonia.Controls;
using Avalonia.Interactivity;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private ExternalPermissions _externalPermissions = new(false, false);
    private Task<bool>? _externalPermissionCheck;
    private bool _externalPermissionRequestActive, _externalStarting;
    private readonly CancellationTokenSource _externalPermissionLifetime = new();

    private Task<bool> EnsureExternalPermissionsAsync()
    {
        if (_externalPermissionCheck is { IsCompleted: false }) return _externalPermissionCheck;
        return _externalPermissionCheck = CheckExternalPermissionsCoreAsync();
    }
    private async Task<bool> CheckExternalPermissionsCoreAsync()
    {
        if (_closing) return false;
        if (!ExternalDesktop.Supported)
        {
            ShowExternalPermissionState("此系统暂不支持外部窗口接管。"); return false;
        }
        ExternalPermissionRetryButton.IsEnabled = false;
        try
        {
            _externalDesktop ??= ExternalDesktop.Create();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_externalPermissionLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var wasReady = _externalPermissions.Ready;
            _externalPermissions = await _externalDesktop.GetPermissionsAsync(timeout.Token);
            if (_closing) return false;
            // A capture process that was alive before a new grant may retain stale permission state.
            if (!wasReady && _externalPermissions.Ready && !_externalRunning)
                await _externalDesktop.CloseCaptureAsync();
            if (!_externalPermissions.Ready) _externalCancellation?.Cancel();
            ShowExternalPermissionState(_externalPermissions.Ready ? "权限就绪，可以继续接管。" : "请授权缺少的项目。返回此窗口会自动重新检测。");
            return _externalPermissions.Ready;
        }
        catch (Exception ex)
        {
            if (!_closing) { _externalPermissions = new(false,false); ShowExternalPermissionState("权限检测未完成：" + ex.Message); }
            return false;
        }
        finally { if (!_closing) ExternalPermissionRetryButton.IsEnabled = true; }
    }
    private void ShowExternalPermissionState(string message)
    {
        ExternalPermissionGate.IsVisible = !_externalPermissions.Ready;
        ExternalWorkspacePanel.IsVisible = _externalPermissions.Ready;
        ExternalScreenPermissionText.Text = _externalPermissions.ScreenCapture ? "已授权" : "未授权 · 用于读取棋盘";
        ExternalInputPermissionText.Text = _externalPermissions.Accessibility ? "已授权" : "未授权 · 用于落子";
        ExternalScreenPermissionButton.IsEnabled = !_externalPermissions.ScreenCapture && !_externalPermissionRequestActive && ExternalDesktop.Supported;
        ExternalInputPermissionButton.IsEnabled = !_externalPermissions.Accessibility && !_externalPermissionRequestActive && ExternalDesktop.Supported;
        ExternalPermissionMessage.Text = message;
        RefreshExternalControls();
    }
    private async void ExternalCheckPermissions_Click(object? sender, RoutedEventArgs e) => await EnsureExternalPermissionsAsync();
    private async void ExternalGrantPermission_Click(object? sender, RoutedEventArgs e)
    {
        if (_externalPermissionRequestActive || sender is not Button button) return;
        _externalPermissionRequestActive = true;
        ShowExternalPermissionState("请在系统设置中开启 Paddi象棋 的对应权限，完成后返回。");
        try
        {
            _externalDesktop ??= ExternalDesktop.Create();
            await _externalDesktop.RequestPermissionAsync(button.Tag?.ToString() == "screen" ? ExternalPermission.ScreenCapture : ExternalPermission.Accessibility,
                _externalPermissionLifetime.Token);
        }
        catch (Exception ex) { if (!_closing) ExternalPermissionMessage.Text = ex.Message; }
        finally
        {
            _externalPermissionRequestActive = false;
            if (!_closing) await EnsureExternalPermissionsAsync();
        }
    }
}
