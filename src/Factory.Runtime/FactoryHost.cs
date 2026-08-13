using Factory.Agents;
using Factory.Core;
using Factory.Evolution;

namespace Factory.Runtime;

/// <summary>
/// A deployed factory: its configuration, blueprint, durable ledger, prompt registry, budget,
/// workspace, and station set, assembled and ready to work.
/// </summary>
public sealed class FactoryHost : IDisposable
{
    private readonly Ledger _ledger;

    public FactoryServices Services { get; }
    public FactoryPaths Paths { get; }
    public Blueprint Blueprint => Services.Blueprint;
    public FactoryConfig Config => Services.Config;

    private FactoryHost(FactoryServices services, Ledger ledger, FactoryPaths paths)
    {
        Services = services;
        _ledger = ledger;
        Paths = paths;
    }

    /// <summary>Deploys a factory into a codebase. Idempotent: re-running against an existing
    /// deployment updates the blueprint and seeds any new station prompts without touching
    /// the ledger, so no history is lost.</summary>
    public static FactoryHost Init(
        string repoRoot,
        Blueprint? blueprint = null,
        FactoryConfig? config = null,
        Action<string>? log = null,
        IAgentTransport? transport = null)
    {
        var paths = new FactoryPaths(repoRoot);
        paths.EnsureCreated();

        var bp = blueprint ?? Blueprint.Standard();
        var errors = bp.Validate().ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid blueprint: " + string.Join("; ", errors));

        var cfg = config ?? new FactoryConfig
        {
            Name = new DirectoryInfo(paths.RepoRoot).Name,
            BlueprintName = bp.Name,
            MaxConcurrency = bp.MaxConcurrency
        };

        File.WriteAllText(paths.BlueprintFile, FactoryJson.Write(bp, pretty: true));
        if (!File.Exists(paths.Config))
            File.WriteAllText(paths.Config, FactoryJson.Write(cfg, pretty: true));

        return Open(repoRoot, log, transport: transport);
    }

    /// <param name="transport">Overrides the Claude SDK transport. Tests substitute a fake so
    /// the full pipeline can be exercised deterministically and without spending anything.</param>
    public static FactoryHost Open(
        string repoRoot,
        Action<string>? log = null,
        BudgetSpec? budgetOverride = null,
        IAgentTransport? transport = null)
    {
        var paths = new FactoryPaths(repoRoot);
        if (!paths.Exists)
            throw new InvalidOperationException(
                $"No factory deployed at {paths.RepoRoot}. Run `factory init` (or `factory up`) there first.");

        paths.EnsureCreated();

        var config = FactoryJson.Read<FactoryConfig>(File.ReadAllText(paths.Config))
                     ?? throw new InvalidOperationException($"Unreadable factory config at {paths.Config}");

        var blueprint = File.Exists(paths.BlueprintFile)
            ? FactoryJson.Read<Blueprint>(File.ReadAllText(paths.BlueprintFile)) ?? Blueprint.Standard()
            : Blueprint.Standard();

        // Config-level links win: `factory link` writes there.
        if (config.Factories.Count > 0)
            blueprint = blueprint with { Factories = config.Factories };

        var ledger = new Ledger(paths.LedgerFile);
        var state = ledger.Replay();

        var prompts = new PromptRegistry(paths.PromptsDir);
        foreach (var (stationId, text) in KitPrompts.All)
            if (blueprint.Station(stationId) is not null)
                prompts.EnsureSeed(stationId, text);

        if (budgetOverride is not null) blueprint = blueprint with { Budget = budgetOverride };

        var budget = new BudgetGuard(blueprint.Budget);
        budget.Restore(state.Runs, state.Items);

        var cache = new ResponseCache(paths.CacheDir);
        var runner = new AgentRunner(transport ?? new CliAgentTransport(), cache);

        var services = new FactoryServices
        {
            Paths = paths,
            Config = config,
            Blueprint = blueprint,
            Ledger = ledger,
            Runner = runner,
            Prompts = prompts,
            Budget = budget,
            Workspace = new Workspace(paths.RepoRoot, paths),
            State = state,
            Log = log ?? (_ => { })
        };

        return new FactoryHost(services, ledger, paths);
    }

