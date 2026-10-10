using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Diagnostics;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public sealed class WindowsCaptureFactAttribute : FactAttribute
{
    public WindowsCaptureFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows GDI; never substitutes a mock capture.";
    }
}

public sealed class WindowsNativeInputFactAttribute : FactAttribute
{
    public WindowsNativeInputFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("PADDI_TEST_NATIVE_INPUT") != "1")
            Skip = "Native input is opt-in and targets only the owned Win32 fixture on an interactive Windows test desktop.";
    }
}

public class WindowsCaptureIntegrationTests
{
    [WindowsNativeInputFact]
    public async Task PacedSourceAndDestinationClicksReachAnOwnedWin32Window()
    {
        using var window = new OwnedCaptureWindow();
        window.Show();
        var desktop = ExternalDesktop.Create();
        await desktop.MoveAsync(window.Target, 40, 100, 180, 100, default);
        for (int i = 0; i < 100 && window.Input.Count < 4; i++) await Task.Delay(10);
        var events = window.Input.ToArray();
        Assert.Equal(new uint[] { 0x0201, 0x0202, 0x0201, 0x0202 }, events.Select(e => e.Message));
        Assert.Equal(new[] { 40, 40, 180, 180 }, events.Select(e => e.X));
        Assert.All(events, e => Assert.Equal(100, e.Y));
        Assert.True(Stopwatch.GetElapsedTime(events[0].Time, events[1].Time).TotalMilliseconds >= 40);
        Assert.True(Stopwatch.GetElapsedTime(events[2].Time, events[3].Time).TotalMilliseconds >= 40);
        await desktop.CompleteSelectedMoveAsync(window.Target, 40, 100, 180, 100, default);
        for (int i = 0; i < 100 && window.Input.Count < 6; i++) await Task.Delay(10);
        events = window.Input.ToArray();
        Assert.Equal(6, events.Length);
        Assert.All(events.Skip(4), e => Assert.Equal(180, e.X));
    }

    [WindowsNativeInputFact]
    public async Task PointerMovementDuringHoverAndPressDoesNotRedirectClicks()
    {
        using var window = new OwnedCaptureWindow { DisplacePointer = true };
        window.Show();
        var desktop = ExternalDesktop.Create();
        await desktop.MoveAsync(window.Target, 40, 100, 180, 100, default);
        await desktop.CompleteSelectedMoveAsync(window.Target, 40, 100, 180, 100, default);
        for (int i = 0; i < 100 && window.Input.Count < 6; i++) await Task.Delay(10);
        var events = window.Input.ToArray();
        Assert.True(window.PointerDisplacements >= 3, "The fixture must actually move the OS pointer during delivery.");
        Assert.Equal(new uint[] { 0x0201, 0x0202, 0x0201, 0x0202, 0x0201, 0x0202 }, events.Select(e => e.Message));
        Assert.Equal(new[] { 40, 40, 180, 180, 180, 180 }, events.Select(e => e.X));
        Assert.All(events, e => Assert.Equal(100, e.Y));
    }

    [WindowsNativeInputFact]
    public async Task PointerMovementDoesNotRedirectDragPressOrRelease()
    {
        using var window = new OwnedCaptureWindow { DisplacePointer = true };
        window.Show();
        var desktop = ExternalDesktop.Create();
        desktop.InputMode = ExternalInputMode.Drag;
        await desktop.MoveAsync(window.Target, 40, 100, 180, 100, default);
        for (int i = 0; i < 100 && window.Input.Count < 2; i++) await Task.Delay(10);
        var events = window.Input.ToArray();
        Assert.True(window.PointerDisplacements >= 2, "The drag must overlap pointer movement.");
        Assert.Equal(new uint[] { 0x0201, 0x0202 }, events.Select(e => e.Message));
        Assert.Equal(new[] { 40, 180 }, events.Select(e => e.X));
        Assert.All(events, e => Assert.Equal(100, e.Y));
    }

