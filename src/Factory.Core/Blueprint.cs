namespace Factory.Core;

/// <summary>What a station does. The engine is generic over these; the blueprint decides
/// which appear, in what order, and with what budget.</summary>
public enum StationRole
{
    Intake,
    Decompose,
    Plan,
    Implement,

    /// <summary>Runs the repository's own toolchain — compiler, tests, linter. Deterministic,
    /// zero tokens, and not authored by the thing it is checking.</summary>
    Check,

    Verify,
    Review,
    Integrate,
    Evolve,
    Delegate
}

/// <summary>
/// Token strategy for a station. The two archetypes are governed by opposite rules:
/// <list type="bullet">
/// <item><b>Thin</b> — no tools, lean system prompt, no ambient settings. Wins by removing
/// context: measured at 165 input tokens vs 19,336 for a default agent call.</item>
/// <item><b>Thick</b> — needs tools, so it wins by keeping its prefix byte-identical across
/// runs so cache reads (~10% rate) dominate. Stripping a thick station makes it
/// <i>more</i> expensive, because it invalidates the shared prefix.</item>
/// </list>
/// </summary>
public enum TokenProfile
{
    /// <summary>No model call at all — deterministic station.</summary>
    None,
    Thin,
    Thick
}

public enum ModelTier
{
    None,
    Haiku,
    Sonnet,
    Opus
}

public sealed record StationDef
{
    public required string Id { get; init; }
    public required StationRole Role { get; init; }

    public ModelTier Tier { get; init; } = ModelTier.Sonnet;
    public TokenProfile Profile { get; init; } = TokenProfile.Thin;

    /// <summary>Least-privilege tool allowlist. Empty means no tools at all.</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>Prompt registry key. Defaults to the station id.</summary>
    public string? PromptId { get; init; }

    public int MaxTurns { get; init; } = 12;

    /// <summary>Per-run spend ceiling, passed to the transport as a hard stop.</summary>
    public decimal? BudgetUsd { get; init; }

    public int Retries { get; init; } = 1;

    /// <summary>Child factory name, for <see cref="StationRole.Delegate"/>.</summary>
    public string? DelegateTo { get; init; }

    /// <summary>Station to route back to when this station's gate fails.</summary>
    public string? OnFail { get; init; }

    /// <summary>When the gate keeps failing, stop and ask a human rather than burn budget.</summary>
    public bool EscalateToHuman { get; init; }

    /// <summary>Hard cap on injected context, in bytes. Bounds the worst case.</summary>
    public int ContextByteCap { get; init; } = 24_000;

    public string EffectivePromptId => PromptId ?? Id;
}

