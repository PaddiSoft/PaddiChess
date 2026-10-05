using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PaddiXiangqi.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Icon = BrandAssets.Icon;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