    [WindowsCaptureFact]
    public async Task CapturedBgraStaysImmutableAcrossNativeRepaintsAndResizes()
    {
        using var window = new OwnedCaptureWindow();
        window.Show(); // PW_RENDERFULLCONTENT needs a shown, renderable window.
        var desktop = ExternalDesktop.Create();
        try
        {
            var first = await desktop.CaptureAsync(window.Target, default);
            Assert.Equal((240, 220), (first.Pixels!.Width, first.Pixels.Height));
            Assert.Equal((byte)255, Pixel(first, 50, 100)[2]); // B,G,R,A: red is index 2.
            Assert.Equal((byte)255, Pixel(first, 190, 100)[0]); // Blue is index 0.
            window.PartialPaint = true;
            var second = await desktop.CaptureAsync(window.Target, default);
            Assert.Equal(new byte[] { 0, 0, 0 }, Pixel(second, 50, 100)[..3]);
            Assert.Equal((byte)255, Pixel(first, 50, 100)[2]); // Published bytes were not reused.
            Assert.False(first.PngEncoded);
            window.Resize(360, 280);
            window.PartialPaint = false;
            var resized = await desktop.CaptureAsync(window.Target, default);
            Assert.Equal((360, 280), (resized.Pixels!.Width, resized.Pixels.Height));
            Assert.Equal((byte)255, Pixel(resized, 300, 100)[0]);
        }
        finally { await desktop.CloseCaptureAsync(); }
    }

    private static byte[] Pixel(ExternalFrame frame, int x, int y)
    {
        var pixels = frame.Pixels!;
        return pixels.Bgra.AsSpan(y * pixels.RowBytes + x * 4, 4).ToArray();
    }

