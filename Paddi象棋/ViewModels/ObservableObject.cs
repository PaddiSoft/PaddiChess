using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace PaddiXiangqi.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(property);
        return true;
    }
    protected void Changed([CallerMemberName] string? property = null)
        => PropertyChanged?.Invoke(this, new(property));
}

public sealed class ActionCommand(Action execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
