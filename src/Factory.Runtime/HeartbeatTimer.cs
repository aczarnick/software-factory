namespace Factory.Runtime;

/// <summary>
/// Periodically invokes a write-heartbeat delegate on a configurable cadence using a
/// <see cref="System.Threading.Timer"/>. Start/Stop let a caller activate the timer only
/// while work is running; Dispose is idempotent and never throws.
/// </summary>
public sealed class HeartbeatTimer : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(3);

    private readonly Func<Task> _writeHeartbeatAsync;
    private readonly TimeSpan _interval;
    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private bool _disposed;
    private int _ticking;

    public HeartbeatTimer(Func<Task> writeHeartbeatAsync, TimeSpan? interval = null)
    {
        _writeHeartbeatAsync = writeHeartbeatAsync;
        _interval = interval ?? DefaultInterval;
        _timer = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public TimeSpan Interval => _interval;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _timer.Change(_interval, _interval);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void OnTick(object? state)
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
            return;

        _ = RunTickAsync();
    }

    private async Task RunTickAsync()
    {
        try
        {
            await _writeHeartbeatAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: a failed heartbeat write must not tear down the timer.
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            try
            {
                _timer.Dispose();
            }
            catch
            {
                // Dispose must never throw.
            }
        }
    }
}
