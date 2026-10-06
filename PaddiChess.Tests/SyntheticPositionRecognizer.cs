using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

// Session tests render Latin piece codes to isolate input, clocks and frame
// ordering. Real model accuracy is tested separately against screenshot fixtures.
// This reader still classifies the supplied image; it never reads the fake game's FEN.
internal sealed class SyntheticPositionRecognizer : IExternalPositionRecognizer
{
    private static readonly BoardSkin Skin = BoardSkin.Learn("synthetic Latin pieces",
        BoardObservation.Read(ExternalBoardTests.Render(new XiangqiGame()), new(40, 40, 440, 490, false)), new());

    public Task<SkinRecognition?> ReadAsync(ExternalFrame frame, BoardCalibration geometry,
        bool redToMove, CancellationToken ct, bool detectOrientation, BoardSkin? sessionSkin, string? customPath)
        => Task.Run<SkinRecognition?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            bool? direction = detectOrientation ? BoardObservation.DetectRedAtTop(frame.Png, geometry) : null;
            var grid = direction is { } flipped ? geometry with { RedAtTop = flipped } : geometry;
            return Skin.Recognize(BoardObservation.Read(frame, grid), redToMove) with { DetectedRedAtTop = direction };
        }, ct);
}
