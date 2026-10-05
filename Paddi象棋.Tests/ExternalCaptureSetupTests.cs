using Avalonia.Controls;
using Avalonia.Interactivity;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using SkiaSharp;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    private static ExternalFrame RetinaSetupFrame(ExternalFrame original)
    {
        using var source = SKBitmap.Decode(original.Png);
        using var image = new SKBitmap(source.Width * 2, source.Height * 2);
        using (var canvas = new SKCanvas(image))
            canvas.DrawBitmap(source, new SKRect(0, 0, image.Width, image.Height));
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return new(original.Window, png.ToArray());
    }

    [Fact]
    public async Task CaptureGeometryEditorRefreshPreservesCorrectedGridAtRetinaScale()
    {
        await Session.Dispatch(async () =>
        {
            var frame = new ExternalFrame(new(884, 884, "Retina editor", 0, 0, 480, 530),
                ExternalBoardTests.Render(new XiangqiGame(), true));
            var retina = RetinaSetupFrame(frame);
            // This intentionally differs from automatic locator coordinates.
            var corrected = new BoardCalibration(41, 42, 438, 488, true);
            var expected = new BoardCalibration(82, 84, 876, 976, true);
            BoardCalibration? readGeometry = null;
            var desktop = new EditorDesktop(retina);
            var owner = new Window(); owner.Show();
            var dialog = new ExternalPositionEditorWindow(new(XiangqiGame.InitialFen, "校正后网格", null,
                frame, corrected, true, 0), desktop,
                (image, geometry, _, _, _) =>
                {
                    Assert.Same(retina, image); readGeometry = geometry;
                    return Task.FromResult<SkinRecognition?>(new(XiangqiGame.InitialFen, [], 0, null));
                }, default);
            var result = dialog.ShowDialog<ExternalPositionEditResult?>(owner);
            try
            {
                dialog.FindControl<Button>("ExternalEditorRefresh")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var deadline = DateTime.UtcNow.AddSeconds(4);
                while (readGeometry == null && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.Equal(expected, readGeometry);
                Assert.Equal(1, desktop.Captures); Assert.Equal(0, desktop.Moves);
                var preview = dialog.FindControl<CalibrationView>("ExternalEditorCalibration")!;
                Assert.Equal(960, preview.PixelWidth); Assert.Equal(1060, preview.PixelHeight);
                Assert.True(dialog.FindControl<BoardView>("ExternalEditorBoard")!.Flipped);
                dialog.FindControl<Button>("ExternalEditorAccept")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var accepted = await result;
                Assert.NotNull(accepted); Assert.Same(retina, accepted.Frame);
                Assert.Equal(expected, accepted.Geometry); Assert.True(accepted.RedAtTop);
            }
            finally { dialog.Close(); owner.Close(); }
            return true;
        }, default);
    }

    [Fact]
    public async Task CaptureGeometrySettleWaitReturnsFrameAndMatchingCalibrationTogether()
    {
        await Session.Dispatch(async () =>
        {
            var settings = Path.Combine(Path.GetTempPath(), $"paddi-scale-setup-{Guid.NewGuid():N}.json");
            var oldSettings = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", settings);
            var window = new MainWindow();
            try
            {
                var frame = new ExternalFrame(new(885, 885, "Retina settle", 0, 0, 480, 530),
                    ExternalBoardTests.Render(new XiangqiGame()));
                var retina = RetinaSetupFrame(frame);
                var desktop = new EditorDesktop(retina);
                Set(window, "_externalDesktop", desktop);
                var work = (Task<(ExternalFrame Frame, BoardCalibration Geometry)>)typeof(MainWindow)
                    .GetMethod("ReadSettledExternalFrameAsync", Private)!.Invoke(window,
                        [frame, new BoardCalibration(40, 40, 440, 490, false), CancellationToken.None])!;
                var settled = await work;
                Assert.Same(retina, settled.Frame);
                Assert.Equal(new BoardCalibration(80, 80, 880, 980, false), settled.Geometry);
                Assert.True(desktop.Captures >= 2); // The two scales cannot be mistaken for one stable frame.
                Assert.Equal(0, desktop.Moves);
            }
            finally
            {
                window.Close(); await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", oldSettings);
                if (File.Exists(settings)) File.Delete(settings);
            }
            return true;
        }, default);
    }
}
