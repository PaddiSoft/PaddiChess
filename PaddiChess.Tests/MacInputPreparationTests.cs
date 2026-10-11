using System.Diagnostics;
using System.Reflection;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public sealed class MacInputPreparationFactAttribute : FactAttribute
{
    public MacInputPreparationFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Requires the macOS bridge; never sends native input.";
    }
}

[Collection("Desktop integration")]
public class MacInputPreparationTests
{
    private static Process? Prepared(MacExternalDesktop desktop) =>
        (Process?)typeof(MacExternalDesktop).GetField("_preparedInput", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(desktop);

    [MacInputPreparationFact]
    public async Task PreparedHelperIsReusedUntilConsumedAndClosedWithTheSession()
    {
        var desktop = new MacExternalDesktop();
        try
        {
            await desktop.PrepareInputAsync(default);
            var prepared = Assert.IsType<Process>(Prepared(desktop));
            Assert.False(prepared.HasExited);
            await desktop.PrepareInputAsync(default);
            Assert.Same(prepared, Prepared(desktop));
            using var observer = Process.GetProcessById(prepared.Id);
            await desktop.CloseCaptureAsync();
            await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Null(Prepared(desktop));
        }
        finally { await desktop.CloseCaptureAsync(); }
    }

    [MacInputPreparationFact]
    public async Task WarmHelperRejectsAnInvalidTargetBeforeAnyInput()
    {
        var desktop = new MacExternalDesktop { InputDelivery = ExternalInputDelivery.TargetWindow };
        try
        {
            await desktop.PrepareInputAsync(default);
            var prepared = Assert.IsType<Process>(Prepared(desktop));
            using var observer = Process.GetProcessById(prepared.Id);
            var invalid = new ExternalWindow(0, 0, "invalid test target", 0, 0, 300, 300);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => desktop.MoveAsync(invalid, 40, 40, 80, 80, default));
            Assert.Contains("目标窗口不可用", error.Message);
            await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Null(Prepared(desktop));
        }
        finally { await desktop.CloseCaptureAsync(); }
    }
}
