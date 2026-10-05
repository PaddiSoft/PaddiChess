using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public class ExternalCaptureGeometryTests
{
    private static ExternalFrame Frame(ExternalWindow window, int width, int height) =>
        new(window, new CapturedPixels(width, height, width * 4, new byte[width * height * 4]));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetinaSwitchPreservesLogicalClickForEverySquare(bool flipped)
    {
        var target = new ExternalWindow(12, 34, "fixture", -1920, 80, 480, 530);
        var before = Frame(target, 480, 530);
        var after = Frame(target with { X = 150, Y = -180 }, 960, 1060);
        var geometry = new BoardCalibration(40, 40, 440, 490, flipped);
        Assert.True(ExternalCaptureGeometry.TryRebaseCalibration(before, after, geometry, out var mapped));
        Assert.Equal(new BoardCalibration(80, 80, 880, 980, flipped), mapped);
        for (var rank = 0; rank < 10; rank++) for (var file = 0; file < 9; file++)
        {
            var point = geometry.Point(new Square(file, rank));
            var next = mapped.Point(new Square(file, rank));
            Assert.Equal(point, ExternalCaptureGeometry.ToWindowPoint(next.X, next.Y, 960, 1060, after.Window));
        }
        Assert.True(ExternalCaptureGeometry.TryRebaseCalibration(after, before, mapped, out var roundTrip));
        Assert.Equal(geometry, roundTrip);
        Assert.False(before.PngEncoded); Assert.False(after.PngEncoded);
    }

    [Fact]
    public void RealResizeOrDifferentWindowCannotReuseCalibration()
    {
        var target = new ExternalWindow(12, 34, "fixture", 0, 0, 480, 530);
        var before = Frame(target, 480, 530);
        var geometry = new BoardCalibration(40, 40, 440, 490, false);
        foreach (var wrong in new[] { target with { Width = 481 }, target with { Height = 530.5 },
                     target with { Id = 13 }, target with { Pid = 35 }, target with { Width = double.NaN } })
            Assert.False(ExternalCaptureGeometry.TryRebaseCalibration(before, Frame(wrong, 960, 1060), geometry, out _));
    }

    [Fact]
    public void PngMetadataAndUnchangedRawFrameKeepExistingCalibration()
    {
        var target = new ExternalWindow(12, 34, "fixture", 0, 0, 480, 530);
        var png = new ExternalFrame(target, ExternalBoardTests.Render(new XiangqiGame()));
        var raw = Frame(target, 480, 530);
        var geometry = new BoardCalibration(40, 40, 440, 490, true);
        Assert.True(ExternalCaptureGeometry.TryRebaseCalibration(png, raw, geometry, out var mapped));
        Assert.Same(geometry, mapped);
        Assert.False(raw.PngEncoded);
        Assert.False(ExternalCaptureGeometry.TryRebaseCalibration(raw, raw,
            geometry with { Left = -20 }, out _));
    }
}
