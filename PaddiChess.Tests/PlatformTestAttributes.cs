namespace PaddiXiangqi.Tests;

public sealed class UnixEngineFactAttribute : FactAttribute
{
    public UnixEngineFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Requires a Unix executable Python engine fixture.";
    }
}

public sealed class UnixEngineTheoryAttribute : TheoryAttribute
{
    public UnixEngineTheoryAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Requires a Unix executable Python engine fixture.";
    }
}

public sealed class WindowsRuntimeFactAttribute : FactAttribute
{
    public WindowsRuntimeFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires the Windows inference distribution.";
    }
}

public sealed class MacClockFactAttribute : FactAttribute
{
    public MacClockFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Requires the macOS native clock reader.";
    }
}
