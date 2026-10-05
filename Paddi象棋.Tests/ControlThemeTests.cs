using Avalonia.Controls;

namespace PaddiXiangqi.Tests;

public class ControlThemeTests
{
    [Fact]
    public void AccessibleComboBoxRetainsBaseComboBoxTheme()
    {
        var combo = new SafeComboBox();
        Assert.Equal(typeof(ComboBox), combo.StyleKey);
    }
}
