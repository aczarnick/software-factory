using System.Text.Json;
using Factory.Core;

namespace Factory.Agents;

public sealed record AgentRunResult
{
    public bool Success { get; init; }

    /// <summary>The agent's final output text (or the raw JSON when a schema was supplied).</summary>
    public string Text { get; init; } = "";

    public string? SessionId { get; init; }
    public decimal CostUsd { get; init; }
    public TokenUsage Usage { get; init; } = TokenUsage.Zero;
    public int Turns { get; init; }
    public string? StopReason { get; init; }
    public string? Error { get; init; }

    /// <summary>The run hit its turn ceiling with work still in hand, rather than ending for a
    /// reason of its own. It is the one failure worth continuing instead of restarting: the
    /// conversation was progressing, and <see cref="SessionId"/> can carry it on for the price of a
    /// cache read rather than paying for the whole briefing again.</summary>
    public bool ExhaustedTurns { get; init; }

    /// <summary>Whether this run can be picked up where it stopped.</summary>
    public bool CanResume => ExhaustedTurns && SessionId is { Length: > 0 };
    public long DurationMs { get; init; }

    /// <summary>True when served from the response cache without a model call.</summary>
    public bool CacheHit { get; init; }

    public IReadOnlyList<string> ToolsUsed { get; init; } = [];

    /// <summary>Raw terminal result message, kept only for failures. A station that fails for
    /// a reason the harness does not model is otherwise undiagnosable after the fact.</summary>
    public string? RawResult { get; init; }

    public static AgentRunResult Failure(string error, long durationMs = 0) =>
        new() { Success = false, Error = error, DurationMs = durationMs };

    /// <summary>Deserialises structured output produced under a JSON schema. Tolerates a
    /// model that wrapped its JSON in prose or a fenced block.</summary>
    public T? Structured<T>()
    {
        var json = ExtractJson(Text);
        if (json is null) return default;
        try { return JsonSerializer.Deserialize<T>(json, FactoryJson.Compact); }
        catch (JsonException) { return default; }
    }

    public bool TryStructured<T>(out T value, out string? error)
    {
        value = default!;
        var json = ExtractJson(Text);
        if (json is null) { error = "no JSON object found in agent output"; return false; }
        try
        {
            var parsed = JsonSerializer.Deserialize<T>(json, FactoryJson.Compact);
            if (parsed is null) { error = "agent output deserialised to null"; return false; }
            value = parsed;
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static string? ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();

        if (t.StartsWith('{') || t.StartsWith('[')) return t;

        // Fenced block, with or without a language tag.
        var fence = t.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var afterFence = t.IndexOf('\n', fence);
            if (afterFence > 0)
            {
                var close = t.IndexOf("```", afterFence, StringComparison.Ordinal);
                if (close > afterFence)
                {
                    var inner = t[(afterFence + 1)..close].Trim();
                    if (inner.StartsWith('{') || inner.StartsWith('[')) return inner;
                }
            }
        }

        // Last resort: widest brace span.
        var open = t.IndexOf('{');
        var closeBrace = t.LastIndexOf('}');
        if (open >= 0 && closeBrace > open) return t[open..(closeBrace + 1)];

        return null;
    }
}
