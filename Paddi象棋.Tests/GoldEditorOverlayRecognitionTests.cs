using PaddiXiangqi.External;
using Xunit.Abstractions;

namespace PaddiXiangqi.Tests;

/// <summary>
/// Only the left board preview was provided for this appearance. Its blue grid
/// dots and selection circles are editor overlays, not raw capture pixels. This
/// is an obstruction safety case, not proof that this game's raw skin is read.
/// </summary>
public class GoldEditorOverlayRecognitionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EditorOverlaysCannotAuthorizeADifferentCompletePosition()
    {
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jj-gold-editor-overlay.png"));
        var geometry = new BoardCalibration(37, 32, 540, 582, true);
        const string expected = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C2B2C1/9/RN1AKABNR b - - 0 1";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var read = await BoardGlyphRecognizer.RecognizeAsync(png, geometry, false, cancellation.Token, detectOrientation: true);
        output.WriteLine($"Confident={read.Confident}; {read.Fen}; {read.Problem}; uncertain={string.Join(',', read.Uncertain.Select(square => square.Uci))}");
        if (read.Confident)
        {
            Assert.Equal(true, read.DetectedRedAtTop);
            Assert.Equal(expected, read.Fen);
        }
        else Assert.True(read.Uncertain.Count > 0 || read.Problem != null);
    }
}
