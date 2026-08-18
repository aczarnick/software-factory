using Factory.Core;

namespace Factory.Runtime;

/// <summary>Contains sink failures. The durable writer already holds the record, so a sink
/// that cannot be reached is a warning, not an outage — and one that keeps failing is
/// switched off rather than retried on every event.</summary>
public sealed class GuardedRunHistorySink(
    IRunHistorySink inner, string providerName, int maxFailures, Action<string> log) : IRunHistorySink
{
    private int _failures;
    private bool _disabled;

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
            _failures++;
            log($"sink '{providerName}' failed ({_failures}/{maxFailures}): {ex.Message}");

            if (_failures < maxFailures) return;
            _disabled = true;
            log($"sink '{providerName}' disabled after {maxFailures} failures");
        }
    }
}
