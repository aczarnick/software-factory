namespace Factory.Runtime;

/// <summary>Claim-lease timing. The values are measured against the backlog store rather than
/// configured: beads grants a five-minute lease from the last heartbeat and exposes no key to
/// change it, so the factory has to refresh well inside a window it cannot widen.</summary>
public static class Leases
{
    /// <summary>Shortest lease the factory has to survive, measured against beads 1.2.1.</summary>
    public static readonly TimeSpan ObservedShortest = TimeSpan.FromMinutes(5);

    /// <summary>Refresh cadence. A third of the lease leaves room to miss two refreshes — to a
    /// long station run, a paused process, or a slow shell — before a claim is actually lost.</summary>
    public static readonly TimeSpan RefreshInterval = ObservedShortest / 3;
}
