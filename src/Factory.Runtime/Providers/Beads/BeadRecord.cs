using System.Text.Json;
using System.Text.Json.Serialization;

namespace Factory.Runtime;

/// <summary>The subset of <c>bd --json</c> output the factory reads.</summary>
public sealed record BeadRecord
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "open";
    [JsonPropertyName("priority")] public int Priority { get; init; } = Core.Priorities.Default;
    [JsonPropertyName("issue_type")] public string IssueType { get; init; } = "task";
    [JsonPropertyName("acceptance_criteria")] public string? AcceptanceCriteria { get; init; }
    [JsonPropertyName("assignee")] public string? Assignee { get; init; }

    /// <summary>Optimistic-concurrency token. Large and often negative — never treat it as a
    /// counter or compare it for ordering, only for equality.</summary>
    [JsonPropertyName("revision")] public long Revision { get; init; }

    [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; init; }
    [JsonPropertyName("started_at")] public DateTimeOffset? StartedAt { get; init; }
    [JsonPropertyName("lease_expires_at")] public DateTimeOffset? LeaseExpiresAt { get; init; }
    [JsonPropertyName("heartbeat_at")] public DateTimeOffset? HeartbeatAt { get; init; }

    /// <summary>Beads that must close before this one can be worked.</summary>
    [JsonPropertyName("dependencies")] public IReadOnlyList<BeadDependency> Dependencies { get; init; } = [];

    /// <summary>Raw JSON object holding everything beads has no native field for.</summary>
    [JsonPropertyName("metadata")] public JsonElement? Metadata { get; init; }
}
