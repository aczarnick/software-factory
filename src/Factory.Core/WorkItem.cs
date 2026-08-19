namespace Factory.Core;

public enum WorkItemState
{
    Draft,
    Ready,
    InProgress,
    InReview,
    Verified,
    Done,
    Blocked,
    Failed,
    Cancelled,
    Superseded
}

/// <summary>What kind of work an item is. The factory's pipeline treats every kind the same way.
///
/// The kinds past <see cref="Improvement"/> were added so an <see cref="IWorkItemStore"/> with its own
/// item vocabulary can round-trip a type the factory did not file, rather than flattening it to
/// <see cref="Feature"/> and writing that back over the store's own value. They are not confined to
/// that: <c>ItemContract.ToDomain</c> parses a decompose station's model output into this enum, and
/// <c>factory add --kind</c> parses an operator's word, so both now mint the new members too —
/// <c>factory add --kind milestone</c> files a Milestone where it used to file a Feature.
///
/// That widening is deliberate rather than an oversight. Nothing in the pipeline branches on kind, so
/// no station behaves differently; refusing the new words at the CLI would mean a special case listing
/// which members an operator may name, to protect nothing.
///
/// The cost worth stating: this makes the enum a growing union of every backlog provider's vocabulary,
/// and each growth is a plugin-ABI event (see <see cref="FactoryVersion.ContractVersion"/>). The
/// provider-agnostic alternative — carrying the store's raw type string opaquely on the item — is
/// named and rejected on cost grounds in <c>BeadMapper.KindFor</c>.</summary>
public enum WorkItemKind
{
    Feature,
    Bug,
    Chore,
    Refactor,
    Spike,
    Improvement,
    Task,
    Epic,
    Decision,
    Story,
    Milestone
}

public enum ProvenanceKind
{
    /// <summary>Filed by a person, normally through the intake conversation.</summary>
    Human,

    /// <summary>Filed by a station as a side output of doing other work.</summary>
    Agent,

    /// <summary>Filed by the evolution loop against the factory's own defects.</summary>
    Evolution
}

/// <summary>Who put this work into the factory. Kept so self-filed work is auditable
/// and can be budgeted separately from user work.</summary>
public sealed record Provenance(ProvenanceKind Kind, string? Source = null)
{
    public static Provenance Human { get; } = new(ProvenanceKind.Human);
    public static Provenance FromAgent(string stationId) => new(ProvenanceKind.Agent, stationId);
    public static Provenance FromEvolution(string detail) => new(ProvenanceKind.Evolution, detail);

    public override string ToString() => Source is null ? Kind.ToString() : $"{Kind}({Source})";
}

