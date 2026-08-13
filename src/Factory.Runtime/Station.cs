using Factory.Agents;
using Factory.Core;
using Factory.Evolution;

namespace Factory.Runtime;

/// <summary>Services a station needs, owned by the host and shared across all stations.</summary>
public sealed class FactoryServices
{
    public required FactoryPaths Paths { get; init; }
    public required FactoryConfig Config { get; init; }
    public required Blueprint Blueprint { get; init; }
    public required Ledger Ledger { get; init; }
    public required AgentRunner Runner { get; init; }
    public required PromptRegistry Prompts { get; init; }
    public required BudgetGuard Budget { get; init; }
    public required Workspace Workspace { get; init; }
    public required FactoryState State { get; init; }

    /// <summary>Transport this host was opened with, so child factories inherit it. Without
    /// this a delegate silently opens its child on the default transport, which means a
    /// composite cannot be exercised without live model calls.</summary>
    public IAgentTransport? Transport { get; init; }
    public Random Rng { get; init; } = new();
    public Action<string> Log { get; init; } = _ => { };

    /// <summary>Appends to the ledger and folds the event into live state in one step, so
    /// the in-memory view never drifts from the durable log.</summary>
    public T Record<T>(T evt) where T : FactoryEvent
    {
        Ledger.Append(evt);
        State.Apply(evt);
        return evt;
    }
}

/// <summary>Per-item state carried across the stations of one pipeline pass. Deliberately
/// small: anything worth keeping beyond the pass belongs in the ledger.</summary>
public sealed class ItemRun(WorkItem item, string workDir, int depth = 0)
{
    public WorkItem Item { get; set; } = item;
    public string WorkDir { get; set; } = workDir;
    public int Depth { get; } = depth;

    public PlanResult? Plan { get; set; }

    /// <summary>Why the previous station failed, fed back into the next attempt. This is how
    /// the implementation station learns from a failed verification without a human relaying it.</summary>
    public string? LastFailure { get; set; }

    public IReadOnlyList<AcceptanceCriterion> DeferredCriteria { get; set; } = [];
}

public sealed class StationContext
{
    public required FactoryServices Services { get; init; }
    public required StationDef Def { get; init; }
    public required ItemRun Run { get; init; }
    public CancellationToken Ct { get; init; }

    public WorkItem Item => Run.Item;
    public void Log(string message) => Services.Log($"  [{Def.Id}] {message}");
}

public sealed record StationResult
{
    public bool Success { get; init; }

    /// <summary>Whether the station's quality gate passed. A run can succeed mechanically and
    /// still fail its gate — a review that returns a clean rejection, for instance.</summary>
    public bool GatePassed { get; init; }

    public string Detail { get; init; } = "";

    /// <summary>Updated item, if the station changed it.</summary>
    public WorkItem? Item { get; init; }

    /// <summary>Work the station filed as a side output — the mechanism by which agents add
    /// work to the factory.</summary>
    public IReadOnlyList<WorkItem> NewItems { get; init; } = [];

    public RunRecord? Run { get; init; }

    /// <summary>Spend and tokens incurred inside a child factory. Reported separately from the
    /// station's own run because it is not in this factory's runner, but it must still reach
    /// the parent's totals — a composite that hides its children's cost makes every budget
    /// figure a lie.</summary>
    public decimal DelegatedCostUsd { get; init; }

    public TokenUsage DelegatedUsage { get; init; } = TokenUsage.Zero;

    /// <summary>Model calls made inside a child factory.</summary>
    public int DelegatedCalls { get; init; }

    /// <summary>Ends the pipeline early and marks the item done (used when decomposition
    /// replaces an item with its children).</summary>
    public bool ShortCircuitToDone { get; init; }

    public static StationResult Ok(string detail = "", WorkItem? item = null, RunRecord? run = null) =>
        new() { Success = true, GatePassed = true, Detail = detail, Item = item, Run = run };

