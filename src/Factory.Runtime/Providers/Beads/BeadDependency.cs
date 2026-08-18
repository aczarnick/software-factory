using System.Text.Json.Serialization;

namespace Factory.Runtime;

/// <summary>
/// An entry in a bead's <c>dependencies</c> array. Beads reports two different shapes for this:
/// <c>bd show</c> embeds the blocking issue itself (<c>id</c>, <c>dependency_type</c>), while
/// <c>bd list</c> reports the edge (<c>issue_id</c>, <c>depends_on_id</c>, <c>type</c>). Both are
/// accepted so a dependency is read the same way whichever command produced it.
/// </summary>
public sealed record BeadDependency
{
    /// <summary>The blocking issue, when beads embedded the issue rather than the edge.</summary>
    [JsonPropertyName("id")] public string? Id { get; init; }

    /// <summary>The dependent side of the edge, when beads reported the edge.</summary>
    [JsonPropertyName("issue_id")] public string? IssueId { get; init; }

    /// <summary>The blocking side of the edge, when beads reported the edge.</summary>
    [JsonPropertyName("depends_on_id")] public string? DependsOnId { get; init; }

    [JsonPropertyName("dependency_type")] public string? DependencyType { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }

    /// <summary>The bead that must close before the one carrying this entry can be worked.</summary>
    [JsonIgnore]
    public string BlockerId => DependsOnId ?? Id ?? "";
}