/// <summary>The unit of production. Everything the factory does is in service of moving
/// work items from Draft to Done through verified state transitions.</summary>
public sealed record WorkItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }

    /// <summary>The underlying goal, in the requester's terms.</summary>
    public string Intent { get; init; } = "";

    public WorkItemKind Kind { get; init; } = WorkItemKind.Feature;
    public IReadOnlyList<string> Requirements { get; init; } = [];
    public IReadOnlyList<AcceptanceCriterion> AcceptanceCriteria { get; init; } = [];

    public WorkItemState State { get; init; } = WorkItemState.Draft;

    /// <summary>Dispatch priority, <see cref="Priorities.Highest"/> to
    /// <see cref="Priorities.Lowest"/>. Lower sorts first.
    ///
    /// Brought into the band on the way in rather than merely documented as being in it. The band is
    /// the backlog store's — beads refuses anything outside it with a non-zero exit, which the store
    /// raises as a halt — so this is the one place that can guarantee no item the factory holds is an
    /// item the backlog will refuse. Every construction path lands here: object initialiser,
    /// <c>with</c>, and deserialisation, which is how a ledger written before the band was narrowed
    /// replays without stopping the factory.</summary>
    public int Priority
    {
        get => _priority;
        init => _priority = Priorities.Clamp(value);
    }

    private readonly int _priority = Priorities.Default;

    public IReadOnlyList<string> Labels { get; init; } = [];
    public string? ParentId { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Per-item spend ceiling. Falls back to the blueprint budget when null.</summary>
    public decimal? BudgetUsd { get; init; }

    public Provenance Provenance { get; init; } = Provenance.Human;

    /// <summary>Checkout holding the claim on this item, as the backlog store records it. Owned by
    /// the backlog rather than by this machine — unlike <see cref="Station"/> and
    /// <see cref="Worktree"/>, which are local run state — so reconciling from the backlog is
    /// entitled to overwrite it. Null when nothing holds the item, or when the store keeps no
    /// claims.</summary>
    public string? Owner { get; init; }

    /// <summary>The backlog store's own word for this item's status, kept verbatim when it is one the
    /// factory has no <see cref="WorkItemState"/> of its own for; null whenever
    /// <see cref="State"/> is a faithful reading of it. Store-owned like <see cref="Owner"/>, so
    /// reconciling from the backlog is entitled to overwrite it.
    ///
    /// It exists so a status the factory cannot name is a status the factory does not destroy: the
    /// read has to fall back to <em>some</em> state, and without the original word the next write
    /// renders that fallback back over the store's own cell.</summary>
    public string? StoreStatus { get; init; }

    /// <summary>Station currently holding the item, when InProgress.</summary>
    public string? Station { get; init; }

    /// <summary>Isolated workspace path (git worktree) while in flight.</summary>
    public string? Worktree { get; init; }

    /// <summary>Explicit assumptions recorded by non-interactive intake.</summary>
    public IReadOnlyList<string> Assumptions { get; init; } = [];

    public int Attempts { get; init; }
    public string? LastError { get; init; }
    public decimal SpentUsd { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True when every acceptance criterion can be checked without a model call.</summary>
    public bool IsFullyDeterministic =>
        AcceptanceCriteria.Count > 0 && AcceptanceCriteria.All(c => c.Verification.IsDeterministic);

    public static WorkItem Create(string title, string intent = "", WorkItemKind kind = WorkItemKind.Feature) =>
        new() { Id = Ids.New("wi"), Title = title, Intent = intent, Kind = kind };
}

public static class WorkItemStates
{
    private static readonly Dictionary<WorkItemState, WorkItemState[]> Allowed = new()
    {
        [WorkItemState.Draft] = [WorkItemState.Ready, WorkItemState.Cancelled, WorkItemState.Blocked],
        [WorkItemState.Ready] = [WorkItemState.InProgress, WorkItemState.Blocked, WorkItemState.Cancelled],
        // Ready is reachable from in-flight states so a crashed factory can requeue orphans.
        // Superseded is reachable only from InProgress: decompose is the one thing that replaces an
        // item with the children that carry its work, and it does so while the item is in flight.
        [WorkItemState.InProgress] = [WorkItemState.InReview, WorkItemState.Failed, WorkItemState.Blocked, WorkItemState.Cancelled, WorkItemState.Ready, WorkItemState.Superseded],
        [WorkItemState.InReview] = [WorkItemState.Verified, WorkItemState.InProgress, WorkItemState.Failed, WorkItemState.Blocked, WorkItemState.Ready],
        [WorkItemState.Verified] = [WorkItemState.Done, WorkItemState.Failed],
        [WorkItemState.Blocked] = [WorkItemState.Ready, WorkItemState.Cancelled],
        // Failed is retryable: the orchestrator can put it back on the queue.
        [WorkItemState.Failed] = [WorkItemState.Ready, WorkItemState.Cancelled],
        [WorkItemState.Done] = [],
        [WorkItemState.Cancelled] = [],
        [WorkItemState.Superseded] = []
    };

    public static bool IsTerminal(WorkItemState s) => Allowed[s].Length == 0;

    public static bool CanTransition(WorkItemState from, WorkItemState to) =>
        from == to || Allowed[from].Contains(to);

    public static IReadOnlyList<WorkItemState> NextFrom(WorkItemState s) => Allowed[s];
}
