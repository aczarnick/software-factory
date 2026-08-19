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

    /// <summary>Whether this edge withholds the bead carrying it. Beads has ten edge types and
    /// treats exactly one of them, <c>blocks</c>, as blocking: probing bd 1.2.1 shows a dependent
    /// joined by <c>tracks</c>, <c>related</c>, <c>parent-child</c>, <c>discovered-from</c>,
    /// <c>until</c>, <c>caused-by</c>, <c>validates</c>, <c>relates-to</c> or <c>supersedes</c> is
    /// still listed by <c>bd ready</c> and still reports <c>dependency_count: 0</c>. Reading those
    /// as blocking would make an edge another tool filed as context a blocker the factory never
    /// dispatches past. An entry with no type recorded blocks, because that is bd's own default for
    /// <c>bd dep add</c>; <c>blocked-by</c> and <c>depends-on</c> are bd's documented aliases for
    /// <c>blocks</c>, accepted here because bd's help declares them synonyms even though every
    /// write path probed normalises them to <c>blocks</c> before storing.</summary>
    [JsonIgnore]
    public bool IsBlocking => BlockingTypes.Contains(DependencyType ?? Type ?? "blocks");

    private static readonly HashSet<string> BlockingTypes =
        new(["blocks", "blocked-by", "depends-on", ""], StringComparer.OrdinalIgnoreCase);
}
