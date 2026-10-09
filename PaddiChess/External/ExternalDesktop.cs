using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using SkiaSharp;

namespace PaddiXiangqi.External;

public sealed record ExternalWindow(long Id, int Pid, string Title, double X, double Y, double Width, double Height)
{
    // macOS includes live windows on another Space. Availability of their pixels
    // is checked by the capture stream, independently of this inventory flag.
    public bool OnScreen { get; init; } = true;
    public override string ToString() => Title;
    public bool SameBounds(ExternalWindow other) => Id == other.Id && Pid == other.Pid &&
        Math.Abs(X - other.X) < 1 && Math.Abs(Y - other.Y) < 1 &&
        Math.Abs(Width - other.Width) < 1 && Math.Abs(Height - other.Height) < 1;
}
public sealed record CapturedPixels(int Width, int Height, int RowBytes, byte[] Bgra)
{
    public static CapturedPixels DecodePng(byte[] png)
    {
        using var decoded = SKBitmap.Decode(png) ?? throw new InvalidOperationException("截图无法读取");
        using var bitmap = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap)) canvas.DrawBitmap(decoded, 0, 0);
        var bytes = new byte[checked(bitmap.RowBytes * bitmap.Height)];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        return new(bitmap.Width, bitmap.Height, bitmap.RowBytes, bytes);
    }
    public SKBitmap ToBitmap()
    {
        Validate();
        var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        for (int row = 0; row < Height; row++)
            Marshal.Copy(Bgra, row * RowBytes, bitmap.GetPixels() + row * bitmap.RowBytes, Width * 4);
        return bitmap;
    }
    public void Validate()
    {
        if (Width <= 0 || Height <= 0 || RowBytes < Width * 4L || (long)RowBytes * Height != Bgra.LongLength)
            throw new InvalidOperationException("截图像素尺寸无效");
    }
    public byte[] EncodePng()
    {
        using var image = ToBitmap();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
public sealed record ExternalFrame
{
    private byte[]? _png;
    public ExternalWindow Window { get; init; }
    public CapturedPixels? Pixels { get; private init; }
    public bool PngEncoded => _png != null;
    public byte[] Png { get => _png ??= Pixels!.EncodePng(); init { _png = value; Pixels = null; } }
    public ExternalFrame(ExternalWindow window, byte[] png) { Window = window; _png = png; }
    public ExternalFrame(ExternalWindow window, CapturedPixels pixels) { pixels.Validate(); Window = window; Pixels = pixels; }
    public long Sequence { get; init; }
    public double FrameAgeMs { get; init; }
    public double CallbackAgeMs { get; init; }
    public string? StreamStatus { get; init; }
}

public sealed record ExternalPermissions(bool ScreenCapture, bool Accessibility)
{
    public bool Ready => ScreenCapture && Accessibility;
}
public enum ExternalPermission { ScreenCapture, Accessibility }
public sealed class ExternalPermissionException(ExternalPermission permission)
    : Exception(permission == ExternalPermission.ScreenCapture ? "屏幕录制权限尚未授权，请在接管页授权后重试。" : "辅助功能权限尚未授权，请在接管页授权后继续。")
{
    public ExternalPermission Permission { get; } = permission;
}
public sealed class ExternalInputBlockedException(bool inputStarted, string message) : Exception(message)
{
    // Only false guarantees that retrying the entire move cannot duplicate a partial submission.
    public bool InputStarted { get; } = inputStarted;
}
public enum ExternalInputMode { Click, Drag }
public enum ExternalInputDelivery { SystemCursor, TargetWindow, TargetWindowFocused }

public interface IExternalDesktop
{
    Task<IReadOnlyList<ExternalWindow>> ListAsync(CancellationToken ct);
    Task<ExternalFrame> CaptureAsync(ExternalWindow window, CancellationToken ct);
    Task MoveAsync(ExternalWindow window, double fromX, double fromY, double toX, double toY, CancellationToken ct);
    bool CanCompleteSelectedMove => false;
    Task CompleteSelectedMoveAsync(ExternalWindow window, double fromX, double fromY, double toX, double toY, CancellationToken ct)
        => throw new NotSupportedException("此输入方式不支持补发已选中棋子的终点。");
    bool EscapePressed();
    ExternalInputMode InputMode { get => ExternalInputMode.Click; set { } }
    ExternalInputDelivery InputDelivery { get => ExternalInputDelivery.SystemCursor; set { } }
    Task CloseCaptureAsync() => Task.CompletedTask;
    Task<ExternalPermissions> GetPermissionsAsync(CancellationToken ct) => Task.FromResult(new ExternalPermissions(true,true));
    Task RequestPermissionAsync(ExternalPermission permission, CancellationToken ct) => Task.CompletedTask;
}

public static class ExternalDesktop
{
    public static bool Supported => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();
    public static IExternalDesktop Create() => OperatingSystem.IsMacOS() ? new MacExternalDesktop()
        : OperatingSystem.IsWindows() ? new WindowsExternalDesktop()
        : throw new PlatformNotSupportedException("外部窗口接管目前支持 macOS 和 Windows；Linux 版暂未接入屏幕捕获与输入授权。 ");
}

internal sealed class MacExternalDesktop : IExternalDesktop
{
    public bool CanCompleteSelectedMove => true;
    public ExternalInputMode InputMode { get; set; }
    public ExternalInputDelivery InputDelivery { get; set; }
    public async Task<ExternalPermissions> GetPermissionsAsync(CancellationToken ct) =>
        JsonSerializer.Deserialize<ExternalPermissions>(await RunAsync(ct, "permissions"), Json) ?? new(false, false);
    public async Task RequestPermissionAsync(ExternalPermission permission, CancellationToken ct)
        => await RunAsync(ct, permission == ExternalPermission.ScreenCapture ? "request-screen" : "request-accessibility");
    private static void ThrowNativeError(string message)
    {
        if (message.Contains("permission:screen",StringComparison.Ordinal)) throw new ExternalPermissionException(ExternalPermission.ScreenCapture);
        if (message.Contains("permission:accessibility",StringComparison.Ordinal)) throw new ExternalPermissionException(ExternalPermission.Accessibility);
        const string blockedPrefix = "input-blocked:";
        if (message.StartsWith(blockedPrefix, StringComparison.Ordinal))
        {
            try
            {
                using var json = JsonDocument.Parse(message[blockedPrefix.Length..]);
                var root = json.RootElement;
                // Malformed/old helper responses must never be interpreted as safe-to-retry.
                if (root.TryGetProperty("inputStarted", out var started) && started.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    root.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String)
                    throw new ExternalInputBlockedException(started.GetBoolean(), text.GetString()!);
            }
            catch (JsonException) { }
        }
        throw new InvalidOperationException(message);
    }
    private Process? _capture;
    private CapturedPixels? _lastPixels;
    private Stream? _captureOutput;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private static void ValidateCaptureState(string? status, double callbackAgeMs)
    {
        // Legacy one-shot captures do not expose heartbeat metadata. New persistent
        // capture may reuse old pixels on idle, but must never reuse an unavailable
        // or silently stopped producer's board to authorize a mouse submission.
        if (status is "blank" or "suspended" or "stopped")
            throw new IOException("窗口捕获暂不可用，正在自动恢复；棋局与待确认落子已保留。");
        if (status != null && (!double.IsFinite(callbackAgeMs) || callbackAgeMs < 0 || callbackAgeMs > 5000))
            throw new IOException("窗口捕获心跳超时，正在自动恢复；棋局与待确认落子已保留。");
    }
    public async Task CloseCaptureAsync()
    {
        await _captureGate.WaitAsync();
        try { StopCapture(); } finally { _captureGate.Release(); }
    }
    private void StopCapture()
    {
        _lastPixels = null; _captureOutput?.Dispose(); _captureOutput = null;
        if (_capture == null) return;
        try { if (!_capture.HasExited) _capture.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        _capture.Dispose(); _capture = null;
    }
    private async Task<ExternalFrame> CapturePersistentAsync(ExternalWindow window, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        await _captureGate.WaitAsync(timeout.Token);
        try
        {
            if (_capture == null || _capture.HasExited)
            {
                StopCapture();
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Native", "PaddiBridge"))
                { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                start.ArgumentList.Add("capture-server-raw");
                _capture = Process.Start(start) ?? throw new InvalidOperationException("无法启动截图服务");
                _captureOutput = new BufferedStream(_capture.StandardOutput.BaseStream, 65536);
                _ = _capture.StandardError.ReadToEndAsync();
            }
            await _capture.StandardInput.WriteLineAsync(window.Id.ToString().AsMemory(), timeout.Token);
            await _capture.StandardInput.FlushAsync(timeout.Token);
            var (reply, pixels) = await ExternalPixelTransport.ReadAsync(_captureOutput!, _lastPixels, timeout.Token);
            if (reply.Error != null) ThrowNativeError(reply.Error);
            ValidateCaptureState(reply.StreamStatus, reply.CallbackAgeMs);
            if (reply.Window?.Pid != window.Pid || reply.Window.Id != window.Id || pixels == null)
                throw new InvalidOperationException("目标窗口已替换，请重新选择");
            _lastPixels = pixels;
            return new(reply.Window, pixels)
            { Sequence = reply.Sequence, FrameAgeMs = reply.FrameAgeMs, CallbackAgeMs = reply.CallbackAgeMs, StreamStatus = reply.StreamStatus };
        }
        catch { StopCapture(); throw; }
        finally { _captureGate.Release(); }
    }
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static Task<string> RunAsync(CancellationToken ct, params string[] args) => RunHelperAsync(ct, args);
    private static async Task<string> RunHelperAsync(CancellationToken ct, string[] args, Action? forcedTermination = null)
    {
        ct.ThrowIfCancellationRequested();
        var path = Path.Combine(AppContext.BaseDirectory, "Native", "PaddiBridge");
        if (!File.Exists(path)) throw new FileNotFoundException("未找到屏幕接管组件，请重新编译或安装完整客户端。", path);
        var start = new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动屏幕接管组件");
        var input = args.FirstOrDefault() == "move";
        using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) { if (input) kill(process.Id, 15); else process.Kill(true); } }
            catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Allow the helper to release a held button and restore the temporary
            // background AppKit context before resorting to a hard termination.
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                try
                {
                    if (!process.HasExited) { process.Kill(true); forcedTermination?.Invoke(); }
                }
                catch (InvalidOperationException) { } // The helper exited during the timeout race.
            }
            throw;
        }
        var result = await output.ConfigureAwait(false);
        var message = await error.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) ThrowNativeError(message.Trim());
        return result;
    }
    public async Task<IReadOnlyList<ExternalWindow>> ListAsync(CancellationToken ct) =>
        JsonSerializer.Deserialize<ExternalWindow[]>(await RunAsync(ct, "list"), Json) ?? [];
    public async Task<ExternalFrame> CaptureAsync(ExternalWindow window, CancellationToken ct)
    {
        if (OperatingSystem.IsMacOSVersionAtLeast(14)) return await CapturePersistentAsync(window, ct);
        var path = Path.Combine(Path.GetTempPath(), $"paddi-frame-{Guid.NewGuid():N}.png");
        try
        {
            var current = JsonSerializer.Deserialize<ExternalWindow>(await RunAsync(ct, "capture", window.Id.ToString(), path), Json)
                ?? throw new InvalidOperationException("无法读取窗口信息");
            if (current.Pid != window.Pid) throw new InvalidOperationException("窗口已经替换，请重新选择");
            return new ExternalFrame(current, await File.ReadAllBytesAsync(path, ct));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    public Task MoveAsync(ExternalWindow window, double fromX, double fromY, double toX, double toY, CancellationToken ct)
        => MoveCoreAsync(window, fromX, fromY, toX, toY, ct, false);
    public Task CompleteSelectedMoveAsync(ExternalWindow window, double fromX, double fromY, double toX, double toY, CancellationToken ct)
        => MoveCoreAsync(window, fromX, fromY, toX, toY, ct, true);
    private async Task MoveCoreAsync(ExternalWindow window, double fromX, double fromY, double toX, double toY, CancellationToken ct, bool finishSelection)
    {
        string F(double value) => value.ToString(CultureInfo.InvariantCulture);
        var forcedTermination = false;
        var inputMode = InputMode;
        var inputDelivery = InputDelivery;
        try
        {
            await RunHelperAsync(ct, ["move", window.Id.ToString(), F(window.X), F(window.Y), F(window.Width), F(window.Height),
                F(window.X + fromX), F(window.Y + fromY), F(window.X + toX), F(window.Y + toY), finishSelection ? "finish-click" : inputMode == ExternalInputMode.Drag ? "drag" : "click",
                inputDelivery switch
                {
                    ExternalInputDelivery.TargetWindow => "window",
                    ExternalInputDelivery.TargetWindowFocused => "window-focused",
                    _ => "cursor"
                }], () => forcedTermination = true);
        }
        finally
        {
            // A killed helper cannot run Swift cleanup. Release an interrupted window
            // button at its origin so stopping cannot finish a drag at the destination.
            // Cooperative SIGTERM and all normal native errors already release the
            // source in Swift. A second synthetic up can toggle a selection; use the
            // emergency fallback only if the helper actually required SIGKILL.
            if (forcedTermination)
            {
                var current = CGEventCreate(0);
                if (current != 0)
                {
                    var point = inputDelivery is ExternalInputDelivery.TargetWindow or ExternalInputDelivery.TargetWindowFocused
                        ? new CGPoint { X = window.X + fromX, Y = window.Y + fromY } : CGEventGetLocation(current);
                    CFRelease(current);
                    var up = CGEventCreateMouseEvent(0, 2, point, 0);
                    if (up != 0)
                    {
                        if (inputDelivery is ExternalInputDelivery.TargetWindow or ExternalInputDelivery.TargetWindowFocused)
                        {
                            try
                            {
                                CGEventSetIntegerValueField(up, 51, window.Id);
                                CGEventSetWindowLocation(up, new CGPoint { X = fromX, Y = fromY });
                                CGEventSetIntegerValueField(up, 91, window.Id);
                                CGEventSetIntegerValueField(up, 92, window.Id);
                                CGEventPostToPid(window.Pid, up);
                            }
                            catch (EntryPointNotFoundException) { /* The helper rejected this mode before input. */ }
                        }
                        else CGEventPost(0, up);
                        CFRelease(up);
                    }
                }
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [DllImport("libSystem.B.dylib")] private static extern int kill(int pid, int signal);
    [DllImport(CG)] private static extern nint CGEventCreate(nint source);
    [DllImport(CG)] private static extern CGPoint CGEventGetLocation(nint ev);
    [DllImport(CG)] private static extern nint CGEventCreateMouseEvent(nint source, int type, CGPoint point, int button);
    [DllImport(CG)] private static extern void CGEventPost(int tap, nint ev);
    [DllImport(CG)] private static extern void CGEventPostToPid(int pid, nint ev);
    [DllImport(CG)] private static extern void CGEventSetIntegerValueField(nint ev, int field, long value);
    [DllImport(CG)] private static extern void CGEventSetWindowLocation(nint ev, CGPoint point);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(nint obj);
    public bool EscapePressed() => CGEventSourceKeyState(0, 53);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGEventSourceKeyState(int state, ushort key);
}

internal sealed class WindowsExternalDesktop : IExternalDesktop
{
    public bool CanCompleteSelectedMove => true;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private WindowCaptureSurface? _captureSurface;
    public ExternalInputMode InputMode { get; set; }
    public Task<IReadOnlyList<ExternalWindow>> ListAsync(CancellationToken ct) => Task.Run<IReadOnlyList<ExternalWindow>>(() =>
    {
        using var dpi = PhysicalPixelScope.Enter();
        var result = new List<ExternalWindow>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle)) return true;
            var title = new System.Text.StringBuilder(512);
            GetWindowText(handle, title, title.Capacity);
            ExternalWindow target;
            try { target = Describe(handle); } catch (InvalidOperationException) { return true; }
            if (title.Length > 0 && target.Width > 180 && target.Height > 180 && target.Pid != Environment.ProcessId)
                result.Add(target with { Title = title.ToString() });
            return true;
        }, 0);
        ct.ThrowIfCancellationRequested();
        return result;
    }, ct);
    private static ExternalWindow Describe(nint handle)
    {
        using var dpi = PhysicalPixelScope.Enter();
        if (!IsWindow(handle) || IsIconic(handle) || !GetWindowRect(handle, out var bounds))
            throw new InvalidOperationException("目标窗口已关闭或最小化");
        GetWindowThreadProcessId(handle, out var pid);
        return new(handle, (int)pid, "", bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }
    public async Task<ExternalFrame> CaptureAsync(ExternalWindow window, CancellationToken ct)
    {
        await _captureGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                // GetWindowRect is DPI-virtualized unless the calling thread opts into
                // physical pixels. Keep it in the same coordinate space as PrintWindow,
                // the captured BGRA buffer and the eventual screen mouse coordinates.
                using var dpi = PhysicalPixelScope.Enter();
                var current = Describe((nint)window.Id) with { Title = window.Title };
                if (current.Pid != window.Pid) throw new InvalidOperationException("窗口已经替换");
                int width = (int)current.Width, height = (int)current.Height;
                if (width <= 0 || height <= 0 || width * (long)height > 40_000_000)
                    throw new InvalidOperationException("窗口分辨率无效或过大，请缩小窗口");
                if (IsHungAppWindow((nint)window.Id))
                    throw new IOException("目标窗口暂未响应，正在等待窗口恢复。");
                if (_captureSurface?.Matches(current, width, height) != true)
                {
                    _captureSurface?.Dispose(); _captureSurface = null;
                    _captureSurface = new WindowCaptureSurface(current, width, height);
                }
                var pixels = _captureSurface.Capture();
                ct.ThrowIfCancellationRequested();
                var after = Describe((nint)window.Id) with { Title = window.Title };
                if (after.Pid != window.Pid || Math.Abs(after.Width-current.Width) >= 1 || Math.Abs(after.Height-current.Height) >= 1)
                    throw new IOException("窗口尺寸正在变化，正在等待稳定画面。");
                // The reusable GDI surface is never exposed: each published frame owns
                // immutable bytes, so a following capture cannot overwrite recognition.
                return new ExternalFrame(after, pixels);
            }, ct).ConfigureAwait(false);
        }
        finally { _captureGate.Release(); }
    }
    public async Task CloseCaptureAsync()
    {
        await _captureGate.WaitAsync().ConfigureAwait(false);
        try { _captureSurface?.Dispose(); _captureSurface = null; }
        finally { _captureGate.Release(); }
    }
    private sealed class WindowCaptureSurface : IDisposable
    {
        private readonly long _window;
        private readonly int _pid, _width, _height;
        private nint _memory, _bitmap, _previous, _pixels;
        public WindowCaptureSurface(ExternalWindow window, int width, int height)
        {
            _window = window.Id; _pid = window.Pid; _width = width; _height = height;
            var dc = GetWindowDC((nint)_window);
            try
            {
                if (dc == 0) throw new IOException("无法读取目标窗口绘图上下文");
                _memory = CreateCompatibleDC(dc);
                var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
                _bitmap = CreateDIBSection(dc, ref info, 0, out _pixels, 0, 0);
                if (_memory == 0 || _bitmap == 0 || _pixels == 0) throw new IOException("无法创建窗口截图");
                _previous = SelectObject(_memory, _bitmap);
                if (_previous == 0 || _previous == -1) throw new IOException("无法准备窗口截图缓冲");
            }
            catch { Dispose(); throw; }
            finally { if (dc != 0) ReleaseDC((nint)_window, dc); }
        }
        public bool Matches(ExternalWindow window, int width, int height) =>
            window.Id == _window && window.Pid == _pid && width == _width && height == _height;
        public CapturedPixels Capture()
        {
            // Some targets return success after painting only part of their DC.
            // Never leave last frame's board in an unpainted area of a reused DIB.
            if (!PatBlt(_memory, 0, 0, _width, _height, 0x00000042)) // BLACKNESS
                throw new IOException("无法清理窗口截图缓冲");
            // GDI batches are per thread. Flush our clear before PrintWindow lets
            // the target's UI thread paint into this DC, or a later flush can
            // erase that fresh painting and publish a black/partial board.
            GdiFlush();
            if (!PrintWindow((nint)_window, _memory, 2))
                throw new IOException("此窗口暂时未提供截图，正在等待窗口恢复");
            GdiFlush(); // Complete batched GDI writes before reading DIB memory.
            var bytes = GC.AllocateUninitializedArray<byte>(checked(_width * _height * 4));
            Marshal.Copy(_pixels, bytes, 0, bytes.Length);
            return new CapturedPixels(_width, _height, _width * 4, bytes);
        }
        public void Dispose()
        {
            if (_memory != 0 && _previous != 0 && _previous != -1) SelectObject(_memory, _previous);
            if (_bitmap != 0) DeleteObject(_bitmap);
            if (_memory != 0) DeleteDC(_memory);
            _memory = _bitmap = _previous = _pixels = 0;
        }
    }
    private readonly struct PhysicalPixelScope(nint previous) : IDisposable
    {
        public static PhysicalPixelScope Enter()
        {
            // Thread-local and always restored before returning to Avalonia. Do not
            // change the UI process DPI mode or keep this scope across an await.
            try
            {
                var prior = SetThreadDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2
                if (prior == 0) prior = SetThreadDpiAwarenessContext(-3); // Windows 10 older builds
                return new(prior);
            }
            catch (EntryPointNotFoundException) { return default; }
        }
        public void Dispose() { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }
    public Task MoveAsync(ExternalWindow window, double fromX, double fromY, double toX, double toY, CancellationToken ct)
        => MoveCoreAsync(window, fromX, fromY, toX, toY, ct, false);
    public Task CompleteSelectedMoveAsync(ExternalWindow window, double fromX, double fromY, double toX, double toY, CancellationToken ct)
        => MoveCoreAsync(window, fromX, fromY, toX, toY, ct, true);
    private async Task MoveCoreAsync(ExternalWindow window, double fromX, double fromY, double toX, double toY, CancellationToken ct, bool finishSelection)
    {
        if (!window.SameBounds(Describe((nint)window.Id))) throw new InvalidOperationException("窗口位置或大小改变，请重新标定");
        if (GetForegroundWindow() != (nint)window.Id)
        {
            if (!SetForegroundWindow((nint)window.Id)) throw new ExternalInputBlockedException(finishSelection, "目标棋盘尚未位于前台，正在等待窗口可操作。");
            var activation = Stopwatch.StartNew();
            while (GetForegroundWindow() != (nint)window.Id && activation.ElapsedMilliseconds < 500)
                await Task.Delay(20, ct);
            if (GetForegroundWindow() != (nint)window.Id)
                throw new ExternalInputBlockedException(finishSelection, "目标棋盘尚未获得焦点，正在等待窗口可操作。");
            await Task.Delay(50, ct); // Let the target process its activation before selecting a piece.
        }
        var from = new Point((int)(window.X + fromX), (int)(window.Y + fromY));
        var to = new Point((int)(window.X + toX), (int)(window.Y + toY));
        var inputStarted = finishSelection;
        void Button(uint flags)
        {
            var events = new[] { new MouseInputEvent { Mouse = new MouseInput { Flags = flags } } };
            if (SendInput(1, events, Marshal.SizeOf<MouseInputEvent>()) != 1)
                throw new ExternalInputBlockedException(inputStarted,
                    "Windows 未接受模拟输入，请确认目标与本程序权限级别一致，或退出目标的管理员模式。");
        }
        void MovePointer(Point point)
        {
            using var dpi = PhysicalPixelScope.Enter();
            if (!SetCursorPos(point.X, point.Y))
                throw new ExternalInputBlockedException(inputStarted, "Windows 无法移动到落点，请检查当前桌面是否可交互。");
        }
        void Verify(Point point)
        {
            using var dpi = PhysicalPixelScope.Enter();
            ct.ThrowIfCancellationRequested();
            if (EscapePressed()) throw new OperationCanceledException(ct);
            if (!window.SameBounds(Describe((nint)window.Id))) throw new InvalidOperationException("目标窗口已移动");
            if (GetAncestor(WindowFromPoint(point), 2) != (nint)window.Id)
                throw new ExternalInputBlockedException(inputStarted, "棋盘落点当前由其他窗口接收鼠标，正在等待窗口可操作。");
        }
        // Both destinations must be usable before selecting or dragging a piece.
        Verify(from); Verify(to);
        if (InputMode == ExternalInputMode.Drag && !finishSelection)
        {
            Verify(from); MovePointer(from);
            await Task.Delay(50, ct); Verify(from);
            Button(2);
            inputStarted = true;
            var completed = false;
            try
            {
                for (int i = 1; i <= 10; i++)
                {
                    var point = new Point(from.X + (to.X - from.X) * i / 10, from.Y + (to.Y - from.Y) * i / 10);
                    Verify(point); MovePointer(point); await Task.Delay(18, ct);
                }
                completed = true;
            }
            finally
            {
                // Cancel an interrupted drag at its source while that source still
                // belongs to this window. Always release even if the window closed.
                if (!completed)
                {
                    try
                    {
                        using var dpi = PhysicalPixelScope.Enter();
                        if (window.SameBounds(Describe((nint)window.Id)) && GetAncestor(WindowFromPoint(from), 2) == (nint)window.Id)
                            SetCursorPos(from.X, from.Y);
                    }
                    catch (InvalidOperationException) { }
                }
                Button(4);
            }
        }
        else
        {
            if (!finishSelection)
            {
                await ExternalClickSequence.ClickAsync(() => MovePointer(from), () => Verify(from),
                    () => { Button(2); inputStarted = true; }, () => Button(4), ct);
                await Task.Delay(180, ct);
            }
            await ExternalClickSequence.ClickAsync(() => MovePointer(to), () => Verify(to),
                () => Button(2), () => Button(4), ct);
            // Confirmation is driven by captured frames, not a fixed delay after the destination.
        }
    }

    public bool EscapePressed() => (GetAsyncKeyState(0x1B) & 0x8000) != 0;
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int X, Y; public uint Used, Important; }
    private delegate bool EnumCallback(nint h, nint p);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(nint h);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint h, System.Text.StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetWindowDC(nint h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint h, nint dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint h, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point p);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint h, uint flags);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    // INPUT's union is MOUSEINPUT-sized; native alignment supplies the padding
    // after Type on x64. Do not pack it to the x86 layout.
    [StructLayout(LayoutKind.Sequential)] private struct MouseInputEvent { public uint Type; public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] MouseInputEvent[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool PatBlt(nint dc, int x, int y, int width, int height, uint operation);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