public sealed record Blueprint
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public BudgetSpec Budget { get; init; } = new();
    public IReadOnlyList<StationDef> Stations { get; init; } = [];

    /// <summary>Ordered station ids forming the default route for a work item.</summary>
    public IReadOnlyList<string> Pipeline { get; init; } = [];

    public int MaxConcurrency { get; init; } = 2;
    public int MaxDelegationDepth { get; init; } = 3;

    /// <summary>Child factories linked into this one, by name -> path.</summary>
    public IReadOnlyDictionary<string, string> Factories { get; init; } =
        new Dictionary<string, string>();

    public StationDef? Station(string id) => Stations.FirstOrDefault(s => s.Id == id);

    public StationDef Require(string id) =>
        Station(id) ?? throw new InvalidOperationException($"Blueprint '{Name}' has no station '{id}'.");

    /// <summary>Next station in the pipeline, or null when the item is finished.</summary>
    public string? NextAfter(string? stationId)
    {
        if (Pipeline.Count == 0) return null;
        if (stationId is null) return Pipeline[0];
        var i = Pipeline.ToList().IndexOf(stationId);
        if (i < 0) return Pipeline[0];
        return i + 1 < Pipeline.Count ? Pipeline[i + 1] : null;
    }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) yield return "blueprint name is required";

        foreach (var id in Pipeline)
            if (Station(id) is null)
                yield return $"pipeline references unknown station '{id}'";

        foreach (var s in Stations)
        {
            if (s.OnFail is not null && Station(s.OnFail) is null)
                yield return $"station '{s.Id}' onFail references unknown station '{s.OnFail}'";
            if (s.Role == StationRole.Delegate && string.IsNullOrWhiteSpace(s.DelegateTo))
                yield return $"delegate station '{s.Id}' must set delegateTo";
            if (s.Role == StationRole.Delegate && s.DelegateTo is { } d && !Factories.ContainsKey(d))
                yield return $"station '{s.Id}' delegates to unlinked factory '{d}'";
            if (s.Profile == TokenProfile.Thin && s.Tools.Count > 0)
                yield return $"station '{s.Id}' is thin but declares tools; thin means no tools";
        }

        var dupes = Stations.GroupBy(s => s.Id).Where(g => g.Count() > 1).Select(g => g.Key);
        foreach (var d in dupes) yield return $"duplicate station id '{d}'";
    }

    /// <summary>The default general-purpose blueprint: the pipeline that turns one
    /// prompt into verified, integrated software.</summary>
    public static Blueprint Standard() => new()
    {
        Name = "standard",
        Description = "General-purpose software production pipeline.",
        Budget = new BudgetSpec(),
        MaxConcurrency = 2,
        // check runs before verify: a repository that no longer compiles makes every
        // acceptance criterion meaningless, and the compiler says why in one step.
        Pipeline = ["decompose", "plan", "implement", "check", "verify", "review", "integrate"],
        Stations =
        [
            new StationDef
            {
                Id = "intake", Role = StationRole.Intake,
                Tier = ModelTier.Sonnet, Profile = TokenProfile.Thin,
                MaxTurns = 4, BudgetUsd = 0.50m
            },
            new StationDef
            {
                Id = "decompose", Role = StationRole.Decompose,
                Tier = ModelTier.Sonnet, Profile = TokenProfile.Thin,
                MaxTurns = 4, BudgetUsd = 0.50m
            },
            new StationDef
            {
                Id = "plan", Role = StationRole.Plan,
                Tier = ModelTier.Sonnet, Profile = TokenProfile.Thin,
                MaxTurns = 4, BudgetUsd = 0.40m
            },
            new StationDef
            {
                Id = "implement", Role = StationRole.Implement,
                Tier = ModelTier.Sonnet, Profile = TokenProfile.Thick,
                Tools = ["Read", "Write", "Edit", "Bash", "Grep", "Glob"],
                MaxTurns = 40, BudgetUsd = 2.00m, Retries = 2, OnFail = "implement"
            },
            new StationDef
            {
                // Deterministic: the repository's own compiler, tests and linter. Zero tokens.
                Id = "check", Role = StationRole.Check,
                Tier = ModelTier.None, Profile = TokenProfile.None,
                OnFail = "implement", Retries = 2
            },
            new StationDef
            {
                // Deterministic: runs acceptance criteria as commands. Zero tokens.
                Id = "verify", Role = StationRole.Verify,
                Tier = ModelTier.None, Profile = TokenProfile.None,
                // Failing verification twice before giving up is normal: the first repair
                // cycle often uncovers the real problem. Retries here are cheap to gate
                // (the check itself costs nothing) and only the repair costs tokens.
                OnFail = "implement", Retries = 2
            },
            new StationDef
            {
                Id = "review", Role = StationRole.Review,
                Tier = ModelTier.Haiku, Profile = TokenProfile.Thin,
                MaxTurns = 4, BudgetUsd = 0.30m, OnFail = "implement", EscalateToHuman = true
            },
            new StationDef
            {
                Id = "integrate", Role = StationRole.Integrate,
                Tier = ModelTier.None, Profile = TokenProfile.None,
                // Integration fails for reasons outside the work itself — a dirty mainline,
                // a conflicting merge. The work is already verified by this point, so block
                // and keep the worktree rather than failing, which would discard it.
                EscalateToHuman = true
            },
            new StationDef
            {
                Id = "evolve", Role = StationRole.Evolve,
                Tier = ModelTier.Sonnet, Profile = TokenProfile.Thin,
                MaxTurns = 4, BudgetUsd = 0.50m
            }
        ]
    };

    /// <summary>
    /// A factory whose work is done by other factories. It decomposes a request and routes
    /// each child item through linked factories in turn, so several specialised factories
    /// present as one. Since a delegate is just a station, a composite is itself a factory
    /// and can be linked into a larger one.
    /// </summary>
    public static Blueprint Composite(string name, IReadOnlyDictionary<string, string> children)
    {
        if (children.Count == 0)
            throw new ArgumentException("A composite factory needs at least one linked child.", nameof(children));

        var standard = Standard();

        var delegates = children.Keys.Select(child => new StationDef
        {
            Id = child,
            Role = StationRole.Delegate,
            Tier = ModelTier.None,
            Profile = TokenProfile.None,
            DelegateTo = child,
            Retries = 0,
            EscalateToHuman = true
        }).ToList();

        return new Blueprint
        {
            Name = name,
            Description = $"Composite of {string.Join(", ", children.Keys)}.",
            Budget = standard.Budget,
            MaxConcurrency = standard.MaxConcurrency,
            Factories = children.ToDictionary(k => k.Key, v => v.Value),
            Pipeline = ["decompose", .. delegates.Select(d => d.Id)],
            Stations =
            [
                standard.Require("intake"),
                standard.Require("decompose"),
                standard.Require("evolve"),
                .. delegates
            ]
        };
    }
}
