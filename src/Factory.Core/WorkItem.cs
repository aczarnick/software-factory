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
    Cancelled
}

/// <summary>What kind of work an item is. The factory's pipeline treats every kind the same way; the
/// kinds past <see cref="Improvement"/> exist so an <see cref="IWorkItemStore"/> that has its own item
/// vocabulary can round-trip a type the factory did not file, rather than flattening it to
/// <see cref="Feature"/> and writing that back over the store's own value.</summary>
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
    /// <see cref="Priorities.Lowest"/>. Lower sorts first.</summary>
    public int Priority { get; init; } = Priorities.Default;

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
        [WorkItemState.InProgress] = [WorkItemState.InReview, WorkItemState.Failed, WorkItemState.Blocked, WorkItemState.Cancelled, WorkItemState.Ready],
        [WorkItemState.InReview] = [WorkItemState.Verified, WorkItemState.InProgress, WorkItemState.Failed, WorkItemState.Blocked, WorkItemState.Ready],
        [WorkItemState.Verified] = [WorkItemState.Done, WorkItemState.Failed],
        [WorkItemState.Blocked] = [WorkItemState.Ready, WorkItemState.Cancelled],
        // Failed is retryable: the orchestrator can put it back on the queue.
        [WorkItemState.Failed] = [WorkItemState.Ready, WorkItemState.Cancelled],
        [WorkItemState.Done] = [],
        [WorkItemState.Cancelled] = []
    };

    public static bool IsTerminal(WorkItemState s) => Allowed[s].Length == 0;

    public static bool CanTransition(WorkItemState from, WorkItemState to) =>
        from == to || Allowed[from].Contains(to);

    public static IReadOnlyList<WorkItemState> NextFrom(WorkItemState s) => Allowed[s];
}
