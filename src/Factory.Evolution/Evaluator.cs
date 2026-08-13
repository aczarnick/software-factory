using Factory.Core;

namespace Factory.Evolution;

/// <summary>Measured behaviour of one prompt version, mined from the run ledger.</summary>
public sealed record PromptStats
{
    public required string StationId { get; init; }
    public required string Version { get; init; }

    public int Runs { get; init; }
    public int GatePasses { get; init; }
    public int Errors { get; init; }
    public int Retries { get; init; }
    public decimal TotalCostUsd { get; init; }
    public long TotalTokens { get; init; }
    public long TotalTurns { get; init; }
    public long TotalDurationMs { get; init; }

    public double PassRate => Runs == 0 ? 0 : (double)GatePasses / Runs;
    public double ErrorRate => Runs == 0 ? 0 : (double)Errors / Runs;
    public double RetryRate => Runs == 0 ? 0 : (double)Retries / Runs;
    public decimal MeanCostUsd => Runs == 0 ? 0 : TotalCostUsd / Runs;
    public double MeanTokens => Runs == 0 ? 0 : (double)TotalTokens / Runs;
    public double MeanTurns => Runs == 0 ? 0 : (double)TotalTurns / Runs;

    public string Describe =>
        $"{Version}: {Runs} runs, {PassRate:P0} pass, ${MeanCostUsd:F4}/run, " +
        $"{MeanTokens:N0} tokens/run, {MeanTurns:F1} turns/run";
}

public sealed record FitnessWeights(
    double Pass = 1.0,
    double Cost = 0.25,
    double Turns = 0.10,
    double Retry = 0.15)
{
    public static readonly FitnessWeights Default = new();
}

/// <summary>
/// Turns the run ledger into per-prompt-version statistics and a single comparable score.
///
/// Quality alone is not the objective: a prompt that passes more often but costs three times
/// as much has not improved the factory. Cost, turns, and retries are therefore penalties in
/// the same score, which is what stops the evolution loop from optimising towards ever
/// longer, ever more expensive prompts.
/// </summary>
public static class Evaluator
{
    public static IReadOnlyList<PromptStats> ByVersion(IEnumerable<RunRecord> runs, string stationId)
    {
        return runs
            .Where(r => r.StationId == stationId && !r.CacheHit)
            .GroupBy(r => r.PromptVersion)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g => new PromptStats
            {
                StationId = stationId,
                Version = g.Key,
                Runs = g.Count(),
                GatePasses = g.Count(r => r.GatePassed),
                Errors = g.Count(r => !r.Success),
                Retries = g.Count(r => r.Attempt > 0),
                TotalCostUsd = g.Sum(r => r.CostUsd),
                TotalTokens = g.Sum(r => (long)r.Usage.Total),
                TotalTurns = g.Sum(r => (long)r.Turns),
                TotalDurationMs = g.Sum(r => r.DurationMs)
            })
            .OrderBy(s => s.Version)
            .ToList();
    }

    public static PromptStats? For(IEnumerable<RunRecord> runs, string stationId, string version) =>
        ByVersion(runs, stationId).FirstOrDefault(s => s.Version == version);

    /// <summary>
    /// Composite score. Cost and turns are normalised against a reference so the penalty is
    /// relative — a station that is inherently expensive is not punished for being itself,
    /// only for getting worse.
    /// </summary>
    public static double Fitness(PromptStats s, PromptStats? reference = null, FitnessWeights? weights = null)
    {
        var w = weights ?? FitnessWeights.Default;

        var costRef = (double)(reference?.MeanCostUsd ?? s.MeanCostUsd);
        var turnRef = reference?.MeanTurns ?? s.MeanTurns;

        var normCost = costRef <= 0 ? 0 : (double)s.MeanCostUsd / costRef;
        var normTurns = turnRef <= 0 ? 0 : s.MeanTurns / turnRef;

        return w.Pass * s.PassRate
             - w.Cost * normCost
             - w.Turns * normTurns
             - w.Retry * s.RetryRate;
    }
}