    /// <summary>Files work into the factory. This is the <c>in</c> port: the intake agent,
    /// a station filing follow-up work, the evolution loop, and a parent factory delegating
    /// all arrive here.
    ///
    /// <paramref name="activate"/> is false for work agents file about their own observations —
    /// review follow-ups and evolution items. It lands in Draft as a proposal rather than
    /// queued work, so a single request cannot snowball into unbounded self-directed effort.
    /// <c>factory up --include-proposed</c> opts into working it.</summary>
    public WorkItem Submit(WorkItem item, bool activate = true)
    {
        var filed = activate && item.State == WorkItemState.Draft
            ? item with { State = WorkItemState.Ready, UpdatedAt = DateTimeOffset.UtcNow }
            : item;

        Services.Record(new WorkItemFiled(filed));
        return filed;
    }

    /// <summary>Queues an item that is waiting on a person: a proposal an agent filed, or work
    /// that was blocked or failed on something outside itself. A blocked item keeps its
    /// worktree and resumes at the station it stopped on, so nothing already verified is redone.</summary>
    public WorkItem Activate(WorkItem item)
    {
        if (item.State is not (WorkItemState.Draft or WorkItemState.Blocked or WorkItemState.Failed))
            return item;

        // Resume mid-pipeline only if the work is still there. If the worktree is gone the
        // item must start over, otherwise it would resume at (say) integrate with an empty
        // checkout and fail for a second, more confusing reason.
        var resumable = item.Station is not null &&
                        Directory.Exists(Path.Combine(Paths.WorktreesDir, item.Id));

        var ready = Transition(item, WorkItemState.Ready, "activated");
        return Update(ready with { Station = resumable ? ready.Station : null });
    }

    public WorkItem Transition(WorkItem item, WorkItemState to, string? reason = null)
    {
        if (!WorkItemStates.CanTransition(item.State, to))
            throw new InvalidOperationException(
                $"Illegal transition {item.State} -> {to} for {item.Id}.");

        Services.Record(new WorkItemStateChanged(item.Id, item.State, to, reason));
        var updated = item with { State = to, UpdatedAt = DateTimeOffset.UtcNow };
        return updated;
    }

    public WorkItem Update(WorkItem item)
    {
        var updated = item with { UpdatedAt = DateTimeOffset.UtcNow };
        Services.Record(new WorkItemUpdated(updated));
        return updated;
    }

    public IStation Resolve(StationDef def) => def.Role switch
    {
        StationRole.Decompose => new DecomposeStation(),
        StationRole.Plan => new PlanStation(),
        StationRole.Implement => new ImplementStation(),
        StationRole.Verify => new VerifyStation(),
        StationRole.Review => new ReviewStation(),
        StationRole.Integrate => new IntegrateStation(),
        StationRole.Delegate => new DelegateStation(),
        StationRole.Intake => throw new InvalidOperationException(
            "Intake is an entry point, not a pipeline station; use IntakeAgent."),
        StationRole.Evolve => throw new InvalidOperationException(
            "Evolve runs on its own cadence; use EvolutionLoop."),
        _ => throw new InvalidOperationException($"No implementation for station role {def.Role}.")
    };

    public Orchestrator CreateOrchestrator() => new(this);
    public IntakeAgent CreateIntake() => new(Services);

    /// <summary>Stations that need an isolated checkout. Planning and decomposition read a
    /// digest instead, so they never pay for a worktree.</summary>
    public static bool NeedsWorkspace(StationRole role) =>
        role is StationRole.Implement or StationRole.Verify or StationRole.Review or StationRole.Integrate;

    public void Dispose() => _ledger.Dispose();
}
