using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

namespace PaddiXiangqi.External;

public sealed record X11Request(string Action, ExternalWindow? Window = null, double FromX = 0, double FromY = 0,
    double ToX = 0, double ToY = 0, bool Drag = false);
public sealed record X11Reply(ExternalWindow[]? Windows = null, ExternalWindow? Window = null, int Bytes = 0,
    bool Escape = false, string? Error = null, bool InputStarted = false);

/// <summary>X11 calls live in an isolated process; a disconnected X server cannot terminate the GUI.</summary>
public static class X11DesktopWorker
{
    private const string X11 = "libX11.so.6", Composite = "libXcomposite.so.1", Xtst = "libXtst.so.6";
    private static int _error;
    private delegate int ErrorHandler(nint display, nint error);
    private static readonly ErrorHandler HandleError = (_, _) => { _error = 1; return 0; };
    [StructLayout(LayoutKind.Sequential)] private struct Attributes
    {
        public int X,Y,Width,Height,Border,Depth; public nint Visual; public nuint Root;
        public int Class,BitGravity,WinGravity,BackingStore; public nuint BackingPlanes,BackingPixel;
        public int SaveUnder; public nuint Colormap; public int MapInstalled,MapState;
        public nint AllMasks,YourMasks,Propagation; public int OverrideRedirect; public nint Screen;
    }
    [StructLayout(LayoutKind.Sequential)] private struct XImage
    {
        public int Width,Height,Offset,Format; public nint Data;
        public int ByteOrder,Unit,BitOrder,Pad,Depth,RowBytes,Bits;
        public nuint Red,Green,Blue;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Visual
    {
        public nint Extension; public nuint Id; public int Class;
        public nuint Red,Green,Blue; public int BitsPerRgb,MapEntries;
    }
    [DllImport(X11)] private static extern nint XOpenDisplay(nint name);
    [DllImport(X11)] private static extern int XCloseDisplay(nint display);
    [DllImport(X11)] private static extern nuint XDefaultRootWindow(nint display);
    [DllImport(X11)] private static extern nint XSetErrorHandler(ErrorHandler handler);
    [DllImport(X11)] private static extern int XSync(nint display, int discard);
    [DllImport(X11)] private static extern nuint XInternAtom(nint display, string name, int onlyIfExists);
    [DllImport(X11)] private static extern int XGetWindowProperty(nint display, nuint window, nuint property, nint offset,
        nint length, int delete, nuint requestedType, out nuint actualType, out int format, out nuint count, out nuint after, out nint data);
    [DllImport(X11)] private static extern int XQueryTree(nint display, nuint window, out nuint root, out nuint parent, out nint children, out uint count);
    [DllImport(X11)] private static extern int XFree(nint data);
    [DllImport(X11)] private static extern int XGetWindowAttributes(nint display, nuint window, out Attributes attributes);
    [DllImport(X11)] private static extern int XTranslateCoordinates(nint display, nuint from, nuint to, int x, int y, out int destX, out int destY, out nuint child);
    [DllImport(X11)] private static extern nint XGetImage(nint display, nuint drawable, int x, int y, uint width, uint height, nuint mask, int format);
    [DllImport(X11)] private static extern int XDestroyImage(nint image);
    [DllImport(X11)] private static extern int XFreePixmap(nint display, nuint pixmap);
    [DllImport(X11)] private static extern int XRaiseWindow(nint display, nuint window);
    [DllImport(X11)] private static extern int XSetInputFocus(nint display, nuint window, int revert, nuint time);
    [DllImport(X11)] private static extern int XQueryKeymap(nint display, byte[] keys);
    [DllImport(X11)] private static extern byte XKeysymToKeycode(nint display, nuint key);
    [DllImport(X11)] private static extern int XQueryPointer(nint display, nuint window, out nuint root, out nuint child,
        out int rootX, out int rootY, out int x, out int y, out uint mask);
    [DllImport(Composite)] private static extern void XCompositeRedirectWindow(nint display, nuint window, int update);
    [DllImport(Composite)] private static extern nuint XCompositeNameWindowPixmap(nint display, nuint window);
    [DllImport(Xtst)] private static extern int XTestFakeMotionEvent(nint display, int screen, int x, int y, nuint delay);
    [DllImport(Xtst)] private static extern int XTestFakeButtonEvent(nint display, uint button, int pressed, nuint delay);

    private static byte[] Property(nint d, nuint window, string name, out int format)
    {
        var atom = XInternAtom(d, name, 1);
        if (atom == 0) { format = 0; return []; }
        var status = XGetWindowProperty(d, window, atom, 0, 16384, 0, 0,
            out _, out format, out var count, out _, out var data);
        try
        {
            if (status != 0 || data == 0 || count > 65536) return [];
            var size = checked((int)count * (format == 32 ? nint.Size : format / 8));
            var bytes = new byte[size]; Marshal.Copy(data, bytes, 0, size); return bytes;
        }
        finally { if (data != 0) XFree(data); }
    }
    private static ExternalWindow Describe(nint d, nuint id)
    {
        _error = 0;
        if (XGetWindowAttributes(d, id, out var a) == 0) throw new IOException("窗口已关闭");
        XTranslateCoordinates(d, id, XDefaultRootWindow(d), 0, 0, out var x, out var y, out _);
        var title = Encoding.UTF8.GetString(Property(d, id, "_NET_WM_NAME", out _)).TrimEnd('\0');
        if (title.Length == 0) title = Encoding.UTF8.GetString(Property(d, id, "WM_NAME", out _)).TrimEnd('\0');
        var pid = Property(d, id, "_NET_WM_PID", out _);
        XSync(d, 0);
        if (_error != 0 || a.Width <= 0 || a.Height <= 0) throw new IOException("窗口不可访问");
        return new((long)id, pid.Length >= 4 ? BitConverter.ToInt32(pid) : 0, title, x, y, a.Width, a.Height) { OnScreen = a.MapState == 2 };
    }
    private static ExternalWindow[] List(nint d)
    {
        var bytes = Property(d, XDefaultRootWindow(d), "_NET_CLIENT_LIST", out var format);
        var ids = new List<nuint>();
        if (format == 32) for (int i = 0; i + nint.Size <= bytes.Length; i += nint.Size)
            ids.Add((nuint)BitConverter.ToUInt64(bytes, i));
        if (ids.Count == 0 && XQueryTree(d, XDefaultRootWindow(d), out _, out _, out var children, out var count) != 0)
        {
            try { for (int i = 0; i < Math.Min(count, 4096); i++) ids.Add((nuint)Marshal.ReadIntPtr(children, i * nint.Size)); }
            finally { if (children != 0) XFree(children); }
        }
        var windows = new List<ExternalWindow>();
        foreach (var id in ids) try { var w = Describe(d, id); if (w.Title.Length > 0 && w.OnScreen) windows.Add(w); } catch (IOException) { }
        return windows.ToArray();
    }
    private static CapturedPixels Capture(nint d, ExternalWindow w, HashSet<long> redirected)
    {
        var id = (nuint)w.Id;
        if (redirected.Add(w.Id)) { XCompositeRedirectWindow(d, id, 0); XSync(d, 0); }
        _error = 0;
        var pixmap = XCompositeNameWindowPixmap(d, id); XSync(d, 0);
        if (_error != 0 || pixmap == 0) throw new IOException("X11 合成器未提供窗口图像，请保持窗口可见并启用 Composite 扩展。");
        nint image = 0;
        try
        {
            XGetWindowAttributes(d, id, out var a);
            image = XGetImage(d, pixmap, a.Border, a.Border, (uint)w.Width, (uint)w.Height, nuint.MaxValue, 2);
            if (image == 0 || _error != 0) throw new IOException("无法读取窗口像素");
            var data = Marshal.PtrToStructure<XImage>(image);
            // XGetImage on a named pixmap may leave image color masks unset.
            // Its pixels use the source window's visual, unlike a window image
            // where Xlib fills those masks directly.
            var visual = Marshal.PtrToStructure<Visual>(a.Visual);
            if (data.Red == 0 && data.Green == 0 && data.Blue == 0)
            { data.Red = visual.Red; data.Green = visual.Green; data.Blue = visual.Blue; }
            if (visual.Class != 4 || data.Bits != 32 || data.ByteOrder != 0 || data.Red != 0xff0000 || data.Green != 0xff00 || data.Blue != 0xff)
                throw new NotSupportedException("X11 捕获要求 24/32 位 TrueColor 显示模式");
            var length = checked(data.RowBytes * data.Height);
            if (length <= 0 || length > 268435456) throw new IOException("窗口图像过大");
            var bytes = new byte[length]; Marshal.Copy(data.Data, bytes, 0, length);
            for (int y = 0; y < data.Height; y++) for (int x = 0; x < data.Width; x++) bytes[y * data.RowBytes + x * 4 + 3] = 255;
            return new(data.Width, data.Height, data.RowBytes, bytes);
        }
        finally { if (image != 0) XDestroyImage(image); XFreePixmap(d, pixmap); }
    }
    private static bool Escape(nint d)
    {
        var keys = new byte[32]; XQueryKeymap(d, keys); var code = XKeysymToKeycode(d, 0xff1b);
        return (keys[code / 8] & (1 << (code % 8))) != 0;
    }
    private static nuint TopLevel(nint d, nuint window)
    {
        var root = XDefaultRootWindow(d);
        for (int depth = 0; depth < 32; depth++)
        {
            if (XQueryTree(d, window, out _, out var parent, out var children, out _) == 0)
                throw new IOException("窗口层级不可访问");
            if (children != 0) XFree(children);
            if (parent == root || parent == 0) return window;
            window = parent;
        }
        throw new IOException("窗口层级异常");
    }
    private static async Task MoveAsync(nint d, X11Request request)
    {
        var old = request.Window!; var w = Describe(d, (nuint)old.Id);
        if (!old.SameBounds(w) || !w.OnScreen) throw new ExternalInputBlockedException(false, "窗口已变化，需重新捕获后落子");
        foreach (var point in new[] { (request.FromX, request.FromY), (request.ToX, request.ToY) })
            if (!double.IsFinite(point.Item1) || !double.IsFinite(point.Item2) || point.Item1 < 0 || point.Item2 < 0 || point.Item1 >= w.Width || point.Item2 >= w.Height)
                throw new ExternalInputBlockedException(false, "落子坐标越界");
        var topLevel = TopLevel(d, (nuint)w.Id);
        XRaiseWindow(d, topLevel); XSetInputFocus(d, (nuint)w.Id, 1, 0); XSync(d, 0);
        if (_error != 0) throw new ExternalInputBlockedException(false, "无法聚焦目标窗口");
        bool started = false, down = false;
        void Point(double x, double y)
        {
            if (Escape(d)) throw new ExternalInputBlockedException(started, "Esc 已停止输入");
            var current = Describe(d, (nuint)w.Id);
            if (!current.SameBounds(w)) throw new ExternalInputBlockedException(started, "窗口移动，已停止输入");
            XTestFakeMotionEvent(d, -1, (int)Math.Round(w.X + x), (int)Math.Round(w.Y + y), 0); XSync(d, 0);
            XQueryPointer(d, XDefaultRootWindow(d), out _, out var underPointer, out var rx, out var ry, out _, out _, out _);
            if (underPointer != topLevel)
                throw new ExternalInputBlockedException(started, "目标被其他窗口遮挡，已停止输入");
            if (Math.Abs(rx - (w.X + x)) > 2 || Math.Abs(ry - (w.Y + y)) > 2)
                throw new ExternalInputBlockedException(started, "鼠标位置发生变化，已停止输入");
        }
        try
        {
            Point(request.FromX, request.FromY);
            XTestFakeButtonEvent(d, 1, 1, 0); started = down = true; XSync(d, 0);
            await Task.Delay(35);
            if (!request.Drag) { XTestFakeButtonEvent(d, 1, 0, 0); down = false; XSync(d, 0); await Task.Delay(65); }
            Point(request.ToX, request.ToY);
            if (!request.Drag) { XTestFakeButtonEvent(d, 1, 1, 0); down = true; XSync(d, 0); await Task.Delay(35); }
        }
        finally { if (down) { XTestFakeButtonEvent(d, 1, 0, 0); XSync(d, 0); } }
    }
    public static async Task ServeAsync(Stream input, Stream output)
    {
        var d = XOpenDisplay(0);
        if (d == 0) throw new IOException("无法连接 X11 显示服务");
        XSetErrorHandler(HandleError); var redirected = new HashSet<long>();
        try
        {
            while (true)
            {
                X11Request request;
                try { request = await RecognitionWorkerProtocol.ReadAsync<X11Request>(input, default); } catch (EndOfStreamException) { return; }
                CapturedPixels? pixels = null; X11Reply reply;
                try
                {
                    _error = 0;
                    if (request.Action == "list") reply = new(Windows: List(d));
                    else if (request.Action == "capture" && request.Window != null)
                    {
                        var window = Describe(d, (nuint)request.Window.Id);
                        if (window.Pid != request.Window.Pid || !window.OnScreen) throw new IOException("窗口已关闭或最小化");
                        pixels = Capture(d, window, redirected);
                        reply = new(Window: window, Bytes: pixels.Bgra.Length, Escape: Escape(d));
                    }
                    else if (request.Action == "move" && request.Window != null) { await MoveAsync(d, request); reply = new(Escape: Escape(d)); }
                    else throw new IOException("未知 X11 请求");
                }
                catch (Exception ex) { reply = new(Error: ex.Message, InputStarted: ex is not ExternalInputBlockedException { InputStarted: false } && request.Action == "move"); }
                await RecognitionWorkerProtocol.WriteAsync(output, reply, default);
                if (pixels != null && reply.Error == null) { await output.WriteAsync(pixels.Bgra); await output.FlushAsync(); }
            }
        }
        finally { XCloseDisplay(d); }
    }
}
