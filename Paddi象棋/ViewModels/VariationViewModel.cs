using System.Windows.Input;

namespace PaddiXiangqi.ViewModels;

public sealed class VariationViewModel(int slot, Action<int> select) : ObservableObject
{
    private string _header = "", _moves = "";
    private bool _selected;
    public string Header { get => _header; set { if (Set(ref _header, value)) Changed(nameof(AccessibleName)); } }
    public string Moves { get => _moves; set { if (Set(ref _moves, value)) Changed(nameof(AccessibleName)); } }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public string AccessibleName => $"候选变化 {Header}，{Moves}";
    public ICommand Select { get; } = new ActionCommand(() => select(slot));
}