    // A self-owned Win32 window, shown without activation, paints through GDI.
    // PW_RENDERFULLCONTENT can copy the DWM surface without sending WM_PRINT.
    // The opt-in native input test activates/clicks only this fixture.
    private sealed class OwnedCaptureWindow : IDisposable
    {
        private const uint Print = 0x0317, PrintClient = 0x0318, Paint = 0x000f, ResizeMessage = 0x8001, ShowMessage = 0x8002, RepaintMessage = 0x8003, Close = 0x0010, Destroy = 0x0002;
        public ConcurrentQueue<(uint Message, int X, int Y, long Time)> Input { get; } = new();
        public bool DisplacePointer { get; init; }
        public int PointerDisplacements => Volatile.Read(ref _pointerDisplacements);
        private int _pointerDisplacements;
        private readonly Thread _thread;
        private readonly WndProc _procedure;
        private readonly ManualResetEventSlim _ready = new();
        private Exception? _error;
        private nint _handle;
        private volatile bool _partial;
        private int _width = 240, _height = 220;
        public ExternalWindow Target => new((long)_handle, Environment.ProcessId, "Owned capture fixture", 20, 20, _width, _height);
        public bool PartialPaint { set { _partial = value; SendMessage(_handle, RepaintMessage, 0, 0); } }
        public OwnedCaptureWindow()
        {
            _procedure = Handle;
            _thread = new Thread(Run) { IsBackground = true, Name = "Owned GDI capture fixture" };
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture window creation timed out.");
            if (_error != null) throw _error;
        }
        public void Resize(int width, int height) => SendMessage(_handle, ResizeMessage, width, height);
        public void Show() => SendMessage(_handle, ShowMessage, 0, 0);
        private void Run()
        {
            var previousDpi = SetThreadDpiAwarenessContext(-4);
            string name = "PaddiCaptureFixture" + Guid.NewGuid().ToString("N");
            var instance = GetModuleHandle(null);
            ushort atom = 0;
            try
            {
                var type = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = _procedure, Instance = instance, Name = name };
                atom = RegisterClassEx(ref type);
                if (atom == 0) throw new Win32Exception();
                _handle = CreateWindowEx(0, name, name, 0x80000000, 20, 20, _width, _height, 0, 0, instance, 0);
                if (_handle == 0) throw new Win32Exception();
                _ready.Set();
                while (GetMessage(out var message, 0, 0, 0) > 0) DispatchMessage(ref message);
            }
            catch (Exception error) { _error = error; _ready.Set(); }
            finally
            {
                if (atom != 0) UnregisterClass(name, instance);
                if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi);
            }
        }
        private nint Handle(nint window, uint message, nint wParam, nint lParam)
        {
            if (message == ShowMessage)
            { SetWindowPos(window, -1, 20, 20, _width, _height, 0x0050); Repaint(window); return 0; }
            if (message == RepaintMessage) { Repaint(window); return 0; }
            if (message is 0x0201 or 0x0202)
                Input.Enqueue((message, (short)(lParam & 0xffff), (short)((lParam >> 16) & 0xffff), Stopwatch.GetTimestamp()));
            // Deliberately move the real OS cursor after hover and press, within
            // this owned window. No synthetic button messages bypass SendInput.
            // The alternate row prevents recursive movement from this callback.
            if (DisplacePointer && message is 0x0200 or 0x0201 && (short)((lParam >> 16) & 0xffff) == 100)
            {
                if (SetCursorPos(140, 180)) Interlocked.Increment(ref _pointerDisplacements);
            }
            if (message is Print or PrintClient)
            {
                Draw(wParam);
                GdiFlush(); // Finish this thread's painting before handing the DC back.
                return 1;
            }
            if (message == Paint)
            {
                var dc = BeginPaint(window, out var paint);
                try { Draw(dc); GdiFlush(); }
                finally { EndPaint(window, ref paint); }
                return 0;
            }
            if (message == ResizeMessage)
            {
                _width = (int)wParam; _height = (int)lParam;
                SetWindowPos(window, 0, 20, 20, _width, _height, 0x14);
                Repaint(window);
                return 0;
            }
            if (message == Destroy) { PostQuitMessage(0); return 0; }
            return DefWindowProc(window, message, wParam, lParam);
        }
        private static void Repaint(nint window) { InvalidateRect(window, 0, false); UpdateWindow(window); DwmFlush(); }
        private void Draw(nint dc)
        {
            if (_partial)
            {
                Fill(dc, new(0, 0, _width, _height), 0);
                Fill(dc, new(0, 0, 10, 10), 0x00ff00);
            }
            else
            {
                Fill(dc, new(0, 0, _width / 2, _height), 0x0000ff);
                Fill(dc, new(_width / 2, 0, _width, _height), 0xff0000);
            }
        }
        private static void Fill(nint dc, Rect rect, uint colour)
        {
            var brush = CreateSolidBrush(colour);
            try { FillRect(dc, ref rect, brush); }
            finally { DeleteObject(brush); }
        }
        public void Dispose()
        {
            if (_handle != 0) PostMessage(_handle, Close, 0, 0);
            if (!_thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture window did not close.");
            _ready.Dispose();
        }
        private delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public uint Size, Style;
            [MarshalAs(UnmanagedType.FunctionPtr)] public WndProc Procedure;
            public int ClassExtra, WindowExtra;
            public nint Instance, Icon, Cursor, Background;
            public string? Menu;
            public string Name;
            public nint SmallIcon;
        }
        [StructLayout(LayoutKind.Sequential)] private readonly record struct Rect(int Left, int Top, int Right, int Bottom);
        [StructLayout(LayoutKind.Sequential)] private struct PaintInfo
        {
            public nint Dc;
            public int Erase;
            public Rect Bounds;
            public int Restore, Incremental;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint WParam, LParam; public uint Time; public int X, Y; public uint Private; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass type);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
        [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
        [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int result);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Rect rect, nint brush);
        [DllImport("user32.dll")] private static extern nint BeginPaint(nint window, out PaintInfo paint);
        [DllImport("user32.dll")] private static extern bool EndPaint(nint window, ref PaintInfo paint);
        [DllImport("user32.dll")] private static extern bool InvalidateRect(nint window, nint rect, bool erase);
        [DllImport("user32.dll")] private static extern bool UpdateWindow(nint window);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
        [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint colour);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
        [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    }
}
