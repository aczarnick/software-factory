using System.Text.Json;
using Factory.Core;

namespace Factory.Agents;

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

    internal static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{t.TotalHours:F1}h" : $"{t.TotalMinutes:F0}m";
}

public sealed record UsagePolicy
{
    /// <summary>Concurrency once the provider says we are close to the ceiling.</summary>
    public int WarningConcurrency { get; init; } = 1;

    /// <summary>Pause added between runs while in warning, to stretch the remaining window.</summary>
    public TimeSpan WarningDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Cap on how long to sit waiting for a window to reset before giving up and
    /// letting the caller decide. A five-hour window should not silently block a CLI run.</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Slack added after a reported reset time before retrying.</summary>
    public TimeSpan ResetGrace { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Cooldown applied when a limit is only *inferred* from an error string, with no reported
    /// reset time. Deliberately short: an inferred limit is a guess, and the retry backoff
    /// already paces the attempt. Treating a guess like a real window stalls the factory for
    /// minutes on a single transient error.
    /// </summary>
    public TimeSpan InferredCooldown { get; init; } = TimeSpan.FromSeconds(30);

    public static readonly UsagePolicy Default = new();
}

/// <summary>
/// Keeps the factory inside the model's usage ceilings.
///
/// The transport already reports them: every run emits a <c>rate_limit_event</c> carrying a
/// status, the window it applies to (five-hour, weekly), and when that window resets. That is
/// a real sensor, so the factory reads it rather than guessing from failures after the fact.
///
/// The response is graded rather than binary. On a warning it narrows to one item at a time
/// and spaces runs out, which stretches the remaining window instead of sprinting into the
/// wall. On a rejection it stops dispatching until the window resets. State is persisted, so
/// a factory restarted inside an exhausted window does not immediately spend its way back
/// into the same rejection.
/// </summary>
public sealed class UsageGovernor
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RateLimitSnapshot> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;
    private readonly string? _statePath;

    public UsagePolicy Policy { get; }

    /// <summary>Raised when the governor changes what it will allow, so callers can report it.</summary>
    public event Action<string>? Changed;

    public UsageGovernor(UsagePolicy? policy = null, string? statePath = null, TimeProvider? clock = null)
    {
        Policy = policy ?? UsagePolicy.Default;
        _statePath = statePath;
        _clock = clock ?? TimeProvider.System;
        Load();
    }

    public IReadOnlyCollection<RateLimitSnapshot> Windows
    {
        get { lock (_gate) return _windows.Values.ToList(); }
    }

    /// <summary>The binding constraint: the worst status across every window we know about.</summary>
    public RateLimitSnapshot? Binding
    {
        get
        {
            lock (_gate)
                return _windows.Values
                    .Where(w => !IsStale(w))
                    .OrderByDescending(w => (int)w.Status)
                    .ThenByDescending(w => w.ResetsAt ?? DateTimeOffset.MinValue)
                    .FirstOrDefault();
        }
    }

    /// <summary>Feeds a transport event to the governor. Non-rate-limit events are ignored,
    /// so this can be wired straight into the run event hook.</summary>
    public void Observe(AgentEvent evt)
    {
        if (!evt.IsRateLimit) return;
        if (!evt.Raw.TryGetProperty("rate_limit_info", out var info)) return;

        var snapshot = new RateLimitSnapshot
        {
            Status = ParseStatus(Str(info, "status")),
            Window = Str(info, "rateLimitType") ?? "unknown",
            ResetsAt = Unix(info, "resetsAt"),
            UsingOverage = Flag(info, "isUsingOverage"),
            OverageAvailable = Str(info, "overageStatus") is { } o &&
                               !o.Equals("rejected", StringComparison.OrdinalIgnoreCase),
            ObservedAt = _clock.GetUtcNow()
        };

        Record(snapshot);
    }

    /// <summary>Records a limit inferred from a failed run, for transports that reject
    /// without having sent an event first.</summary>
    public void ObserveRejection(string? error)
    {
        if (error is null) return;
        if (!error.Contains("rate_limit", StringComparison.OrdinalIgnoreCase) &&
            !error.Contains("usage limit", StringComparison.OrdinalIgnoreCase)) return;

        Record(new RateLimitSnapshot
        {
            Status = RateLimitStatus.Rejected,
            Window = "inferred",
            // Nothing told us when this clears. A short cooldown avoids hammering a ceiling
            // we cannot see, without mistaking a guess for a measured window — the transport
            // will report a real one on the next attempt if the limit is genuine.
            ResetsAt = _clock.GetUtcNow() + Policy.InferredCooldown,
            ObservedAt = _clock.GetUtcNow()
        });
    }

