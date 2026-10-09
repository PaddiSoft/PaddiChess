namespace PaddiXiangqi.External;

/// <summary>Frame-paced clicks, including an unconditional release on cancellation.</summary>
public static class ExternalClickSequence
{
    public static async Task ClickAsync(Action move, Action verify, Action down, Action up,
        CancellationToken ct, Func<int, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        verify(); move();
        await delay(50, ct).ConfigureAwait(false); // Hover must reach frame-driven game surfaces first.
        verify(); ct.ThrowIfCancellationRequested();
        down();
        try { await delay(60, ct).ConfigureAwait(false); }
        finally { up(); }
    }
}
