using System.Diagnostics;
using PaddiXiangqi.External;
using PaddiXiangqi.Core;

namespace PaddiXiangqi.Sessions;

/// <summary>Single-flight full recognition with separately retained, frame-bound completed evidence.</summary>
public sealed class ExternalRecognitionRefresh(CancellationToken lifetime, TimeSpan? retryInterval = null,
    TimeSpan? attemptTimeout = null) : IAsyncDisposable
{
    private sealed record Evidence(string Position, string? Pending, BoardObservation Source, SkinRecognition Read);
    private sealed record Attempt(Evidence? Evidence, string? Failure, TimeSpan Elapsed);
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private Task<Attempt>? _task;
    private Evidence? _completed;
    private TimeSpan _nextAttempt;
    private SkinRecognition? _vocabularyRead, _latestRead;
    private BoardSkin? _vocabulary;
    private BoardObservation? _latest, _vocabularySource;
    public bool IsRunning => _task is { IsCompleted: false };
    public string? LastFailure { get; private set; }
    public TimeSpan LastDuration { get; private set; }
    public int CompletedAttempts { get; private set; }

    public bool TryStart(string position, string? pending, BoardObservation source, TimeSpan now,
        Func<CancellationToken, Task<SkinRecognition?>> recognize)
    {
        CollectCompleted();
        if (_lifetime.IsCancellationRequested || IsRunning || now < _nextAttempt) return false;
        _nextAttempt = now + (retryInterval ?? TimeSpan.FromSeconds(2));
        // Starting the next read must not erase the last completed one. A fresh
        // capture still has to match/reclassify its pixels and exact history below.
        _task = Task.Run(() => ReadAsync(position, pending, source, recognize));
        return true;
    }

    public SkinRecognition? Current(string position, string? pending, BoardObservation latest)
    {
        CollectCompleted();
        if (_lifetime.IsCancellationRequested || _completed is not { } evidence ||
            evidence.Position != position || evidence.Pending != pending) return null;
        var source = evidence.Source; var read = evidence.Read;
        if (SameSamples(source, latest)) return read;
        if (read is not { Confident: true } || source.Width != latest.Width || source.Height != latest.Height) return null;
        // Animations need not become pixel-identical before an independent read
        // is useful. Reclassify every changed cell from that read's vocabulary;
        // never borrow labels from the live game or accept a distance-only match.
        if (!ReferenceEquals(_vocabularyRead, read) || !ReferenceEquals(_vocabularySource, source))
        {
            var positionGame = new XiangqiGame(); positionGame.LoadFen(read.Fen);
            _vocabulary = BoardSkin.Learn("independent observation", source, positionGame, requireAllPieces: false);
            _vocabularyRead = read; _vocabularySource = source; _latest = null;
        }
        if (!ReferenceEquals(_latest, latest))
        {
            _latestRead = _vocabulary!.RecognizeFromPreviousRead(latest, read.Fen.Split(' ')[1] == "w", source, read);
            _latest = latest;
        }
        // Preserve uncertainty for diagnostics. Callers must still require Confident
        // before input; null used to hide this as an indefinitely pending request.
        return _latestRead;
    }

    private void CollectCompleted()
    {
        if (_task is not { IsCompletedSuccessfully: true }) return;
        var result = _task.Result; _task = null;
        CompletedAttempts++; LastDuration = result.Elapsed; LastFailure = result.Failure;
        // A newer read, including uncertainty or a conflict, supersedes old evidence.
        // Failed inference has no new evidence: retain only the existing frame-bound read.
        if (result.Evidence != null) _completed = result.Evidence;
    }

    // A pre-input identity check can also discover a missed move. Keep that evidence
    // available to the normal multi-frame reconciliation, bound to its source pixels.
    public void Seed(string position, string? pending, BoardObservation source, SkinRecognition read)
    {
        CollectCompleted();
        if (IsRunning || _lifetime.IsCancellationRequested) return;
        _completed = new(position, pending, source, read);
    }

    private async Task<Attempt> ReadAsync(string position, string? pending, BoardObservation source,
        Func<CancellationToken, Task<SkinRecognition?>> recognize)
    {
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(attemptTimeout ?? TimeSpan.FromSeconds(3));
        try
        {
            var read = await recognize(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return new(read == null ? null : new(position, pending, source, read),
                read == null ? "识别未返回结果" : null, clock.Elapsed);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        { return new(null, null, clock.Elapsed); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { return new(null, "本次核验超时，正在重新读取", clock.Elapsed); }
        catch (Exception ex)
        { return new(null, $"识别失败（{ex.GetType().Name}），正在重试", clock.Elapsed); }
    }

    public static bool SameSamples(BoardObservation source, BoardObservation latest)
        => source.HasSameRecognitionSamples(latest);

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { if (_task != null) await _task.ConfigureAwait(false); }
        finally { _lifetime.Dispose(); }
    }
}
