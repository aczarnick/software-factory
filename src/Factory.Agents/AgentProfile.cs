using Factory.Core;

namespace Factory.Agents;

/// <summary>
/// A token strategy materialised as transport arguments.
///
/// The two archetypes are governed by opposite rules, and this was established by
/// measurement rather than assumption:
///
/// <list type="bullet">
/// <item><b>Thin</b> strips everything — no tools, a lean replacement system prompt, no
/// ambient settings, skills, or MCP servers. Measured: 165 billed input tokens against
/// 19,336 for a default agent call on identical work (99.1% reduction, 8.8x cheaper).</item>
/// <item><b>Thick</b> needs tools, so it cannot strip. It wins instead by holding its prefix
/// byte-identical across runs so cache reads (~10% of input rate) dominate. Critically,
/// applying thin's technique to a thick station makes it <i>worse</i>: a measured "lean
/// thick" run cost $0.018 against $0.0072 for plain thick, because replacing the preamble
/// invalidated the shared prefix and forced a 6,898-token cache write at 1.25x rate.</item>
/// </list>
///
/// Hence: thick stations keep the default preamble and put their station-specific
/// instructions in the user message, where they do not disturb the cached prefix.
/// </summary>
public sealed record AgentProfile
{
    public required string Name { get; init; }
    public TokenProfile Kind { get; init; } = TokenProfile.Thin;
    public ModelTier Tier { get; init; } = ModelTier.Sonnet;

    /// <summary>Least-privilege tool allowlist. Always empty for thin.</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    public int MaxTurns { get; init; } = 1;

    /// <summary>Replacement system prompt. Thin only — setting this on a thick profile
    /// invalidates the shared cache prefix and costs more than it saves.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Permission mode for tool-using stations.</summary>
    public string PermissionMode { get; init; } = "bypassPermissions";

    /// <summary>Strip per-machine system prompt sections so the cache prefix is identical
    /// across runs and machines. Only meaningful with the default preamble, i.e. thick.</summary>
    public bool StablePrefix { get; init; } = true;

    public bool IsThin => Kind == TokenProfile.Thin;

    public static AgentProfile Thin(ModelTier tier, string systemPrompt, string name = "thin", int maxTurns = 1) =>
        new()
        {
            Name = name,
            Kind = TokenProfile.Thin,
            Tier = tier,
            Tools = [],
            SystemPrompt = systemPrompt,
            MaxTurns = maxTurns
        };

    public static AgentProfile Thick(ModelTier tier, IReadOnlyList<string> tools, string name = "thick", int maxTurns = 40) =>
        new()
        {
            Name = name,
            Kind = TokenProfile.Thick,
            Tier = tier,
            Tools = tools,
            SystemPrompt = null,   // deliberately: preserve the cacheable default preamble
            MaxTurns = maxTurns,
            StablePrefix = true
        };

    /// <summary>
    /// Structured output costs a turn of its own: the transport spends one turn producing the
    /// answer and another emitting it against the schema, so a station asking for JSON with
    /// <c>--max-turns 1</c> always terminates as <c>error_max_turns</c> before returning
    /// anything. Measured, not assumed — a one-turn structured call reports num_turns 2.
    /// </summary>
    public const int StructuredOutputTurnFloor = 3;

    /// <summary>Builds the transport argument list for this profile.</summary>
    /// <param name="structuredOutput">Whether a JSON schema will be attached to the request.</param>
    public List<string> ToArgs(bool structuredOutput = false)
    {
        var maxTurns = structuredOutput ? Math.Max(MaxTurns, StructuredOutputTurnFloor) : MaxTurns;

        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--model", ModelCatalog.Resolve(Tier),
            "--max-turns", maxTurns.ToString()
        };

        // Ambient-context stripping. Applies to both archetypes: skills, project settings,
        // and MCP servers are pure overhead for a station that was given an explicit job.
        args.AddRange(["--setting-sources", ""]);
        args.Add("--disable-slash-commands");
        args.Add("--strict-mcp-config");

        if (IsThin)
        {
            args.AddRange(["--tools", ""]);
            if (!string.IsNullOrWhiteSpace(SystemPrompt))
                args.AddRange(["--system-prompt", SystemPrompt]);
            args.Add("--no-session-persistence");
        }
        else
        {
            args.AddRange(["--tools", string.Join(",", Tools)]);
            args.AddRange(["--permission-mode", PermissionMode]);
            if (StablePrefix) args.Add("--exclude-dynamic-system-prompt-sections");
        }

        return args;
    }
}
