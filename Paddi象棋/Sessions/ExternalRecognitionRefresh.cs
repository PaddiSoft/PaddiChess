using PaddiXiangqi.External;

namespace PaddiXiangqi.Sessions;

/// <summary>Bounded background full-board recognition when incremental tracking cannot recover.</summary>
public sealed class ExternalRecognitionRefresh(CancellationToken lifetime) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private Task<SkinRecognition?>? _task;
    private BoardObservation? _source;
    private string? _position, _pending;
    private TimeSpan _nextAttempt;
    public bool IsRunning => _task is { IsCompleted: false };

    public bool TryStart(string position, string? pending, BoardObservation source, TimeSpan now,
        Func<CancellationToken, Task<SkinRecognition?>> recognize)
    {
        if (_lifetime.IsCancellationRequested || _task is { IsCompleted: false } || now < _nextAttempt) return false;
        _position = position; _pending = pending; _source = source;
        _nextAttempt = now + TimeSpan.FromSeconds(2);
        _task = ReadAsync(recognize);
        return true;
    }

    public SkinRecognition? Current(string position, string? pending, BoardObservation latest)
    {
        if (_lifetime.IsCancellationRequested || _task is not { IsCompletedSuccessfully: true } ||
            _position != position || _pending != pending || _source == null || !SameSamples(_source, latest)) return null;
        return _task.Result;
    }

    // A pre-input identity check can also discover a missed move. Keep that evidence
    // available to the normal multi-frame reconciliation, bound to its source pixels.
    public void Seed(string position, string? pending, BoardObservation source, SkinRecognition read)
    {
        if (IsRunning || _lifetime.IsCancellationRequested) return;
        _position = position; _pending = pending; _source = source;
        _task = Task.FromResult<SkinRecognition?>(read);
    }

    private async Task<SkinRecognition?> ReadAsync(Func<CancellationToken, Task<SkinRecognition?>> recognize)
    {
        try { return await recognize(_lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return null; }
        catch (Exception) { return null; } // Incremental observation remains available; retry after the bounded interval.
    }

    public static bool SameSamples(BoardObservation source, BoardObservation latest)
    {
        if (ReferenceEquals(source, latest)) return true;
        if (source.Width != latest.Width || source.Height != latest.Height) return false;
        for (var i = 0; i < source.Cells.Length; i++)
            if (!source.Cells[i].AsSpan().SequenceEqual(latest.Cells[i])) return false;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { if (_task != null) await _task.ConfigureAwait(false); }
        finally { _lifetime.Dispose(); }
    }
}
