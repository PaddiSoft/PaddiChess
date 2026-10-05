using System.Reflection;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public class ExternalCaptureHeartbeatTests
{
    private static readonly MethodInfo Validate = typeof(ExternalDesktop).Assembly
        .GetType("PaddiXiangqi.External.MacExternalDesktop")!
        .GetMethod("ValidateCaptureState", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Fact]
    public void HealthyIdleProducerCanReuseOldPixelsWithoutRestarting()
    {
        var frame = new ExternalFrame(new(7, 8, "own static fixture", 0, 0, 480, 530), [])
        { Sequence = 1, FrameAgeMs = 60_000, CallbackAgeMs = 12, StreamStatus = "idle" };
        // The original pixel timestamp may be old while callbacks report that
        // the static image is still current. Never restart only for pixel age.
        Validate.Invoke(null, [frame.StreamStatus, frame.CallbackAgeMs]);
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("suspended")]
    [InlineData("stopped")]
    public void UnavailableProducerCannotPresentCachedPixelsAsCurrent(string status)
    {
        var error = Assert.Throws<TargetInvocationException>(() => Validate.Invoke(null, [status, 0d]));
        Assert.IsType<IOException>(error.InnerException);
    }

    [Theory]
    [InlineData("complete", 5_001)]
    [InlineData("idle", 5_001)]
    [InlineData("complete", -1)]
    [InlineData("idle", double.NaN)]
    [InlineData("complete", double.PositiveInfinity)]
    public void MissingOrInvalidProducerHeartbeatRequiresCaptureRecovery(string status, double callbackAgeMs)
    {
        var error = Assert.Throws<TargetInvocationException>(() => Validate.Invoke(null, [status, callbackAgeMs]));
        Assert.IsType<IOException>(error.InnerException);
    }

    [Fact]
    public void LegacyOneShotCaptureDoesNotRequireStreamMetadata()
    {
        Validate.Invoke(null, [null, 0d]);
    }
}
