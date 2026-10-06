using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace PaddiXiangqi;

/// <summary>
/// Avalonia 12's ComboBox peer exposes a writable value but throws from SetValue.
/// That exception escapes the macOS native accessibility callback and aborts the app.
/// Keep the normal ComboBox behavior while providing a working value setter.
/// </summary>
public sealed class SafeComboBox : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);
    protected override AutomationPeer OnCreateAutomationPeer() => new SafeComboBoxAutomationPeer(this);
}

internal sealed class SafeComboBoxAutomationPeer : ComboBoxAutomationPeer, IValueProvider
{
    private readonly ComboBox _comboBox;

    public SafeComboBoxAutomationPeer(ComboBox comboBox) : base(comboBox) => _comboBox = comboBox;

    bool IValueProvider.IsReadOnly => false;

    string IValueProvider.Value => ItemText(_comboBox.SelectedItem) ?? string.Empty;

    void IValueProvider.SetValue(string? value)
    {
        foreach (var item in _comboBox.Items)
        {
            if (!string.Equals(ItemText(item), value, StringComparison.Ordinal)) continue;
            _comboBox.SelectedItem = item;
            return;
        }
        // Native accessibility callbacks must not throw for an unknown value.
    }

    private static string? ItemText(object? item) => item is ComboBoxItem option
        ? option.Content?.ToString() : item?.ToString();
}
