namespace PaddiXiangqi.Services;

/// <summary>Serial, coalesced, atomic writes of immutable settings snapshots.</summary>
public sealed class PreferencesWriter(string path, TimeSpan? debounce = null)
{
    private readonly object _sync = new();
    private readonly string _path = Path.GetFullPath(path);
    private readonly TimeSpan _debounce = ValidateDebounce(debounce);
    private string? _pending;
    private Task? _worker;
    public Exception? LastError { get; private set; }
    public int WriteCount { get; private set; }

    public void Schedule(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        lock (_sync)
        {
            _pending = json;
            _worker ??= Task.Run(WriteLoopAsync);
        }
    }
    public Task FlushAsync() { lock (_sync) return _worker ?? Task.CompletedTask; }

    private static TimeSpan ValidateDebounce(TimeSpan? requested)
    {
        var value = requested ?? TimeSpan.FromMilliseconds(300);
        if (value < TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(requested), "保存延迟必须是有限的非负时长。");
        return value;
    }

    private async Task WriteLoopAsync()
    {
        while (true)
        {
            await Task.Delay(_debounce).ConfigureAwait(false);
            string snapshot;
            lock (_sync) { snapshot = _pending!; _pending = null; }
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                await File.WriteAllTextAsync(temp, snapshot).ConfigureAwait(false);
                File.Move(temp, _path, true);
                LastError = null; WriteCount++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { LastError = ex; }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
            lock (_sync)
            {
                if (_pending != null) continue;
                _worker = null; return;
            }
        }
    }
}
