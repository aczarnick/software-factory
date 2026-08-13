namespace Factory.Agents;

/// <summary>
/// The seam between the factory and the Claude SDK. Production uses
/// <see cref="CliAgentTransport"/>; tests substitute a fake so the whole pipeline can be
/// exercised deterministically and for free.
/// </summary>
public interface IAgentTransport
{
    Task<AgentRunResult> RunAsync(
        AgentRequest request,
        Action<AgentEvent>? onEvent = null,
        CancellationToken ct = default);
}
