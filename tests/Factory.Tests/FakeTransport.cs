using Factory.Agents;
using Factory.Core;

namespace Factory.Tests;

/// <summary>
/// Stands in for the Claude SDK so the whole pipeline can be exercised deterministically and
/// for free. Responders are keyed by profile name, which is the station id — so a test can
/// script exactly what each station returns.
/// </summary>
public sealed class FakeTransport : IAgentTransport
{
    private readonly Dictionary<string, Func<AgentRequest, AgentRunResult>> _responders = new();

    public List<AgentRequest> Requests { get; } = [];
    public int Calls => Requests.Count;

    public FakeTransport Respond(string station, string text, decimal cost = 0.001m)
        => Respond(station, _ => Success(text, cost));

    public FakeTransport Respond(string station, Func<AgentRequest, AgentRunResult> responder)
    {
        _responders[station] = responder;
        return this;
    }

    public FakeTransport Fail(string station, string error)
        => Respond(station, _ => AgentRunResult.Failure(error));

    public Task<AgentRunResult> RunAsync(
        AgentRequest request, Action<AgentEvent>? onEvent = null, CancellationToken ct = default)
    {
        Requests.Add(request);

        var responder = _responders.GetValueOrDefault(request.Profile.Name);
        return Task.FromResult(responder?.Invoke(request)
            ?? AgentRunResult.Failure($"no fake responder for station '{request.Profile.Name}'"));
    }

    /// <summary>A run stopped by the turn ceiling with work still in hand — the case worth
    /// continuing rather than restarting.</summary>
    public static AgentRunResult OutOfTurns(string sessionId, decimal cost = 1.90m) => new()
    {
        Success = false,
        Error = "error_max_turns",
        ExhaustedTurns = true,
        SessionId = sessionId,
        CostUsd = cost,
        Turns = 40,
        StopReason = "tool_use"
    };

    public static AgentRunResult Success(string text, decimal cost = 0.001m) => new()
    {
        Success = true,
        Text = text,
        CostUsd = cost,
        Usage = new TokenUsage(120, 60, 0, 0),
        Turns = 1,
        StopReason = "end_turn",
        SessionId = "fake-session"
    };
}
