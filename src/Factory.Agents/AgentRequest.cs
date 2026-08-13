namespace Factory.Agents;

public sealed record AgentRequest
{
    public required string Prompt { get; init; }
    public required AgentProfile Profile { get; init; }

    /// <summary>Directory the agent runs in. For thick stations this is the item's
    /// isolated worktree.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>JSON Schema for structured output. Removes brittle text parsing and
    /// bounds output tokens.</summary>
    public string? JsonSchema { get; init; }

    /// <summary>Resume an existing session to land on a warm prompt cache.</summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>Hard spend ceiling handed to the transport.</summary>
    public decimal? MaxBudgetUsd { get; init; }

    public IReadOnlyList<string> AddDirs { get; init; } = [];

    /// <summary>Extra key mixed into the response cache key — normally a digest of the
    /// workspace state the prompt depends on.</summary>
    public string? ContextDigest { get; init; }

    /// <summary>Skip the response cache for this request (e.g. non-deterministic stations).</summary>
    public bool NoCache { get; init; }

    public string CacheKey => Core.Ids.Hash(
        Profile.Name,
        Profile.Kind.ToString(),
        ModelCatalog.Resolve(Profile.Tier),
        Profile.SystemPrompt,
        string.Join(",", Profile.Tools),
        Prompt,
        JsonSchema,
        ContextDigest);
}
