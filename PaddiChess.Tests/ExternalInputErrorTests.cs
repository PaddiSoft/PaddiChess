using System.Reflection;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public class ExternalInputErrorTests
{
    private static Exception Parse(string message)
    {
        var backend = typeof(ExternalDesktop).Assembly.GetType("PaddiXiangqi.External.MacExternalDesktop")!;
        var method = backend.GetMethod("ThrowNativeError", BindingFlags.Static | BindingFlags.NonPublic)!;
        return Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [message])).InnerException!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeBlockRetainsWhetherInputMayAlreadyHaveBeenSent(bool started)
    {
        var error = Parse("input-blocked:{\"inputStarted\":" + (started ? "true" : "false") + ",\"message\":\"落点暂不可操作\"}");
        var blocked = Assert.IsType<ExternalInputBlockedException>(error);
        Assert.Equal(started, blocked.InputStarted);
        Assert.Equal("落点暂不可操作", blocked.Message);
    }

    [Theory]
    [InlineData("input-blocked:{")]
    [InlineData("input-blocked:{\"message\":\"missing stage\"}")]
    [InlineData("input-blocked:{\"inputStarted\":\"false\",\"message\":\"invalid stage\"}")]
    [InlineData("棋盘被遮挡")]
    public void MissingOrMalformedStageCannotAuthorizeAnAutomaticRetry(string message)
        => Assert.IsType<InvalidOperationException>(Parse(message));
}
