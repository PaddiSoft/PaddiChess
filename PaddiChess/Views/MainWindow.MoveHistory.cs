using PaddiXiangqi.ViewModels;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private readonly MoveHistoryViewModel _history;
    private void RefreshMoveListIncrementally() => _history.Update(_game.StartFen, _game.History, _game.Ply, _notes);
}
