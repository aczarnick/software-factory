using Factory.Core;

namespace Factory.Agents;

public sealed record AgentRunnerOptions
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromSeconds(2);
    public bool CacheEnabled { get; init; } = true;
}

/// <summary>
/// The harness entry point stations call. Layers the token economy over the raw transport:
/// response cache first, then a budget-capped model call, with backoff on transient
/// transport failures.
/// </summary>
public sealed class AgentRunner(
    IAgentTransport transport,
    ResponseCache? cache = null,
    AgentRunnerOptions? options = null,
    UsageGovernor? governor = null)
{
    private readonly AgentRunnerOptions _opts = options ?? new AgentRunnerOptions();

    public ResponseCache? Cache { get; } = cache;

    /// <summary>Watches the provider's usage windows and paces work to stay inside them.</summary>
    public UsageGovernor Governor { get; } = governor ?? new UsageGovernor();

    /// <summary>Cumulative spend observed through this runner, for reporting.</summary>
    public decimal TotalCostUsd { get; private set; }
    public TokenUsage TotalUsage { get; private set; } = TokenUsage.Zero;
    public int Calls { get; private set; }
    public int CacheHits { get; private set; }

    public async Task<AgentRunResult> RunAsync(
        AgentRequest request,
        Action<AgentEvent>? onEvent = null,
        CancellationToken ct = default)
    {
        if (_opts.CacheEnabled && Cache is not null && Cache.TryGet(request, out var cached))
        {
            CacheHits++;
            return cached;
        }

        AgentRunResult result = AgentRunResult.Failure("not attempted");

        // Every run feeds the governor as well as obeying it: the transport reports the
        // usage windows on the way out, which is how the next dispatch decision is made.
        void Watch(AgentEvent evt)
        {
            Governor.Observe(evt);
            onEvent?.Invoke(evt);
        }

        for (var attempt = 0; attempt <= _opts.MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (!await Governor.AwaitClearanceAsync(ct).ConfigureAwait(false))
                return AgentRunResult.Failure("held back by the model usage limit");

            result = await transport.RunAsync(request, Watch, ct).ConfigureAwait(false);

            Calls++;
            TotalCostUsd += result.CostUsd;
            TotalUsage += result.Usage;

            if (result.Success) break;

            Governor.ObserveRejection(result.Error);

            if (!IsTransient(result.Error)) break;
            if (attempt == _opts.MaxRetries) break;

            var delay = _opts.BaseBackoff * Math.Pow(2, attempt);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        if (result.Success && _opts.CacheEnabled) Cache?.Put(request, result);
        return result;
    }

    /// <summary>Runs with a JSON schema and deserialises. One repair attempt is made when the
    /// model returns unparseable output, then it gives up rather than burning budget.</summary>
    public async Task<(bool Ok, T? Value, AgentRunResult Run, string? Error)> RunStructuredAsync<T>(
        AgentRequest request,
        Action<AgentEvent>? onEvent = null,
        CancellationToken ct = default)
    {
        var run = await RunAsync(request, onEvent, ct).ConfigureAwait(false);
        if (!run.Success) return (false, default, run, run.Error);

        if (run.TryStructured<T>(out var value, out var parseError))
            return (true, value, run, null);

        var repair = request with
        {
            Prompt = request.Prompt +
                     $"\n\nYour previous reply could not be parsed ({parseError}). " +
                     "Reply with the JSON object only — no prose, no code fence.",
            NoCache = true
        };

        var retry = await RunAsync(repair, onEvent, ct).ConfigureAwait(false);
        if (retry.Success && retry.TryStructured<T>(out var repaired, out _))
            return (true, repaired, retry, null);

        return (false, default, retry, parseError);
    }

    private static bool IsTransient(string? error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        ReadOnlySpan<string> markers =
        [
            "rate_limit", "overloaded", "timed out", "503", "529", "502",
            "connection", "temporarily", "ECONNRESET"
        ];
        foreach (var m in markers)
            if (error.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
