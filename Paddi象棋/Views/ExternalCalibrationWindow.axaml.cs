using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Views;

public partial class ExternalCalibrationWindow : Window
{
    private readonly byte[] _png;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _cancel;
    private BoardCalibration? _selected;
    private int _revision;
    // Parameterless construction supports the Avalonia designer/runtime XAML loader.
    public ExternalCalibrationWindow()
    {
        InitializeComponent();
        _lifetime = new CancellationTokenSource();
        _png = [];
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    public ExternalCalibrationWindow(byte[] png, CancellationToken cancellation)
    {
        InitializeComponent(); _png = png;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Preview.SetImage(png);
        Preview.Changed += OnSelectionChanged;
        _cancel = _lifetime.Token.Register(() => Dispatcher.UIThread.Post(() => { if (IsVisible) Close(); }));
        Opened += async (_, _) => await LocateAsync();
        Closed += (_, _) => { _revision++; _cancel.Dispose(); _lifetime.Cancel(); _lifetime.Dispose(); Preview.Dispose(); };
    }
    private void OnSelectionChanged()
    {
        _selected = null; AcceptButton.IsEnabled = false;
        if (Preview.First is not { } first) { HelpText.Text = "点击左上角交叉点中心。"; return; }
        if (Preview.Last is not { } last) { HelpText.Text = "再点击右下角交叉点中心。"; return; }
        try
        {
            var geometry = new BoardCalibration(first.X, first.Y, last.X, last.Y, false);
            geometry.Validate(Preview.PixelWidth, Preview.PixelHeight);
            _selected = geometry; AcceptButton.IsEnabled = true;
            HelpText.Text = "确认 90 个蓝点对准棋盘交叉点，然后采用标定。";
        }
        catch (InvalidOperationException ex) { HelpText.Text = ex.Message; }
    }
    private async Task LocateAsync()
    {
        int revision = ++_revision;
        LocateButton.IsEnabled = false; HelpText.Text = "正在查找九路十行棋盘…";
        try
        {
            var found = await Task.Run(() => BoardLocator.Locate(_png), _lifetime.Token);
            if (revision != _revision || _lifetime.IsCancellationRequested) return;
            if (found != null) Preview.Set(found);
            else HelpText.Text = "未找到可靠棋盘，请手动点击左上和右下交叉点。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (revision == _revision) HelpText.Text = ex.Message; }
        finally { if (revision == _revision) LocateButton.IsEnabled = true; }
    }
    private async void Locate_Click(object? sender, RoutedEventArgs e) => await LocateAsync();
    private void Reset_Click(object? sender, RoutedEventArgs e) { _revision++; LocateButton.IsEnabled = true; Preview.Reset(); }
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
    private void Accept_Click(object? sender, RoutedEventArgs e) { if (_selected != null) Close(_selected); }
}
