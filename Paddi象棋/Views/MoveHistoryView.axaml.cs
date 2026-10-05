using Avalonia.Controls;
using PaddiXiangqi.ViewModels;

namespace PaddiXiangqi.Views;

public partial class MoveHistoryView : UserControl
{
    public MoveHistoryView() => InitializeComponent();
    public void ScrollToEnd()
    {
        if (DataContext is MoveHistoryViewModel { Rows.Count: > 0 } model)
            HistoryList.ScrollIntoView(model.Rows[^1]);
    }
}
