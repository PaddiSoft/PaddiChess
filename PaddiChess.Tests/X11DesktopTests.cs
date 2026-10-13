using System.Diagnostics;
using System.Runtime.InteropServices;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public sealed class X11FactAttribute : FactAttribute
{
    public X11FactAttribute()
    {
        if (!X11ExternalDesktop.Available || Environment.GetEnvironmentVariable("PADDI_TEST_NATIVE_INPUT") != "1")
            Skip = "Requires Linux X11 and PADDI_TEST_NATIVE_INPUT=1 (owned test window only).";
    }
}

public class X11DesktopTests
{
    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern nuint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] private static extern nuint XCreateSimpleWindow(nint display, nuint parent, int x, int y, uint width, uint height, uint borderWidth, nuint border, nuint background);
    [DllImport("libX11.so.6")] private static extern int XStoreName(nint display, nuint window, string name);
    [DllImport("libX11.so.6")] private static extern int XMapWindow(nint display, nuint window);
    [DllImport("libX11.so.6")] private static extern int XDestroyWindow(nint display, nuint window);
    [DllImport("libX11.so.6")] private static extern int XSync(nint display, int discard);
    [DllImport("libX11.so.6")] private static extern int XSelectInput(nint display, nuint window, nint mask);
    [DllImport("libX11.so.6")] private static extern int XPending(nint display);
    [DllImport("libX11.so.6")] private static extern int XNextEvent(nint display, nint eventBuffer);

    [X11Fact]
    public async Task CompositeCaptureAndTwoClicksUseOwnedWindow()
    {
        var d = XOpenDisplay(0); Assert.NotEqual(0, d);
        var root = XDefaultRootWindow(d);
        var window = XCreateSimpleWindow(d, root, 30, 40, 300, 300, 0, 0, 0xff0000);
        nuint cover = 0;
        var backend = new X11ExternalDesktop(() =>
        {
            var info = RecognitionWorkerClient.ApplicationStartInfo("dotnet", Path.Combine(AppContext.BaseDirectory, "Paddi象棋.dll"), true);
            info.ArgumentList.RemoveAt(info.ArgumentList.Count - 1); info.ArgumentList.Add("--x11-worker"); return info;
        });
        try
        {
            XStoreName(d, window, "Paddi owned X11 fixture"); XSelectInput(d, window, (nint)(4 | 8));
            XMapWindow(d, window); XSync(d, 0);
            var target = Assert.Single(await backend.ListAsync(default), w => w.Id == (long)window);
            var first = await backend.CaptureAsync(target, default);
            Assert.Equal(255, first.Pixels!.Bgra[2]); Assert.Equal(0, first.Pixels.Bgra[0]);
            cover = XCreateSimpleWindow(d, root, 20, 20, 330, 340, 0, 0, 0xffffff);
            XMapWindow(d, cover); XSync(d, 0);
            var hidden = await backend.CaptureAsync(target, default);
            Assert.Equal(first.Pixels.Bgra, hidden.Pixels!.Bgra); // A covering window must not contaminate pixels.
            await backend.MoveAsync(target, 50, 60, 180, 190, default);
            var points = new List<(int X, int Y)>();
            var buffer = Marshal.AllocHGlobal(192);
            try
            {
                var clock = Stopwatch.StartNew();
                while (points.Count < 2 && clock.Elapsed < TimeSpan.FromSeconds(3))
                {
                    while (XPending(d) > 0)
                    {
                        XNextEvent(d, buffer);
                        if (Marshal.ReadInt32(buffer) == 4) points.Add((Marshal.ReadInt32(buffer, 64), Marshal.ReadInt32(buffer, 68)));
                    }
                    if (points.Count < 2) await Task.Delay(10);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            Assert.Equal(new[] { (50,60), (180,190) }, points);
        }
        finally
        {
            await backend.CloseCaptureAsync();
            if (cover != 0) XDestroyWindow(d, cover);
            XDestroyWindow(d, window); XCloseDisplay(d);
        }
    }
}
