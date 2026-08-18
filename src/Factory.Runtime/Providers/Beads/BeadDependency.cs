using System.Text.Json.Serialization;

namespace Factory.Runtime;

/// <summary>An edge in a bead's <c>dependencies</c> array. Every entry beads reports there is a
/// blocker of the bead carrying it, whichever direction the edge was declared from.</summary>
public sealed record BeadDependency
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("dependency_type")] public string DependencyType { get; init; } = "";
}
