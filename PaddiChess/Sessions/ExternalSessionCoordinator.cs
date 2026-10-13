using System.Diagnostics;
using PaddiXiangqi.External;
using PaddiXiangqi.Core;

namespace PaddiXiangqi.Sessions;

public sealed record VerificationFrame(ExternalFrame Capture, BoardObservation Observation, BoardCalibration Calibration, bool GeometryChanged);
public sealed record InputVerificationResult(VerificationFrame Frame, SkinRecognition? Read, BoardMatch? Match,
    string? WaitingReason, bool RetryImmediately = false);

public sealed record StageLatency(string Stage, long Samples, double P50Ms, double P95Ms, double P99Ms);

/// <summary>Bounded, thread-safe timing evidence. No titles, pixels, model endpoints or credentials.</summary>
public sealed class SessionLatencyMetrics
{
    private sealed class Samples { public readonly Queue<double> Values = new(); public long Count; }
    private readonly Dictionary<string, Samples> _stages = new();
    public void Record(string stage, double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0) return;
        lock (_stages)
        {
            if (!_stages.TryGetValue(stage, out var samples)) _stages.Add(stage, samples = new());
            samples.Count++; samples.Values.Enqueue(milliseconds);
            if (samples.Values.Count > 2048) samples.Values.Dequeue();
        }
    }
    public IReadOnlyList<StageLatency> Snapshot()
    {
        lock (_stages) return _stages.Select(pair =>
        {
            var ordered = pair.Value.Values.Order().ToArray();
            double Percentile(double p) => ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * p) - 1, 0, ordered.Length - 1)];
            return new StageLatency(pair.Key, pair.Value.Count, Percentile(.5), Percentile(.95), Percentile(.99));
        }).OrderBy(x => x.Stage).ToArray();
    }
}

/// <summary>
/// Owns one observation session's worker lifetime, search slot and evidence gates.
/// The view supplies frozen search inputs and renders progress; it cannot start a second
/// search over an unfinished one or replace a generation after cancellation.
/// </summary>
public sealed class ExternalSessionCoordinator<TDecision, TConfiguration> : IAsyncDisposable where TConfiguration : class
{
    private readonly CancellationTokenSource _lifetime;
    private CancellationTokenSource? _decisionCancellation;
    private Task<TDecision>? _decision;
    private long _readyAt, _armedAt;
    private bool _armed;
    private readonly Stopwatch _cadence = Stopwatch.StartNew();
    public ExternalSynchronizationSession Synchronization { get; } = new();
    public ExternalInputRetry InputRetry { get; } = new();
    public ExternalRecognitionRefresh Recognition { get; }
    public ExternalRecognitionRefresh Verification { get; }
    public SessionLatencyMetrics Metrics { get; } = new();
    public Task<TDecision>? Decision => _decision;
    public string? DecisionFen { get; private set; }
    public TConfiguration? Configuration { get; private set; }
    public CancellationTokenSource? DecisionCancellation => _decisionCancellation;