    private void Record(RateLimitSnapshot snapshot)
    {
        bool changed;
        lock (_gate)
        {
            changed = !_windows.TryGetValue(snapshot.Window, out var previous) ||
                      previous.Status != snapshot.Status;
            _windows[snapshot.Window] = snapshot;
        }

        Save();
        if (changed && snapshot.Status != RateLimitStatus.Allowed)
            Changed?.Invoke(snapshot.Describe(_clock.GetUtcNow()));
    }

    /// <summary>How many items may be in flight, given what the provider has told us.</summary>
    public int Concurrency(int configured)
    {
        var binding = Binding;
        return binding?.Status switch
        {
            RateLimitStatus.Rejected => 1,
            RateLimitStatus.Warning => Math.Min(configured, Policy.WarningConcurrency),
            _ => configured
        };
    }

    /// <summary>Whether dispatch should hold, and for how long.</summary>
    public bool ShouldHold(out TimeSpan wait, out string reason)
    {
        wait = TimeSpan.Zero;
        reason = "";

        var binding = Binding;
        if (binding is null) return false;
        var now = _clock.GetUtcNow();

        if (binding.Status == RateLimitStatus.Rejected)
        {
            var untilReset = binding.TimeToReset(now) ?? TimeSpan.FromMinutes(5);
            wait = untilReset + Policy.ResetGrace;
            reason = $"{binding.Window} usage limit reached; resets in {RateLimitSnapshot.Format(untilReset)}";
            return true;
        }

        if (binding.Status == RateLimitStatus.Warning)
        {
            wait = Policy.WarningDelay;
            reason = $"approaching the {binding.Window} usage limit; pacing runs";
            return true;
        }

        return false;
    }

    /// <summary>Waits out a throttle before a run. Returns false when the wait would exceed
    /// the policy ceiling, leaving the decision to stop with the caller rather than blocking
    /// a command for hours.</summary>
    public async Task<bool> AwaitClearanceAsync(CancellationToken ct = default)
    {
        if (!ShouldHold(out var wait, out var reason)) return true;

        if (wait > Policy.MaxWait)
        {
            Changed?.Invoke($"{reason} — longer than the {RateLimitSnapshot.Format(Policy.MaxWait)} wait ceiling, stopping");
            return false;
        }

        Changed?.Invoke($"{reason} — waiting {RateLimitSnapshot.Format(wait)}");
        await Task.Delay(wait, ct).ConfigureAwait(false);

        // A rejection is cleared by the passage of time, not by another event arriving.
        lock (_gate)
        {
            foreach (var (window, snapshot) in _windows.ToList())
            {
                if (snapshot.Status == RateLimitStatus.Rejected &&
                    snapshot.ResetsAt is { } reset && _clock.GetUtcNow() >= reset)
                {
                    _windows[window] = snapshot with { Status = RateLimitStatus.Unknown };
                }
            }
        }

        return true;
    }

    /// <summary>A window is only informative for as long as it plausibly still holds.</summary>
    private bool IsStale(RateLimitSnapshot snapshot)
    {
        var now = _clock.GetUtcNow();
        if (snapshot.ResetsAt is { } reset && now >= reset) return true;
        return now - snapshot.ObservedAt > TimeSpan.FromHours(12);
    }

    // ── persistence ─────────────────────────────────────────────────────────

    private void Save()
    {
        if (_statePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            List<RateLimitSnapshot> snapshot;
            lock (_gate) snapshot = _windows.Values.ToList();
            File.WriteAllText(_statePath, FactoryJson.Write(snapshot, pretty: true));
        }
        catch (IOException) { /* the governor still works in memory */ }
    }

    private void Load()
    {
        if (_statePath is null || !File.Exists(_statePath)) return;
        try
        {
            var saved = FactoryJson.Read<List<RateLimitSnapshot>>(File.ReadAllText(_statePath));
            if (saved is null) return;
            lock (_gate)
                foreach (var snapshot in saved.Where(s => !IsStale(s)))
                    _windows[snapshot.Window] = snapshot;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { /* start clean */ }
    }

    // ── parsing helpers ─────────────────────────────────────────────────────

    private static RateLimitStatus ParseStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "allowed" or "ok" => RateLimitStatus.Allowed,
        "warning" or "approaching_limit" or "near_limit" => RateLimitStatus.Warning,
        "rejected" or "exceeded" or "blocked" => RateLimitStatus.Rejected,
        _ => RateLimitStatus.Unknown
    };

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Unix(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(v.GetInt64())
            : null;
}
