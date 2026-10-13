using System.Diagnostics;
using System.Reflection;

namespace PaddiXiangqi.External;

/// <summary>Composite window capture and XTest foreground input on Linux X11.</summary>
public sealed class X11ExternalDesktop(Func<ProcessStartInfo>? start = null) : IExternalDesktop
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _worker;
    private bool _escape;
    private long _sequence;
    public static bool Available => OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
        && !string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);
    public ExternalInputMode InputMode { get; set; }
    public ExternalInputDelivery InputDelivery { get; set; }
    public bool EscapePressed() => _escape;
    private static ProcessStartInfo DefaultStart()
    {
        var executable = Environment.ProcessPath!;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable) == "dotnet") info.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        info.ArgumentList.Add("--x11-worker"); return info;
    }
    private async Task<(X11Reply Reply, CapturedPixels? Pixels)> RequestAsync(X11Request request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_worker == null || _worker.HasExited)
            {
                Stop(); _worker = Process.Start(start?.Invoke() ?? DefaultStart()) ?? throw new IOException("X11 服务启动失败");
                _ = _worker.StandardError.BaseStream.CopyToAsync(Stream.Null);
            }
            // Once a move is submitted, let its short finally release the button before
            // cancellation disposes the worker. Escape is checked inside the sequence.
            using var timeout = request.Action == "move" ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await RecognitionWorkerProtocol.WriteAsync(_worker.StandardInput.BaseStream, request, timeout.Token).ConfigureAwait(false);
            var reply = await RecognitionWorkerProtocol.ReadAsync<X11Reply>(_worker.StandardOutput.BaseStream, timeout.Token).ConfigureAwait(false);
            _escape = reply.Escape;
            if (reply.Error != null)
            {
                if (request.Action == "move") throw new ExternalInputBlockedException(reply.InputStarted, reply.Error);
                throw new IOException(reply.Error);
            }
            CapturedPixels? pixels = null;
            if (reply.Window is { } window && reply.Bytes > 0)
            {
                if (reply.Bytes > 268435456 || window.Height <= 0 || window.Height > 32768 || window.Width <= 0
                    || reply.Bytes % (int)window.Height != 0) throw new IOException("X11 像素尺寸无效");
                var bytes = new byte[reply.Bytes]; await _worker.StandardOutput.BaseStream.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
                pixels = new((int)window.Width, (int)window.Height, reply.Bytes / (int)window.Height, bytes); pixels.Validate();
            }
            ct.ThrowIfCancellationRequested();
            return (reply, pixels);
        }
        catch (ExternalInputBlockedException) { throw; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        {
            Stop();
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (request.Action == "move") throw new ExternalInputBlockedException(true, "X11 输入通信中断，先核验棋盘后继续");
            throw;
        }
        finally { _gate.Release(); }
    }
    public async Task<IReadOnlyList<ExternalWindow>> ListAsync(CancellationToken ct) => (await RequestAsync(new("list"), ct).ConfigureAwait(false)).Reply.Windows ?? [];
    public async Task<ExternalFrame> CaptureAsync(ExternalWindow window, CancellationToken ct)
    {
        var result = await RequestAsync(new("capture", window), ct).ConfigureAwait(false);
        return new(result.Reply.Window!, result.Pixels ?? throw new IOException("X11 未返回图像")) { Sequence = ++_sequence, StreamStatus = "x11-composite" };
    }
    public async Task MoveAsync(ExternalWindow window, double fromX, double fromY, double toX, double toY, CancellationToken ct)
    {
        if (InputDelivery != ExternalInputDelivery.SystemCursor) throw new ExternalInputBlockedException(false, "Linux X11 当前支持系统鼠标输入，请选择兼容模式。");
        await RequestAsync(new("move", window, fromX, fromY, toX, toY, InputMode == ExternalInputMode.Drag), ct).ConfigureAwait(false);
    }
    private void Stop()
    {
        if (_worker == null) return;
        try { if (!_worker.HasExited) _worker.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _worker.Dispose(); _worker = null;
    }
    public async Task CloseCaptureAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { Stop(); } finally { _gate.Release(); }
    }
}