    public ExternalSessionCoordinator(CancellationToken ct)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Recognition = new(_lifetime.Token);
        Verification = new(_lifetime.Token, TimeSpan.FromMilliseconds(250));
    }
    public async Task WaitForCaptureAsync(int pollMs, bool immediate, bool candidate, bool recovering, bool inputPending)
    {
        var interval = candidate ? Math.Min(pollMs, 80) : recovering ? Math.Max(pollMs, 120)
            : inputPending ? Math.Min(pollMs, 80) : pollMs;
        var delay = immediate ? 0 : Math.Max(1, interval - (int)_cadence.ElapsedMilliseconds);
        if (delay > 0) await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
        _cadence.Restart();
    }
    public void Start(string fen, TConfiguration configuration, Func<CancellationToken, Task<TDecision>> calculate)
    {
        if (_decision != null) throw new InvalidOperationException("必须先收取上一搜索结果");
        _lifetime.Token.ThrowIfCancellationRequested();
        DecisionFen = fen; Configuration = configuration; _readyAt = 0;
        _decisionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _decision = CalculateAsync(calculate, _decisionCancellation.Token);
    }
    private async Task<TDecision> CalculateAsync(Func<CancellationToken, Task<TDecision>> calculate, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        try { return await calculate(ct).ConfigureAwait(false); }
        finally
        {
            Metrics.Record("search", Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            Interlocked.Exchange(ref _readyAt, Stopwatch.GetTimestamp());
        }
    }
    public void UsePrepared(string fen, TConfiguration configuration, TDecision decision)
    {
        if (_decision != null) throw new InvalidOperationException("搜索仍在运行");
        _lifetime.Token.ThrowIfCancellationRequested();
        DecisionFen = fen; Configuration = configuration;
        if (_readyAt == 0) _readyAt = Stopwatch.GetTimestamp();
        _decision = Task.FromResult(decision);
    }
    public void CancelIfChanged(string fen, TConfiguration? configuration)
    {
        if (DecisionFen != fen || !Equals(Configuration, configuration)) CancelDecision();
    }
    public void CancelDecision() => _decisionCancellation?.Cancel();
    public async Task<TDecision> CollectAsync()
    {
        var task = _decision ?? throw new InvalidOperationException("没有搜索结果");
        try { return await task.ConfigureAwait(false); }
        finally { _decision = null; _decisionCancellation?.Dispose(); _decisionCancellation = null; }
    }
    public async Task<InputVerificationResult> VerifyInputAsync(string identity, ExternalBoardTracker tracker,
        XiangqiGame game, TimeSpan now, Func<CancellationToken, Task<VerificationFrame>> capture,
        Func<VerificationFrame, CancellationToken, Task<SkinRecognition?>> recognize, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var frame = await capture(ct);
            if (frame.GeometryChanged) return new(frame, null, null, "窗口像素尺寸变化，重新核验", true);
            var read = await Task.Run(() => Verification.Current(identity, null, frame.Observation), ct);
            if (read is not { Confident: true })
            {
                Verification.TryStart(identity, null, frame.Observation, now,
                    token => RecognizeAsync(inner => recognize(frame, inner), token));
                var reason = read is { Uncertain.Count: > 0 } ? $"正在核验 {read.Uncertain.Count} 个不确定位置"
                    : read?.Problem ?? Verification.LastFailure ?? "正在核验最新棋盘";
                return new(frame, read, null, reason);
            }
            var match = await Task.Run(() => ExternalInputVerification.Check(tracker, game, frame.Observation, read), ct);
            if (match is { Recognized: true, Moves.Count: 0 })
            {
                var latest = await capture(ct);
                if (latest.GeometryChanged) return new(latest, null, null, "窗口像素尺寸变化，重新核验", true);
                var latestRead = await Task.Run(() => Verification.Current(identity, null, latest.Observation), ct);
                if (latestRead is not { Confident: true }) return new(latest, latestRead, null, "最新画面仍有变化，正在核验棋子", true);
                frame = latest; read = latestRead;
                match = await Task.Run(() => ExternalInputVerification.Check(tracker, game, frame.Observation, read), ct);
            }
            Recognition.Seed(identity, null, frame.Observation, read);
            return new(frame, read, match, null);
        }
        finally { Metrics.Record("verification", Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
    }

    public void SetInputEnabled(bool enabled)
    {
        if (enabled && !_armed) _armedAt = Stopwatch.GetTimestamp();
        _armed = enabled;
    }
    public async Task<SkinRecognition?> RecognizeAsync(Func<CancellationToken, Task<SkinRecognition?>> recognize, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try { return await recognize(ct).ConfigureAwait(false); }
        finally { Metrics.Record("full-recognition", clock.Elapsed.TotalMilliseconds); }
    }
    public void InputSubmitted()
    {
        var ready = Interlocked.Exchange(ref _readyAt, 0);
        if (ready != 0) Metrics.Record("ready-to-input", Stopwatch.GetElapsedTime(Math.Max(ready, _armedAt)).TotalMilliseconds);
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            if (_decision != null) try { await _decision.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        finally
        {
            _decisionCancellation?.Dispose();
            await Recognition.DisposeAsync().ConfigureAwait(false);
            await Verification.DisposeAsync().ConfigureAwait(false);
            _lifetime.Dispose();
        }
    }
}
