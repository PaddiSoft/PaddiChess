using Avalonia.Headless;

namespace PaddiXiangqi.Tests;

/// <summary>
/// Avalonia exposes Action and generic Func&lt;T&gt; / Func&lt;Task&lt;T&gt;&gt; dispatch,
/// but no Func&lt;Task&gt; overload. Explicitly await void-returning async tests.
/// Otherwise they dispatch a Task value and can pass before their assertions run.
/// </summary>
internal sealed class AwaitedUiSession(Type application)
{
    private readonly HeadlessUnitTestSession _session = HeadlessUnitTestSession.StartNew(application);
    public Task Dispatch(Func<Task> action, CancellationToken ct) => _session.Dispatch(async () =>
    { await action(); return true; }, ct);
    public Task Dispatch(Action action, CancellationToken ct) => _session.Dispatch(action, ct);
    public Task<T> Dispatch<T>(Func<Task<T>> action, CancellationToken ct) => _session.Dispatch(action, ct);
    public Task<T> Dispatch<T>(Func<T> action, CancellationToken ct) => _session.Dispatch(action, ct);
}
