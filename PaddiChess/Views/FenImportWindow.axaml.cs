using Avalonia.Controls;
using Avalonia.Interactivity;
using PaddiXiangqi.Core;

namespace PaddiXiangqi.Views;

public partial class FenImportWindow : Window
{
    public FenImportWindow() => InitializeComponent();
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
    private void Import_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var game = new XiangqiGame();
            game.LoadFen(FenInput.Text ?? "");
            Close(game.CurrentFen());
        }
        catch (FormatException ex) { ErrorText.Text = ex.Message; }
    }
}
