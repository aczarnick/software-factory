using System.Text.Json.Serialization;

namespace Factory.Runtime;

/// <summary>What <c>bd reclaim --json</c> returns: a summary object, not a list of beads.
/// <see cref="Reclaimed"/> is absent rather than empty when nothing was stale.</summary>
public sealed record BeadsReclaimResponse
{
    [JsonPropertyName("count")] public int Count { get; init; }
    [JsonPropertyName("reclaimed")] public IReadOnlyList<ReclaimedLease>? Reclaimed { get; init; }

    /// <summary>Whether <c>-a/--assignee</c> restricted this reclaim to one checkout's own leases.
    /// True as soon as the flag is passed, independent of whether anything was actually stale.</summary>
    [JsonPropertyName("scoped")] public bool Scoped { get; init; }
}
