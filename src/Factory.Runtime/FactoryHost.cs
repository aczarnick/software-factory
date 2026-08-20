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
    private readonly IRunHistory _history;

    public FactoryServices Services { get; }
    public FactoryPaths Paths { get; }
    public Blueprint Blueprint => Services.Blueprint;
    public FactoryConfig Config => Services.Config;

    private FactoryHost(FactoryServices services, IRunHistory history, FactoryPaths paths)
    {
        Services = services;
        _history = history;
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

        var log2 = log ?? (_ => { });
        var pluginLog = (string message) => log2($"  [plugin] {message}");
        var backlogLog = (string message) => log2($"  [backlog] {message}");
        var sinkLog = (string message) => log2($"  [sink] {message}");

        var registry = new ProviderRegistry(pluginLog);

        registry.Register<IRunHistory>("jsonl", _ => new JsonlRunHistory(paths.LedgerFile));
        PluginCatalog.LoadInto(registry, paths.PluginsDir, pluginLog);

        var writer = registry.Resolve<IRunHistory>(new ProviderRef(config.RunHistory.Writer));

        var sinks = config.RunHistory.Sinks
            .Select(reference => BuildSink(registry, reference, sinkLog))
            .OfType<IRunHistorySink>()
            .ToList();

        var history = new FanOutRunHistory(writer, sinks);
        var state = FactoryState.Replay(history.ReadFrom(0));

        registry.Register<IWorkItemStore>("ledger", _ => new LedgerWorkItemStore(history, state));

        registry.Register<IWorkItemStore>("beads", reference =>
        {
            var cli = new BeadsCli(paths.RepoRoot, config.Name);
            BeadsDeployment.EnsureInitialised(cli, reference.Options.GetValueOrDefault("prefix", "wi"), backlogLog);
            return new BeadsWorkItemStore(cli, config.Name, backlogLog);
        });

        var items = new GuardedWorkItemStore(
            WithAuditCopy(ResolveStore(registry, config.WorkItemStore), history, state, backlogLog),
            config.WorkItemStore.Provider);

        // The backlog store is the authority, so the fold is corrected from it before anything
        // reads state. Sync and reclaim are best-effort by contract: beads holds a complete local
        // database, so an absent or unreachable remote degrades sharing, not working.
        items.Sync();
        BacklogReconciler.Reconcile(items, state, history, backlogLog);

        foreach (var reclaimed in items.Reclaim(Leases.ObservedShortest * 2))
            backlogLog($"reclaimed {reclaimed.Id} from a stale lease");

        var prompts = new PromptRegistry(paths.PromptsDir);
        foreach (var (stationId, text) in KitPrompts.All)
            if (blueprint.Station(stationId) is not null)
                prompts.EnsureSeed(stationId, text);

        if (budgetOverride is not null) blueprint = blueprint with { Budget = budgetOverride };

        var budget = new BudgetGuard(blueprint.Budget);
        budget.Restore(history.ForBudget());

        var cache = new ResponseCache(paths.CacheDir);

        var governor = new UsageGovernor(statePath: paths.UsageFile);
        governor.Changed += (_, e) => (log ?? (_ => { }))($"  [usage] {e.Message}");

        var runner = new AgentRunner(transport ?? new CliAgentTransport(), cache, governor: governor);

        var services = new FactoryServices
        {
            Paths = paths,
            Config = config,
            Blueprint = blueprint,
            History = history,
            Items = items,
            Runner = runner,
            Prompts = prompts,
            Budget = budget,
            Workspace = new Workspace(paths.RepoRoot, paths),
            State = state,
            Transport = transport,
            Log = log ?? (_ => { })
        };

        return new FactoryHost(services, history, paths);
    }

    // A sink that cannot be built is the same class of event as one that cannot be reached:
    // logged and dropped. That covers a name no provider answers to as well — a mistyped
    // tracing backend must not stop a factory whose durable writer is fine.
    private static IRunHistorySink? BuildSink(
        ProviderRegistry registry, ProviderRef reference, Action<string> log)
    {
        try
        {
            return new GuardedRunHistorySink(
                registry.Resolve<IRunHistorySink>(reference), reference.Provider, maxFailures: 3, log);
        }
        catch (Exception ex)
        {
            log($"sink '{reference.Provider}' could not be created and is dropped: {ex.Message}");
            return null;
        }
    }

    // The ledger provider's own writes already are the audit copy; every other provider needs one
    // made for it, in backlog-then-ledger order.
    private static IWorkItemStore WithAuditCopy(
        IWorkItemStore store, IRunHistory history, FactoryState state, Action<string> log) =>
        store is LedgerWorkItemStore
            ? store
            : new LedgerMirroringWorkItemStore(store, history, state, log);

    // Construction sits inside the store's failure boundary too: a provider that fails while
    // connecting must halt as the typed failure the port promises, not as a raw plugin exception.
    private static IWorkItemStore ResolveStore(ProviderRegistry registry, ProviderRef reference)
    {
        try
        {
            return registry.Resolve<IWorkItemStore>(reference);
        }
        catch (Exception ex) when (ex is not WorkItemStoreException)
        {
            throw new WorkItemStoreException(reference.Provider, "Create", ex);
        }
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

        return Services.Items.Add(filed);
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

    public WorkItem Transition(WorkItem item, WorkItemState to, string? reason = null) =>
        Services.Items.Transition(item, to, reason);

    public WorkItem Update(WorkItem item) => Services.Items.Update(item);

    public IStation Resolve(StationDef def) => def.Role switch
    {
        StationRole.Decompose => new DecomposeStation(),
        StationRole.Plan => new PlanStation(),
        StationRole.Implement => new ImplementStation(),
        StationRole.Check => new CheckStation(
            new DefaultRemediationRunner(Services.Workspace.RepoRoot),
            repoStateProvider: new GitRepoStateProvider(Services.Workspace.RepoRoot)),
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
        role is StationRole.Implement or StationRole.Check or StationRole.Verify
             or StationRole.Review or StationRole.Integrate;

    public void Dispose() => _history.Dispose();
}
