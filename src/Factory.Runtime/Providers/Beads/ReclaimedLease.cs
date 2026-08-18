using System.Text.Json.Serialization;

namespace Factory.Runtime;

/// <summary>One lease <c>bd reclaim</c> reverted. Beads reports only the id and the holder it was
/// taken from, so the item itself has to be read back.</summary>
public sealed record ReclaimedLease
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("previous_owner")] public string? PreviousOwner { get; init; }
}
