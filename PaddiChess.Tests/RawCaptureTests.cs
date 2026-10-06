using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using SkiaSharp;

namespace PaddiXiangqi.Tests;

public class RawCaptureTests
{
    internal static ExternalFrame RawFrame(byte[] png, ExternalWindow target, int padding = 0)
    {
        using var decoded = SKBitmap.Decode(png);
        using var bitmap = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap)) canvas.DrawBitmap(decoded, 0, 0);
        var stride = bitmap.Width * 4 + padding;
        var bytes = new byte[stride * bitmap.Height];
        for (int y = 0; y < bitmap.Height; y++) Marshal.Copy(bitmap.GetPixels() + bitmap.RowBytes * y, bytes, y * stride, bitmap.Width * 4);
        // PrintWindow's DIB alpha channel can be zero even though RGB pixels are valid.
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++) bytes[y * stride + x * 4 + 3] = 0;
        return new(target, new CapturedPixels(bitmap.Width, bitmap.Height, stride, bytes));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 64)]
    public async Task RawBgraPreservesColourOrientationAndLiftedSamplesWithoutEncoding(bool flipped, int padding)
    {
        var png = ExternalBoardTests.Render(new XiangqiGame(), flipped);
        var frame = RawFrame(png, new(1, 1, "pixels", 0, 0, 480, 530), padding);
        var geometry = new BoardCalibration(40, 40, 440, 490, flipped);
        var reader = new ExternalObservationReader(geometry);
        var raw = await reader.ReadAsync(frame, default);
        var encoded = BoardObservation.Read(png, geometry);
        Assert.All(Enumerable.Range(0, 90), i => Assert.Equal(encoded.Cells[i], raw.Cells[i]));
        for (int i = 0; i < 90; i++) for (int v = 0; v < raw.LiftedCells[i].Length; v++)
            Assert.Equal(encoded.LiftedCells[i][v], raw.LiftedCells[i][v]);
        Assert.Same(raw, await reader.ReadAsync(frame with { Sequence = 2 }, default));
        Assert.False(frame.PngEncoded);
        var roundTrip = BoardObservation.Read(frame.Png, geometry);
        Assert.True(frame.PngEncoded);
        Assert.All(Enumerable.Range(0, 90), i => Assert.Equal(encoded.Cells[i], roundTrip.Cells[i]));
        Assert.Null((frame with { Png = png }).Pixels); // Replacing a picture must invalidate the old raw payload.
    }

    private static byte[] Header(int bytes, int width = 2) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        window = new ExternalWindow(1, 2, "fixture", 0, 0, 2, 2), width, height = 2, rowBytes = 8,
        byteCount = bytes, streamStatus = "complete", sequence = 1
    }) + "\n");

    [Fact]
    public async Task BinaryTransportKeepsPacketBoundariesAndReusesOnlyAValidPreviousFrame()
    {
        byte[] pixels = [13, 10, 0, 255, 123, 125, 0, 255, 10, 13, 1, 255, 2, 3, 4, 255];
        using var stream = new MemoryStream([..Header(16), ..pixels, ..Header(0)]);
        var first = await ExternalPixelTransport.ReadAsync(stream, null, default);
        Assert.Equal(pixels, first.Pixels!.Bgra);
        var repeated = await ExternalPixelTransport.ReadAsync(stream, first.Pixels, default);
        Assert.Same(first.Pixels, repeated.Pixels);
        Assert.Equal(stream.Length, stream.Position);
        using var missing = new MemoryStream(Header(0));
        await Assert.ThrowsAsync<IOException>(() => ExternalPixelTransport.ReadAsync(missing, null, default));
    }

    [Fact]
    public async Task TruncatedOversizedOrCancelledCaptureDoesNotPublishStalePixels()
    {
        using var truncated = new MemoryStream([..Header(16), 1, 2]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => ExternalPixelTransport.ReadAsync(truncated, null, default));
        using var badSize = new MemoryStream(Header(16, int.MaxValue));
        await Assert.ThrowsAsync<IOException>(() => ExternalPixelTransport.ReadAsync(badSize, null, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var valid = new MemoryStream([..Header(16), ..new byte[16]]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExternalPixelTransport.ReadAsync(valid, null, cancellation.Token));
    }

    [Fact]
    public async Task BackingScaleChangeRequiresFreshPixelsEvenWhenSequenceRestarts()
    {
        var previous = new CapturedPixels(2, 2, 8, new byte[16]);
        byte[] resizedHeader(int count) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            window = new ExternalWindow(1, 2, "fixture", 0, 0, 2, 2), width = 4, height = 4,
            rowBytes = 16, byteCount = count, sequence = 1, streamStatus = "complete"
        }) + "\n");
        using var missing = new MemoryStream(resizedHeader(0));
        await Assert.ThrowsAsync<IOException>(() => ExternalPixelTransport.ReadAsync(missing, previous, default));
        var pixels = Enumerable.Repeat((byte)127, 64).ToArray();
        using var complete = new MemoryStream([..resizedHeader(64), ..pixels]);
        var next = await ExternalPixelTransport.ReadAsync(complete, previous, default);
        Assert.NotSame(previous, next.Pixels);
        Assert.Equal(4, next.Pixels!.Width); Assert.Equal(4, next.Pixels.Height);
        Assert.Equal(pixels, next.Pixels.Bgra);
    }
}
