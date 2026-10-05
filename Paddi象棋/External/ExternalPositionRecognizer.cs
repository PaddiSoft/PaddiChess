namespace PaddiXiangqi.External;

/// <summary>Initial/full-board recognition, separate from the lightweight move tracker.</summary>
public sealed class ExternalPositionRecognizer
{
    public async Task<SkinRecognition?> ReadAsync(ExternalFrame frame, BoardCalibration geometry,
        bool redToMove, CancellationToken ct, bool detectOrientation, BoardSkin? sessionSkin, string? customPath)
    {
        var observation = await Task.Run(() => BoardObservation.Read(frame, geometry), ct).ConfigureAwait(false);
        var template = await ReadTemplatesAsync(observation, redToMove, ct, sessionSkin, customPath).ConfigureAwait(false);
        if (template != null) return template;
        return await BoardGlyphRecognizer.RecognizeAsync(frame, geometry, redToMove, ct, detectOrientation)
            .ConfigureAwait(false);
    }

    public Task<SkinRecognition?> ReadTemplatesAsync(BoardObservation observation, bool redToMove,
        CancellationToken ct, BoardSkin? sessionSkin, string? customPath) => Task.Run(() =>
        {
            IEnumerable<BoardSkin> skins = BuiltInBoardSkins.All;
            if (sessionSkin != null) skins = new[] { sessionSkin }.Concat(skins);
            if (customPath != null) skins = new[] { BoardSkin.Load(customPath) }.Concat(skins);
            foreach (var skin in skins)
            {
                ct.ThrowIfCancellationRequested();
                var read = skin.Recognize(observation, redToMove);
                if (read.Confident) return read;
            }
            return (SkinRecognition?)null;
        }, ct);
}
