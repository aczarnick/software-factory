namespace Factory.Core;

public enum RateLimitStatus
{
    Unknown,
    Allowed,

    /// <summary>Approaching the ceiling. Work continues, but narrowed.</summary>
    Warning,

    /// <summary>The window is spent. Nothing will succeed until it resets.</summary>
    Rejected
}

public sealed record RateLimitSnapshot
{
    public RateLimitStatus Status { get; init; } = RateLimitStatus.Unknown;

    /// <summary>Which ceiling this refers to — <c>five_hour</c>, <c>weekly</c>, and so on.
    /// Kept as the transport's own string so a new window type is observed, not discarded.</summary>
    public string Window { get; init; } = "unknown";

    public DateTimeOffset? ResetsAt { get; init; }
    public bool UsingOverage { get; init; }
    public bool OverageAvailable { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;

    public TimeSpan? TimeToReset(DateTimeOffset now) =>
        ResetsAt is { } reset && reset > now ? reset - now : null;

    public string Describe(DateTimeOffset now)
    {
        var reset = TimeToReset(now) is { } t ? $", resets in {Format(t)}" : "";
        return $"{Window} limit {Status.ToString().ToLowerInvariant()}{reset}";
    }

    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{t.TotalHours:F1}h" : $"{t.TotalMinutes:F0}m";
}
