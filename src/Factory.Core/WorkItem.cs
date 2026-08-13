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

public enum WorkItemKind
{
    Feature,
    Bug,
    Chore,
    Refactor,
    Spike,
    Improvement
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

    /// <summary>Lower sorts first.</summary>
    public int Priority { get; init; } = 100;

    public IReadOnlyList<string> Labels { get; init; } = [];
    public string? ParentId { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Per-item spend ceiling. Falls back to the blueprint budget when null.</summary>
    public decimal? BudgetUsd { get; init; }

    public Provenance Provenance { get; init; } = Provenance.Human;

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
