using System.Text.Json.Serialization;

namespace Factory.Core;

/// <summary>Token accounting for a single model call, taken from the transport's
/// terminal <c>result</c> message.</summary>
public sealed record TokenUsage(
    int InputTokens = 0,
    int OutputTokens = 0,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0)
{
    public static readonly TokenUsage Zero = new();

    /// <summary>Everything that counted as input, however it was billed.</summary>
    public int BilledInput => InputTokens + CacheReadTokens + CacheWriteTokens;

    public int Total => BilledInput + OutputTokens;

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) => new(
        a.InputTokens + b.InputTokens,
        a.OutputTokens + b.OutputTokens,
        a.CacheReadTokens + b.CacheReadTokens,
        a.CacheWriteTokens + b.CacheWriteTokens);
}

/// <summary>The evidence record for one station execution. This is the row the prompt
/// evaluator mines; everything the factory learns about itself comes from here.</summary>
public sealed record RunRecord
{
    public required string RunId { get; init; }
    public required string ItemId { get; init; }
    public required string StationId { get; init; }

    /// <summary>Exact prompt version that produced this run, e.g. <c>implement@v3</c>.</summary>
    public string PromptVersion { get; init; } = "";

    public string Model { get; init; } = "";
    public TokenProfile Profile { get; init; } = TokenProfile.Thin;

    public bool Success { get; init; }

    /// <summary>Whether the station's gate passed. Distinct from Success: a run can
    /// complete cleanly and still fail its gate.</summary>
    public bool GatePassed { get; init; }

    public decimal CostUsd { get; init; }
    public TokenUsage Usage { get; init; } = TokenUsage.Zero;
    public int Turns { get; init; }
    public long DurationMs { get; init; }
    public string? StopReason { get; init; }
    public string? Error { get; init; }

    /// <summary>True when the response cache served this without a model call.</summary>
    public bool CacheHit { get; init; }

    public int Attempt { get; init; }

    /// <summary>Harness version that produced this run. Without it, a shift in a prompt's
    /// measured pass rate cannot be told apart from a change to the factory itself.</summary>
    public string FactoryVersion { get; init; } = Core.FactoryVersion.Full;

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WorkItemFiled), "work_item_filed")]
[JsonDerivedType(typeof(WorkItemUpdated), "work_item_updated")]
[JsonDerivedType(typeof(WorkItemStateChanged), "work_item_state_changed")]
[JsonDerivedType(typeof(RunStarted), "run_started")]
[JsonDerivedType(typeof(RunCompleted), "run_completed")]
[JsonDerivedType(typeof(GateEvaluated), "gate_evaluated")]
[JsonDerivedType(typeof(CriteriaVerified), "criteria_verified")]
[JsonDerivedType(typeof(BudgetConsumed), "budget_consumed")]
[JsonDerivedType(typeof(PromptPromoted), "prompt_promoted")]
[JsonDerivedType(typeof(PromptDemoted), "prompt_demoted")]
[JsonDerivedType(typeof(FactoryLinked), "factory_linked")]
[JsonDerivedType(typeof(DelegationStarted), "delegation_started")]
[JsonDerivedType(typeof(DelegationCompleted), "delegation_completed")]
[JsonDerivedType(typeof(ArtifactProduced), "artifact_produced")]
[JsonDerivedType(typeof(FactoryNote), "note")]
public abstract record FactoryEvent
{
    public string EventId { get; init; } = Ids.New("evt");
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Monotonic sequence assigned by the ledger on append.</summary>
    public long Seq { get; set; }
}

public sealed record WorkItemFiled(WorkItem Item) : FactoryEvent;

public sealed record WorkItemUpdated(WorkItem Item) : FactoryEvent;

public sealed record WorkItemStateChanged(
    string ItemId, WorkItemState From, WorkItemState To, string? Reason = null) : FactoryEvent;

public sealed record RunStarted(
    string RunId, string ItemId, string StationId, string PromptVersion, string Model) : FactoryEvent;

public sealed record RunCompleted(RunRecord Record) : FactoryEvent;

public sealed record GateEvaluated(
    string ItemId, string StationId, bool Passed, string Detail) : FactoryEvent;

/// <summary>The per-criterion outcome of verifying an item. <see cref="GateEvaluated"/> records only
/// that a gate passed; without this, which criteria were settled and which were never attempted is
/// computed and thrown away, so an item that skipped verification is indistinguishable from one that
/// passed everything.</summary>
public sealed record CriteriaVerified(
    string ItemId, IReadOnlyList<CriterionResult> Results) : FactoryEvent;

public sealed record BudgetConsumed(string Scope, decimal Usd, decimal ScopeTotal) : FactoryEvent;

public sealed record PromptPromoted(
    string StationId, string FromVersion, string ToVersion, double FitnessDelta, string Rationale) : FactoryEvent;

public sealed record PromptDemoted(
    string StationId, string FromVersion, string ToVersion, string Reason) : FactoryEvent;

public sealed record FactoryLinked(string ChildName, string ChildPath, string Port) : FactoryEvent;

public sealed record DelegationStarted(string ItemId, string ChildFactory, int Depth) : FactoryEvent;

public sealed record DelegationCompleted(
    string ItemId, string ChildFactory, bool Success, decimal ChildCostUsd) : FactoryEvent;

public sealed record ArtifactProduced(string ItemId, string Path, string Hash) : FactoryEvent;

public sealed record FactoryNote(string Message, string? ItemId = null) : FactoryEvent;
