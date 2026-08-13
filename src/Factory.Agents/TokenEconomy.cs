using Factory.Core;

namespace Factory.Agents;

/// <summary>
/// Reference numbers for the token economy, measured against this transport rather than
/// assumed. Reported savings are computed against these so the figures the factory prints
/// about itself are traceable to an experiment.
/// </summary>
public static class TokenEconomy
{
    /// <summary>
    /// Billed input tokens for a single default agent call doing trivial work: all tools
    /// loaded, full preamble, project settings, skills, and MCP config present.
    /// Measured: 10 input + 16,293 cache read + 3,043 cache write.
    /// </summary>
    public const int NaiveBaselineInputTokens = 19_336;

    /// <summary>Billed input tokens for the same work under the thin profile: no tools,
    /// lean replacement system prompt, no ambient settings. Measured: 165.</summary>
    public const int ThinProfileInputTokens = 165;

    public static double ThinReductionRatio =>
        1.0 - (double)ThinProfileInputTokens / NaiveBaselineInputTokens;

    /// <summary>
    /// Fixed ambient-context overhead — tool definitions, the default preamble, project
    /// settings, skills, MCP config — that a thin station avoids on <i>every</i> call.
    /// This is the difference between the two measurements, and it is the honest unit of
    /// saving: it is per-call overhead removed, independent of how much real content the
    /// prompt carries, because a naive call would have paid this on top of that content too.
    /// </summary>
    public const int AmbientOverheadTokens = NaiveBaselineInputTokens - ThinProfileInputTokens;

    /// <summary>Overhead avoided by running thin stations stripped. Thick runs are excluded:
    /// they keep the default preamble deliberately, so they never avoided this cost.</summary>
    public static long OverheadAvoided(IEnumerable<RunRecord> runs) =>
        runs.LongCount(r => r.Profile == TokenProfile.Thin && !r.CacheHit) * AmbientOverheadTokens;

    /// <summary>Tokens not spent because the response cache answered instead of the model.
    /// A cache hit avoids the whole call, not merely its overhead.</summary>
    public static long CacheAvoided(IEnumerable<RunRecord> runs) =>
        runs.Where(r => r.CacheHit).Sum(r => (long)r.Usage.Total);
}
