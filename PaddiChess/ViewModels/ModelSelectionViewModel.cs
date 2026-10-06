namespace PaddiXiangqi.ViewModels;

public sealed class ModelSelectionViewModel(string name, bool enabled, Action<string, bool> changed) : ObservableObject
{
    public string Name { get; } = name;
    private bool _enabled = enabled;
    public bool Enabled
    {
        get => _enabled;
        set { if (Set(ref _enabled, value)) changed(Name, value); }
    }
}
