using System.Text.Json;

namespace PaddiXiangqi.External;

/// <summary>A bounded JSON header followed by BGRA bytes. No image codec or base64 on the live path.</summary>
public static class ExternalPixelTransport
{
    public sealed record Header(ExternalWindow? Window, string? Error, int Width = 0, int Height = 0,
        int RowBytes = 0, int ByteCount = 0, long Sequence = 0, double FrameAgeMs = 0,
        double CallbackAgeMs = 0, string? StreamStatus = null);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static async Task<(Header Header, CapturedPixels? Pixels)> ReadAsync(Stream stream,
        CapturedPixels? previous, CancellationToken ct)
    {
        var header = new byte[8192];
        int length = 0;
        while (true)
        {
            if (length == header.Length) throw new IOException("截图数据头过大");
            await stream.ReadExactlyAsync(header.AsMemory(length, 1), ct).ConfigureAwait(false);
            if (header[length] == 10) break;
            length++;
        }
        var reply = JsonSerializer.Deserialize<Header>(header.AsSpan(0, length), Json)
            ?? throw new IOException("截图服务未返回数据");
        if (reply.Error != null) return (reply, null);
        long size = (long)reply.RowBytes * reply.Height;
        if (reply.Width <= 0 || reply.Height <= 0 || reply.RowBytes < (long)reply.Width * 4 ||
            size is <= 0 or > 268435456 || reply.ByteCount != 0 && reply.ByteCount != size)
            throw new IOException("截图像素尺寸无效");
        if (reply.ByteCount == 0)
        {
            if (previous == null || previous.Width != reply.Width || previous.Height != reply.Height || previous.RowBytes != reply.RowBytes)
                throw new IOException("截图服务缺少首帧");
            return (reply, previous);
        }
        var bytes = GC.AllocateUninitializedArray<byte>(reply.ByteCount);
        await stream.ReadExactlyAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
        return (reply, new(reply.Width, reply.Height, reply.RowBytes, bytes));
    }
}
