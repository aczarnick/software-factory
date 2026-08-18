using Factory.Core;

namespace Factory.Runtime;

/// <summary>The structured remainder of a work item, stored in the bead's metadata JSON.
/// Volatile per-run state is deliberately absent: it belongs to the local ledger.</summary>
public sealed record BeadMetadata
{
    public string? Intent { get; init; }
    public IReadOnlyList<string> Requirements { get; init; } = [];
    public IReadOnlyList<AcceptanceCriterion> Criteria { get; init; } = [];
    public IReadOnlyList<string> Assumptions { get; init; } = [];
    public IReadOnlyList<string> Labels { get; init; } = [];
    public string? ParentId { get; init; }
    public decimal? BudgetUsd { get; init; }
    public ProvenanceKind ProvenanceKind { get; init; } = ProvenanceKind.Human;
    public string? ProvenanceSource { get; init; }
}
