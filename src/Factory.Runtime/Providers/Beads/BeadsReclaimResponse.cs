using System.Text.Json.Serialization;

namespace Factory.Runtime;

/// <summary>What <c>bd reclaim --json</c> returns: a summary object, not a list of beads.
/// <see cref="Reclaimed"/> is absent rather than empty when nothing was stale.</summary>
public sealed record BeadsReclaimResponse
{
    [JsonPropertyName("count")] public int Count { get; init; }
    [JsonPropertyName("reclaimed")] public IReadOnlyList<ReclaimedLease>? Reclaimed { get; init; }
}
