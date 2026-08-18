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

    /// <summary>When the factory filed the item. Kept here rather than read back from the bead's
    /// own <c>created_at</c>, which beads stamps at write time: dispatch breaks priority ties on
    /// this value, so it has to be the filing time and it has to round-trip exactly.</summary>
    public DateTimeOffset? CreatedAt { get; init; }
}
