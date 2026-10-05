using Avalonia.Controls;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private Task? _cleanupTask;

    private bool _shutdownReady;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shutdownReady) return;
        e.Cancel = true;
        if (_cleanupTask != null) return;
        BeginCleanup();
        try { await _cleanupTask!; }
        catch (Exception ex)
        {
            // The engine/capture cleanup uses finally blocks. Do not let an async
            // Closing handler crash the process after those resources are released.
            System.Diagnostics.Debug.WriteLine($"Shutdown cleanup: {ex.GetType().Name}");
        }
        finally { _shutdownReady = true; Close(); }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Fallback for a lifetime that closes a window without a cancellable event.
        if (_cleanupTask == null) BeginCleanup();
    }

    private void BeginCleanup()
    {
        _closing = true;
        _workspaceMotionCancellation?.Cancel();
        _externalCancellation?.Cancel();
        _externalSetupCancellation?.Cancel();
        _externalPermissionLifetime.Cancel();
        SaveSettings();
        _modelFetchCancellation?.Cancel();
        _benchmarkCancellation?.Cancel();
        _enginePluginCancellation?.Cancel();
        CancelSearch();
        _cleanupTask = CleanupAsync();
    }

    private async Task CleanupAsync()
    {
        try { await _settingsWriter.FlushAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Settings flush: {ex.GetType().Name}"); }
        try { if (_externalTask is not null) await _externalTask; } catch { }
        await EndExternalHistoryAsync("关闭程序");
        try { if (_searchTask is not null) await _searchTask; } catch { }
        try { if (_llmTask is not null) await _llmTask; } catch { }
        try { await DisposeLlmInsightsAsync(); }
        finally
        {
            try
            {
                await Task.WhenAll(_engine.DisposeAsync().AsTask(), _modelRules.DisposeAsync().AsTask(),
                    _externalPlayingEngine?.DisposeAsync().AsTask() ?? Task.CompletedTask);
            }
            finally
            {
                try { if (_externalDesktop != null) await _externalDesktop.CloseCaptureAsync(); }
                finally { _externalPreview?.Dispose(); }
            }
        }
    }

}
