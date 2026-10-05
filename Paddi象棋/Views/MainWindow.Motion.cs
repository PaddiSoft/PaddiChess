using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Styling;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private int _motionTab = -1;
    private CancellationTokenSource? _workspaceMotionCancellation;

    private void RefreshMotionPreference()
    {
        Classes.Set("motion", Board.AnimationDurationMs > 0);
        if (Board.AnimationDurationMs == 0) _workspaceMotionCancellation?.Cancel();
    }

    private void AnimateWorkspaceChange()
    {
        if (_motionTab == MainTabs.SelectedIndex) return;
        var previous = _motionTab;
        _motionTab = MainTabs.SelectedIndex;
        _workspaceMotionCancellation?.Cancel();
        if (previous < 0 || !IsVisible || Board.AnimationDurationMs == 0 || _closing) return;
        var lifetime = new CancellationTokenSource();
        _workspaceMotionCancellation = lifetime;
        _ = FadeWorkspaceAsync(lifetime);
    }

    private async Task FadeWorkspaceAsync(CancellationTokenSource lifetime)
    {
        try
        {
            // Opacity is composited: no repeated measure/arrange, and no blocking
            // delay in the capture or move-delivery path. Only tab changes animate.
            var animation = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(150), Easing = new CubicEaseOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, .84) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1d) } }
                }
            };
            await animation.RunAsync(WorkspaceGrid, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_workspaceMotionCancellation, lifetime)) _workspaceMotionCancellation = null;
            lifetime.Dispose();
        }
    }
}