    public static StationResult GateFailed(string detail, RunRecord? run = null) =>
        new() { Success = true, GatePassed = false, Detail = detail, Run = run };

    public static StationResult Failed(string detail, RunRecord? run = null) =>
        new() { Success = false, GatePassed = false, Detail = detail, Run = run };
}

public interface IStation
{
    StationRole Role { get; }
    Task<StationResult> ExecuteAsync(StationContext ctx);
}

/// <summary>Shared plumbing for stations that call a model: prompt selection, budget-capped
/// dispatch, and evidence recording.</summary>
public abstract class AgentStation : IStation
{
    public abstract StationRole Role { get; }

    public abstract Task<StationResult> ExecuteAsync(StationContext ctx);

    protected static AgentProfile ProfileFor(StationDef def, string systemPrompt) =>
        def.Profile switch
        {
            TokenProfile.Thin => AgentProfile.Thin(def.Tier, systemPrompt, def.Id, def.MaxTurns),
            TokenProfile.Thick => AgentProfile.Thick(def.Tier, def.Tools, def.Id, def.MaxTurns),
            _ => throw new InvalidOperationException($"Station '{def.Id}' has no model profile.")
        };

    /// <summary>Runs the station's model call and turns the outcome into a ledger record.
    /// Every model call in the factory goes through here, which is what makes the run table
    /// complete enough to evaluate prompts from.</summary>
    protected static async Task<(AgentRunResult Run, RunRecord Record)> InvokeAsync(
        StationContext ctx, PromptVersion prompt, string userPrompt, string? schema = null,
        bool noCache = false)
    {
        var s = ctx.Services;
        var def = ctx.Def;

        var request = new AgentRequest
        {
            Prompt = userPrompt,
            Profile = ProfileFor(def, prompt.Text),
            WorkingDirectory = ctx.Run.WorkDir,
            JsonSchema = schema,
            MaxBudgetUsd = s.Budget.RemainingForRun(ctx.Item, def.BudgetUsd),
            ContextDigest = Ids.Hash(prompt.Hash, ctx.Item.Id, ctx.Run.LastFailure),
            NoCache = noCache
        };

        var runId = Ids.New("run");
        s.Record(new RunStarted(runId, ctx.Item.Id, def.Id, prompt.Id, ModelCatalog.Resolve(def.Tier)));

        var result = await s.Runner.RunAsync(request, ct: ctx.Ct).ConfigureAwait(false);

        var record = new RunRecord
        {
            RunId = runId,
            ItemId = ctx.Item.Id,
            StationId = def.Id,
            PromptVersion = prompt.Id,
            Model = ModelCatalog.Resolve(def.Tier),
            Profile = def.Profile,
            Success = result.Success,
            CostUsd = result.CostUsd,
            Usage = result.Usage,
            Turns = result.Turns,
            DurationMs = result.DurationMs,
            StopReason = result.StopReason,
            Error = result.Error,
            CacheHit = result.CacheHit,
            Attempt = ctx.Item.Attempts
        };

        if (result.CostUsd > 0)
        {
            var total = s.Budget.Record(ctx.Item, result.CostUsd);
            s.Record(new BudgetConsumed($"item:{ctx.Item.Id}", result.CostUsd, total));
        }

        if (result.RawResult is { Length: > 0 } raw) PersistFailure(s, runId, raw);

        return (result, record);
    }

    /// <summary>Keeps the transport's terminal message for a failed run. The ledger records
    /// that a run failed and the harness's reading of why; this keeps the evidence for the
    /// cases the harness does not yet model.</summary>
    private static void PersistFailure(FactoryServices services, string runId, string raw)
    {
        try
        {
            Directory.CreateDirectory(services.Paths.RunsDir);
            File.WriteAllText(Path.Combine(services.Paths.RunsDir, $"{runId}.failed.json"), raw);
        }
        catch (IOException)
        {
            // Diagnostics are best-effort; losing them must not fail the run.
        }
    }
}
