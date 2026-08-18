using Factory.Core;

namespace Factory.Runtime;

/// <summary>Contains sink failures. The durable writer already holds the record, so a sink
/// that cannot be reached is a warning, not an outage — and one that keeps failing is
/// switched off rather than retried on every event. The failure bookkeeping is locked because
/// <see cref="IRunHistorySink.Emit"/> is called from parallel station tasks: an approximate
/// ceiling would make "disabled after N failures" mean nothing under concurrency.</summary>
public sealed class GuardedRunHistorySink(
    IRunHistorySink inner, string providerName, int maxFailures, Action<string> log) : IRunHistorySink
{
    private readonly Lock _gate = new();
    private int _failures;
    private volatile bool _disabled;

    public void Emit(FactoryEvent evt) => Attempt(() => inner.Emit(evt));

    public void Flush() => Attempt(inner.Flush);

    private void Attempt(Action action)
    {
        if (_disabled) return;

        try
        {
            action();
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
        }
    }

    private void RecordFailure(Exception ex)
    {
        lock (_gate)
        {
            if (_disabled) return;

            _failures++;
            log($"sink '{providerName}' failed ({_failures}/{maxFailures}): {ex.Message}");

            if (_failures < maxFailures) return;
            _disabled = true;
            log($"sink '{providerName}' disabled after {maxFailures} failures");
        }
    }
}
