using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace PaddiXiangqi.Views;

/// <summary>A declarative workspace page. Actions are delegated to the session owner.</summary>
public class WorkspaceView : UserControl
{
    public static readonly StyledProperty<Control?> ExternalConfigurationSourceProperty =
        AvaloniaProperty.Register<WorkspaceView, Control?>(nameof(ExternalConfigurationSource));

    private readonly Dictionary<string, Control> _controls = new(StringComparer.Ordinal);
    public event EventHandler<WorkspaceActionEventArgs>? ActionRequested;

    public Control? ExternalConfigurationSource
    {
        get => GetValue(ExternalConfigurationSourceProperty);
        set => SetValue(ExternalConfigurationSourceProperty, value);
    }

    public T Control<T>(string name) where T : Control
    {
        if (_controls.TryGetValue(name, out var cached)) return (T)cached;
        var control = this.FindControl<T>(name) ??
            throw new InvalidOperationException($"Missing {typeof(T).Name} '{name}' in {GetType().Name}.");
        _controls.Add(name, control);
        return control;
    }

    protected void Action_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WorkspaceAction action })
            ActionRequested?.Invoke(this, new(action, sender, e));
    }

    internal static void ComposeNameScopes(Control owner, params WorkspaceView[] pages)
    {
        var primary = NameScope.GetNameScope(owner) ?? throw new InvalidOperationException("Workspace has no name scope.");
        var composed = new NameScope();
        CopyNames(owner, primary);
        foreach (var page in pages)
            CopyNames(page, NameScope.GetNameScope(page) ?? throw new InvalidOperationException("Workspace page has no name scope."));
        composed.Complete();
        NameScope.SetNameScope(owner, composed);

        // One startup traversal preserves named automation access. Controls and
        // compiled bindings keep their original page-local scope; hot paths use
        // cached typed access and never traverse the visual tree.
        void CopyNames(Control root, INameScope source)
        {
            foreach (var name in root.GetLogicalDescendants().OfType<Control>()
                         .Select(control => control.Name).Where(name => name is not null).Distinct())
            {
                if (composed.Find(name!) is null && source.Find(name!) is { } control)
                    composed.Register(name!, control);
            }
        }
    }
}
