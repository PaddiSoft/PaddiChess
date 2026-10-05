using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace PaddiXiangqi;

public partial class App : Application
{
    private Views.AboutWindow? _aboutWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Views.MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnAboutClick(object? sender, EventArgs e)
    {
        if (_aboutWindow is { } existing) { existing.Activate(); return; }
        var about = _aboutWindow = new Views.AboutWindow();
        about.Closed += (_, _) => _aboutWindow = null;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true } owner })
            await about.ShowDialog(owner);
        else about.Show();
    }
}
