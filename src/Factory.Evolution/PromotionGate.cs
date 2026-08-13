namespace Factory.Evolution;

public sealed record GateSettings
{
    /// <summary>Minimum challenger runs before any promotion is considered.</summary>
    public int MinSamples { get; init; } = 20;

    /// <summary>Fitness improvement the challenger must clear.</summary>
    public double MinFitnessDelta { get; init; } = 0.02;

    /// <summary>z for the confidence interval. 1.96 ≈ 95%.</summary>
    public double Z { get; init; } = 1.96;

    /// <summary>Champion runs needed before proposing a challenger at all.</summary>
    public int MinChampionSamples { get; init; } = 10;

    public static readonly GateSettings Default = new();
}

public enum PromotionAction
{
    Hold,
    Promote,
    Demote
}

public sealed record PromotionDecision(
    PromotionAction Action,
    string Rationale,
    double FitnessDelta,
    double ChallengerLowerBound);

/// <summary>
/// Decides whether a challenger prompt replaces the champion.
///
/// The hard part of a self-improving system is not generating variants — it is refusing to
/// adopt one that only looked better. A challenger that wins 4 of 5 runs against a champion
/// on 0.8 pass rate is indistinguishable from noise, and a system that promotes it will drift
/// randomly while reporting continuous improvement.
///
/// Promotion therefore requires two independent things to agree: a better composite fitness,
/// and a Wilson score lower bound on the challenger's pass rate that still exceeds the
/// champion's observed rate. The lower bound shrinks towards zero when samples are few, so an
/// undersampled challenger cannot clear the bar however lucky its streak.
/// </summary>
public static class PromotionGate
{
    /// <summary>Wilson score interval lower bound — the pessimistic estimate of the true
    /// pass rate given the evidence so far.</summary>
    public static double WilsonLowerBound(int successes, int trials, double z = 1.96)
    {
        if (trials <= 0) return 0;

        var p = (double)successes / trials;
        var z2 = z * z;
        var denominator = 1 + z2 / trials;
        var centre = p + z2 / (2 * trials);
        var margin = z * Math.Sqrt((p * (1 - p) + z2 / (4 * trials)) / trials);

        return Math.Max(0, (centre - margin) / denominator);
    }

    public static double WilsonUpperBound(int successes, int trials, double z = 1.96)
    {
        if (trials <= 0) return 1;

        var p = (double)successes / trials;
        var z2 = z * z;
        var denominator = 1 + z2 / trials;
        var centre = p + z2 / (2 * trials);
        var margin = z * Math.Sqrt((p * (1 - p) + z2 / (4 * trials)) / trials);

        return Math.Min(1, (centre + margin) / denominator);
    }

    public static PromotionDecision Decide(
        PromptStats champion,
        PromptStats? challenger,
        GateSettings? settings = null,
        FitnessWeights? weights = null)
    {
        var cfg = settings ?? GateSettings.Default;

        if (challenger is null)
            return new PromotionDecision(PromotionAction.Hold, "no challenger under trial", 0, 0);

        var championFitness = Evaluator.Fitness(champion, champion, weights);
        var challengerFitness = Evaluator.Fitness(challenger, champion, weights);
        var delta = challengerFitness - championFitness;
        var lowerBound = WilsonLowerBound(challenger.GatePasses, challenger.Runs, cfg.Z);

        if (challenger.Runs < cfg.MinSamples)
        {
            // Cut a challenger loose early only when it is clearly, not marginally, worse.
            var upper = WilsonUpperBound(challenger.GatePasses, challenger.Runs, cfg.Z);
            if (challenger.Runs >= Math.Max(5, cfg.MinSamples / 4) && upper < champion.PassRate)
                return new PromotionDecision(PromotionAction.Demote,
                    $"challenger discarded early: even its optimistic pass rate ({upper:P0}) " +
                    $"is below the champion's {champion.PassRate:P0} after {challenger.Runs} runs",
                    delta, lowerBound);

            return new PromotionDecision(PromotionAction.Hold,
                $"gathering evidence: {challenger.Runs}/{cfg.MinSamples} runs", delta, lowerBound);
        }

        if (delta < cfg.MinFitnessDelta)
            return new PromotionDecision(PromotionAction.Demote,
                $"challenger fitness {challengerFitness:F3} did not beat champion {championFitness:F3} " +
                $"by the required {cfg.MinFitnessDelta:F3} (delta {delta:+0.000;-0.000})",
                delta, lowerBound);

        if (lowerBound <= champion.PassRate)
            return new PromotionDecision(PromotionAction.Demote,
                $"challenger's pass rate is not statistically better: lower bound {lowerBound:P0} " +
                $"does not exceed the champion's {champion.PassRate:P0} over {challenger.Runs} runs",
                delta, lowerBound);

        return new PromotionDecision(PromotionAction.Promote,
            $"challenger wins: fitness {challengerFitness:F3} vs {championFitness:F3} " +
            $"(delta {delta:+0.000;-0.000}), pass-rate lower bound {lowerBound:P0} > champion {champion.PassRate:P0} " +
            $"over {challenger.Runs} runs, ${challenger.MeanCostUsd:F4}/run vs ${champion.MeanCostUsd:F4}",
            delta, lowerBound);
    }
}
