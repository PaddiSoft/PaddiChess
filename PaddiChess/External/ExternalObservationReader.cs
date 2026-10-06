namespace PaddiXiangqi.External;

/// <summary>One observation per identical validated image, scoped to one session.</summary>
public sealed class ExternalObservationReader(BoardCalibration calibration)
{
    private byte[]? _png;
    private BoardObservation? _observation;
    private CapturedPixels? _pixels;

    public async Task<BoardObservation> ReadAsync(ExternalFrame frame, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Compare bytes, not sequence numbers: capture restarts can reuse a sequence.
        // The desktop validates heartbeat/availability before returning every frame.
        if (frame.Pixels is { } pixels)
        {
            if (_observation != null && ReferenceEquals(_pixels, pixels)) return _observation;
            // A clock/avatar may refresh the window while the entire board is identical.
            // Compare the full board rectangle, including every lifted-disc sample, with
            // exact bytes. Never substitute a colour-distance threshold for new evidence.
            if (_observation != null && _pixels != null && SameBoardPixels(_pixels, pixels))
            { _pixels = pixels; return _observation; }
            var sampled = await Task.Run(() => BoardObservation.Read(frame, calibration, _observation), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _png = null; _pixels = pixels; _observation = sampled;
            return sampled;
        }
        if (_observation != null && _pixels == null && _png != null &&
            (ReferenceEquals(_png, frame.Png) || _png.AsSpan().SequenceEqual(frame.Png))) return _observation;
        var observation = await Task.Run(() => BoardObservation.Read(frame.Png, calibration, _observation), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        _png = frame.Png; _pixels = null; _observation = observation;
        return observation;
    }

    private bool SameBoardPixels(CapturedPixels before, CapturedPixels after)
    {
        after.Validate();
        if (before.Width != after.Width || before.Height != after.Height) return false;
        double dx = (calibration.Right - calibration.Left) / 8, dy = (calibration.Bottom - calibration.Top) / 9;
        int left = Math.Max(0, (int)Math.Floor(calibration.Left - dx * .65));
        int right = Math.Min(after.Width, (int)Math.Ceiling(calibration.Right + dx * .65));
        int top = Math.Max(0, (int)Math.Floor(calibration.Top - dy * .65));
        int bottom = Math.Min(after.Height, (int)Math.Ceiling(calibration.Bottom + dy * .65));
        for (int y = top; y < bottom; y++)
            if (!before.Bgra.AsSpan(y * before.RowBytes + left * 4, (right - left) * 4)
                .SequenceEqual(after.Bgra.AsSpan(y * after.RowBytes + left * 4, (right - left) * 4))) return false;
        return true;
    }
}
