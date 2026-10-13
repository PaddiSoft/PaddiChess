using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;

namespace PaddiXiangqi.External;

/// <summary>Application entry point opts into process isolation; embedded/test hosts stay injectable.</summary>
public static class RecognitionHost
{
    public static Func<IExternalPositionRecognizer> Create { get; set; } = () => new ExternalPositionRecognizer();
    public static Task PrepareAsync(IExternalPositionRecognizer recognizer) => recognizer is RecognitionWorkerClient worker
        ? worker.PrepareAsync() : RecognitionAcceleration.PrepareAsync();
}

public sealed record RecognitionWorkerRequest(BoardCalibration? Geometry, bool RedToMove, bool DetectOrientation,
    int Width = 0, int Height = 0, int RowBytes = 0, bool GlyphsOnly = false);
public sealed record RecognitionWorkerReply(SkinRecognition? Read, RecognitionDeviceStatus[] Devices, string? Error = null);

/// <summary>A length-delimited control message; pixels use raw BGRA to avoid PNG/base64 latency.</summary>
public static class RecognitionWorkerProtocol
{
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > 4 * 1024 * 1024) throw new IOException("识别消息过大");
        var size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length);
        await stream.WriteAsync(size, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var size = new byte[4]; await stream.ReadExactlyAsync(size, ct).ConfigureAwait(false);
        var count = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (count is <= 0 or > 4 * 1024 * 1024) throw new IOException("识别消息长度无效");
        var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new IOException("识别消息为空");
    }

    public static async Task ServeAsync(Stream input, Stream output, CancellationToken ct = default)
    {
        var recognizer = new ExternalPositionRecognizer();
        while (!ct.IsCancellationRequested)
        {
            RecognitionWorkerRequest request;
            try { request = await ReadAsync<RecognitionWorkerRequest>(input, ct).ConfigureAwait(false); }
            catch (EndOfStreamException) { return; }
            SkinRecognition? read = null;
            string? error = null;
            try
            {
                if (request.Geometry is null) await RecognitionAcceleration.EnsureReadyAsync().ConfigureAwait(false);
                else
                {
                    var size = (long)request.Height * request.RowBytes;
                    if (request.Width <= 0 || request.Height <= 0 || request.RowBytes < (long)request.Width * 4
                        || size is <= 0 or > 268435456) throw new IOException("识别像素尺寸无效");
                    var pixels = new CapturedPixels(request.Width, request.Height, request.RowBytes, new byte[(int)size]);
                    await input.ReadExactlyAsync(pixels.Bgra, ct).ConfigureAwait(false);
                    var frame = new ExternalFrame(new ExternalWindow(0, 0, "recognition", 0, 0, request.Width, request.Height), pixels);
                    read = request.GlyphsOnly
                        ? await BoardGlyphRecognizer.RecognizeAsync(frame, request.Geometry, request.RedToMove, ct,
                            request.DetectOrientation).ConfigureAwait(false)
                        : await recognizer.ReadAsync(frame, request.Geometry, request.RedToMove, ct,
                            request.DetectOrientation, null, null).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { error = "识别进程失败（" + ex.GetType().Name + "）"; }
            await WriteAsync(output, new RecognitionWorkerReply(read, RecognitionAcceleration.Status.ToArray(), error), ct).ConfigureAwait(false);
        }
    }
}

/// <summary>One persistent worker owns recognition models and caches. Cancellation kills native hangs and the next request restarts on CPU.</summary>
public sealed class RecognitionWorkerClient(Func<bool, ProcessStartInfo> startInfo, TimeSpan? timeout = null)
    : IExternalPositionRecognizer, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private bool _cpu;
    private bool _disposed;
    public int Starts { get; private set; }
    public int Restarts { get; private set; }

    public static ProcessStartInfo ApplicationStartInfo(string executable, string? assembly, bool cpu)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (assembly != null) info.ArgumentList.Add(assembly);
        info.ArgumentList.Add("--recognition-worker");
        if (cpu) info.Environment["PADDI_RECOGNITION_DEVICE"] = "cpu";
        return info;
    }

    public async Task PrepareAsync()
    {
        try { await ExchangeAsync(new(null, false, false), null, _lifetime.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or InvalidOperationException)
        { RecognitionAcceleration.Report(new("识别进程", "等待重试", "后台预热未完成，识别时自动重启。")); }
    }

    public Task<SkinRecognition?> ReadAsync(ExternalFrame frame, BoardCalibration geometry, bool redToMove,
        CancellationToken ct, bool detectOrientation, BoardSkin? sessionSkin, string? customPath)
    {
        var pixels = frame.Pixels ?? CapturedPixels.DecodePng(frame.Png);
        return ExchangeAsync(new(geometry, redToMove, detectOrientation, pixels.Width, pixels.Height, pixels.RowBytes), pixels, ct);
    }

    public async Task<SkinRecognition> ReadGlyphsAsync(ExternalFrame frame, BoardCalibration geometry, bool redToMove,
        CancellationToken ct, bool detectOrientation)
    {
        var pixels = frame.Pixels ?? CapturedPixels.DecodePng(frame.Png);
        return await ExchangeAsync(new(geometry, redToMove, detectOrientation, pixels.Width, pixels.Height,
            pixels.RowBytes, GlyphsOnly: true), pixels, ct).ConfigureAwait(false)
            ?? throw new IOException("识字进程未返回局面");
    }

    private async Task<SkinRecognition?> ExchangeAsync(RecognitionWorkerRequest request, CapturedPixels? pixels, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        // A cancelled queued request must not kill another request's active worker.
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
            if (_process is null || _process.HasExited)
            {
                Stop();
                _process = Process.Start(startInfo(_cpu)) ?? throw new IOException("无法启动识别进程");
                Starts++;
                _ = _process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            }
            // Kill closes blocked pipe I/O even if the native runtime ignores its own cancellation.
            using var registration = linked.Token.Register(() => { try { _process?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
            await RecognitionWorkerProtocol.WriteAsync(_process.StandardInput.BaseStream, request, linked.Token).ConfigureAwait(false);
            if (pixels != null)
            {
                pixels.Validate();
                await _process.StandardInput.BaseStream.WriteAsync(pixels.Bgra, linked.Token).ConfigureAwait(false);
                await _process.StandardInput.BaseStream.FlushAsync(linked.Token).ConfigureAwait(false);
            }
            var reply = await RecognitionWorkerProtocol.ReadAsync<RecognitionWorkerReply>(_process.StandardOutput.BaseStream, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            foreach (var device in reply.Devices) RecognitionAcceleration.Report(device);
            if (reply.Error != null) throw new IOException(reply.Error);
            RecognitionAcceleration.Clear("识别进程");
            return reply.Read;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            Stop(); _cpu = true; Restarts++;
            if (ct.IsCancellationRequested || _lifetime.IsCancellationRequested) throw new OperationCanceledException(linked.Token);
            if (linked.IsCancellationRequested) throw new TimeoutException("识别进程超时，已终止；下次使用 CPU 重启。");
            throw;
        }
        finally { _gate.Release(); }
    }

    private void Stop()
    {
        if (_process == null) return;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        _process.Dispose(); _process = null;
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; Stop(); }
        finally { _gate.Release(); }
    }
}
