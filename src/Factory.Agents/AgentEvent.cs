using System.Text.Json;

namespace Factory.Agents;

/// <summary>
/// One message from the transport's stream-json output. Parsed leniently on purpose: the
/// factory reads the handful of fields it needs and keeps the raw element, so transport
/// schema additions never break the harness.
/// </summary>
public sealed record AgentEvent(string Type, string? Subtype, JsonElement Raw)
{
    public bool IsInit => Type == "system" && Subtype == "init";
    public bool IsResult => Type == "result";
    public bool IsAssistant => Type == "assistant";
    public bool IsRateLimit => Type == "rate_limit_event";

    public string? SessionId => Str("session_id");

    /// <summary>Assistant text content, concatenated across blocks.</summary>
    public string? AssistantText
    {
        get
        {
            if (!IsAssistant) return null;
            if (!Raw.TryGetProperty("message", out var m)) return null;
            if (!m.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;

            var parts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                    block.TryGetProperty("text", out var txt) && txt.GetString() is { } s)
                    parts.Add(s);
            }
            return parts.Count == 0 ? null : string.Join("", parts);
        }
    }

    /// <summary>Names of tools invoked in this message, for observability.</summary>
    public IEnumerable<string> ToolUses
    {
        get
        {
            if (!IsAssistant || !Raw.TryGetProperty("message", out var m)) yield break;
            if (!m.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) yield break;

            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_use" &&
                    block.TryGetProperty("name", out var n) && n.GetString() is { } name)
                    yield return name;
            }
        }
    }

    public string? Str(string prop) =>
        Raw.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public decimal Dec(string prop) =>
        Raw.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

    public int Int(string prop) =>
        Raw.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    public bool Bool(string prop) =>
        Raw.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.True;

    public static AgentEvent? TryParse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement.Clone();
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type is null) return null;
            var subtype = root.TryGetProperty("subtype", out var s) ? s.GetString() : null;
            return new AgentEvent(type, subtype, root);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
