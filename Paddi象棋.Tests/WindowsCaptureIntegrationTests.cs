using System.ComponentModel;
using System.Runtime.InteropServices;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public sealed class WindowsCaptureFactAttribute : FactAttribute
{
    public WindowsCaptureFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows GDI; never substitutes a mock capture.";
    }
}

public class WindowsCaptureIntegrationTests
{
    [WindowsCaptureFact]
    public async Task CapturedBgraStaysImmutableAndNeverKeepsUnpaintedPixelsFromAnOlderFrame()
    {
        using var window = new OwnedCaptureWindow();
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

    // A hidden, self-owned Win32 window paints only in response to PrintWindow.
    // No user's screen, game, focus, mouse or permissions are touched.
    private sealed class OwnedCaptureWindow : IDisposable
    {
        private const uint Print = 0x0317, ResizeMessage = 0x8001, Close = 0x0010, Destroy = 0x0002;
        private readonly Thread _thread;
        private readonly WndProc _procedure;
        private readonly ManualResetEventSlim _ready = new();
        private Exception? _error;
        private nint _handle;
        private volatile bool _partial;
        private int _width = 240, _height = 220;
        public ExternalWindow Target => new((long)_handle, Environment.ProcessId, "Owned capture fixture", 20, 20, _width, _height);
        public bool PartialPaint { set => _partial = value; }
        public OwnedCaptureWindow()
        {
            _procedure = Handle;
            _thread = new Thread(Run) { IsBackground = true, Name = "Owned GDI capture fixture" };
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture window creation timed out.");
            if (_error != null) throw _error;
        }
        public void Resize(int width, int height) => SendMessage(_handle, ResizeMessage, width, height);
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
            if (message == Print)
            {
                if (_partial) Fill(wParam, new(0, 0, 10, 10), 0x00ff00);
                else
                {
                    Fill(wParam, new(0, 0, _width / 2, _height), 0x0000ff);
                    Fill(wParam, new(_width / 2, 0, _width, _height), 0xff0000);
                }
                return 1;
            }
            if (message == ResizeMessage)
            {
                _width = (int)wParam; _height = (int)lParam;
                SetWindowPos(window, 0, 20, 20, _width, _height, 0x14);
                return 0;
            }
            if (message == Destroy) { PostQuitMessage(0); return 0; }
            return DefWindowProc(window, message, wParam, lParam);
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
        [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Rect rect, nint brush);
        [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint colour);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    }
}
