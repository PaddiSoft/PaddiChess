using PaddiXiangqi.External;
using PaddiXiangqi.Core;

namespace PaddiXiangqi.Sessions;

/// <summary>Bounded background full-board recognition when incremental tracking cannot recover.</summary>
public sealed class ExternalRecognitionRefresh(CancellationToken lifetime, TimeSpan? retryInterval = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private Task<SkinRecognition?>? _task;
    private BoardObservation? _source;
    private string? _position, _pending;
    private TimeSpan _nextAttempt;
    private SkinRecognition? _vocabularyRead, _latestRead;
    private BoardSkin? _vocabulary;
    private BoardObservation? _latest, _vocabularySource;
    public bool IsRunning => _task is { IsCompleted: false };

    public bool TryStart(string position, string? pending, BoardObservation source, TimeSpan now,
        Func<CancellationToken, Task<SkinRecognition?>> recognize)
    {
        if (_lifetime.IsCancellationRequested || _task is { IsCompleted: false } || now < _nextAttempt) return false;
        _position = position; _pending = pending; _source = source;
        _nextAttempt = now + (retryInterval ?? TimeSpan.FromSeconds(2));
        _task = Task.Run(() => ReadAsync(recognize));
        return true;
    }

    public SkinRecognition? Current(string position, string? pending, BoardObservation latest)
    {
        if (_lifetime.IsCancellationRequested || _task is not { IsCompletedSuccessfully: true } ||
            _position != position || _pending != pending || _source == null) return null;
        var read = _task.Result;
        if (SameSamples(_source, latest)) return read;
        if (read is not { Confident: true } || _source.Width != latest.Width || _source.Height != latest.Height) return null;
        // Animations need not become pixel-identical before an independent read
        // is useful. Reclassify every changed cell from that read's vocabulary;
        // never borrow labels from the live game or accept a distance-only match.
        if (!ReferenceEquals(_vocabularyRead, read) || !ReferenceEquals(_vocabularySource, _source))
        {
            var positionGame = new XiangqiGame(); positionGame.LoadFen(read.Fen);
            _vocabulary = BoardSkin.Learn("independent observation", _source, positionGame, requireAllPieces: false);
            _vocabularyRead = read; _vocabularySource = _source; _latest = null;
        }
        if (!ReferenceEquals(_latest, latest))
        {
            _latestRead = _vocabulary!.RecognizeFromPreviousRead(latest, read.Fen.Split(' ')[1] == "w", _source, read);
            _latest = latest;
        }
        return _latestRead is { Confident: true } ? _latestRead : null;
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
