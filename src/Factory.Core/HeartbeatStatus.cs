namespace Factory.Core;

/// <summary>
/// Point-in-time snapshot of a running factory, written for external readers (a CLI status
/// command, a dashboard) so they do not need to replay the ledger themselves.
/// </summary>
public sealed record HeartbeatStatus
{
    public int Pid { get; init; }
    public DateTime StartedAtUtc { get; init; }

    /// <summary><c>running</c> or <c>stopped</c>.</summary>
    public string Status { get; init; } = "running";

    public DateTime? StoppedAtUtc { get; init; }

    public List<HeartbeatItemStatus> Items { get; init; } = [];
    public SpendTotals Spend { get; init; } = SpendTotals.Empty;
    public List<RateLimitSnapshot> UsageWindows { get; init; } = [];
    public List<HeartbeatGateResult> RecentGates { get; init; } = [];
    public List<HeartbeatWorkItemStatus> WorkItems { get; init; } = [];

    /// <summary>Cap on <see cref="RecentGates"/> so the heartbeat file does not grow without
    /// bound over a long-running factory.</summary>
    public const int MaxRecentGates = 20;
}

/// <summary>Where one active work item sits in the factory right now, keyed by work item id.</summary>
public sealed record HeartbeatWorkItemStatus
{
    public required string WorkItemId { get; init; }
    public string Station { get; init; } = "";
    public DateTime EnteredStationAtUtc { get; init; }
    public double ElapsedSeconds { get; init; }
    public string? CurrentCommand { get; init; }
}

/// <summary>Where one work item sits in the factory right now.</summary>
public sealed record HeartbeatItemStatus
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Station { get; init; } = "";
    public DateTime EnteredStationAtUtc { get; init; }
    public double ElapsedSeconds { get; init; }
    public string? CurrentCommand { get; init; }

    /// <summary>True when the item has sat in the same station well past its usual time.</summary>
    public bool Stalled { get; init; }
}

/// <summary>One recent gate verdict, for a rolling view of what is passing and failing.</summary>
public sealed record HeartbeatGateResult
{
    public required string ItemId { get; init; }
    public required string GateName { get; init; }
    public bool Passed { get; init; }
    public DateTime TimestampUtc { get; init; }
}
