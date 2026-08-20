using Factory.Core;
using Factory.Agents;
using Factory.Evolution;

namespace Factory.Tests;

public class PromotionGateTests
{
    private static PromptStats Stats(string version, int runs, int passes, decimal costPerRun = 0.01m, double turns = 2) =>
        new()
        {
            StationId = "implement",
            Version = version,
            Runs = runs,
            GatePasses = passes,
            TotalCostUsd = costPerRun * runs,
            TotalTurns = (long)(turns * runs),
            TotalTokens = 1000L * runs
        };

    [Fact]
    public void WilsonLowerBoundPunishesSmallSamples()
    {
        // Same observed rate, very different confidence.
        var few = PromotionGate.WilsonLowerBound(4, 4);
        var many = PromotionGate.WilsonLowerBound(100, 100);

        Assert.True(few < 0.55, $"4/4 should not read as high confidence, got {few:F3}");
        Assert.True(many > 0.95, $"100/100 should read as high confidence, got {many:F3}");
    }

    [Fact]
    public void WilsonBoundsAreOrderedAndWithinRange()
    {
        var lower = PromotionGate.WilsonLowerBound(7, 10);
        var upper = PromotionGate.WilsonUpperBound(7, 10);

        Assert.InRange(lower, 0, 0.7);
        Assert.InRange(upper, 0.7, 1);
        Assert.True(lower < upper);
    }

    [Fact]
    public void HoldsWhileTheChallengerIsStillUndersampled()
    {
        var decision = PromotionGate.Decide(Stats("v1", 50, 40), Stats("v2", 4, 4));
        Assert.Equal(PromotionAction.Hold, decision.Action);
        Assert.Contains("gathering evidence", decision.Rationale);
    }

    [Fact]
    public void RefusesToPromoteOnALuckyStreak()
    {
        // A perfect run of 20 against a champion already at 95% is not evidence of improvement.
        var decision = PromotionGate.Decide(Stats("v1", 200, 190), Stats("v2", 20, 20));
        Assert.NotEqual(PromotionAction.Promote, decision.Action);
    }

    [Fact]
    public void PromotesOnAClearAndWellSampledWin()
    {
        var champion = Stats("v1", 100, 50);      // 50% pass
        var challenger = Stats("v2", 60, 57);     // 95% pass, same cost

        var decision = PromotionGate.Decide(champion, challenger);

        Assert.Equal(PromotionAction.Promote, decision.Action);
        Assert.True(decision.FitnessDelta > 0);
        Assert.Contains("lower bound", decision.Rationale);
    }

    [Fact]
    public void DiscardsAChallengerThatIsClearlyWorseWithoutWaitingForFullSamples()
    {
        var decision = PromotionGate.Decide(Stats("v1", 200, 200), Stats("v2", 8, 1));
        Assert.Equal(PromotionAction.Demote, decision.Action);
        Assert.Contains("optimistic", decision.Rationale);
    }

    [Fact]
    public void AChallengerThatWinsOnQualityButLosesOnCostIsRefused()
    {
        var champion = Stats("v1", 100, 80, costPerRun: 0.01m);
        var expensive = Stats("v2", 40, 36, costPerRun: 0.20m);   // +10% pass, 20x the cost

        var decision = PromotionGate.Decide(champion, expensive);

        Assert.Equal(PromotionAction.Demote, decision.Action);
        Assert.True(decision.FitnessDelta < 0);
    }

    [Fact]
    public void NoChallengerMeansNothingToDecide()
    {
        var decision = PromotionGate.Decide(Stats("v1", 10, 10), challenger: null);
        Assert.Equal(PromotionAction.Hold, decision.Action);
    }
}

public class EvaluatorTests
{
    private static RunRecord Run(string version, bool gatePassed, decimal cost = 0.01m, bool cacheHit = false) => new()
    {
        RunId = Ids.New("run"),
        ItemId = "item",
        StationId = "plan",
        PromptVersion = version,
        GatePassed = gatePassed,
        Success = true,
        CostUsd = cost,
        CacheHit = cacheHit,
        Turns = 2,
        Usage = new TokenUsage(100, 50, 0, 0)
    };

    [Fact]
    public void GroupsRunsByPromptVersion()
    {
        var stats = Evaluator.ByVersion(
            [Run("plan@v1", true), Run("plan@v1", false), Run("plan@v2", true)], "plan");

        Assert.Equal(2, stats.Count);
        Assert.Equal(0.5, stats.First(s => s.Version == "plan@v1").PassRate);
        Assert.Equal(1.0, stats.First(s => s.Version == "plan@v2").PassRate);
    }

    [Fact]
    public void CacheHitsAreExcludedBecauseTheyDidNotTestThePrompt()
    {
        var stats = Evaluator.ByVersion([Run("plan@v1", true), Run("plan@v1", true, cacheHit: true)], "plan");
        Assert.Equal(1, stats.Single().Runs);
    }

    [Fact]
    public void FitnessPenalisesAMoreExpensivePromptAtEqualQuality()
    {
        var cheap = new PromptStats { StationId = "plan", Version = "v1", Runs = 10, GatePasses = 9, TotalCostUsd = 0.10m, TotalTurns = 20 };
        var dear = cheap with { Version = "v2", TotalCostUsd = 1.00m };

        Assert.True(Evaluator.Fitness(dear, cheap) < Evaluator.Fitness(cheap, cheap));
    }

    [Fact]
    public void FitnessRewardsAHigherPassRate()
    {
        var worse = new PromptStats { StationId = "plan", Version = "v1", Runs = 10, GatePasses = 5, TotalCostUsd = 0.10m, TotalTurns = 20 };
        var better = worse with { Version = "v2", GatePasses = 9 };

        Assert.True(Evaluator.Fitness(better, worse) > Evaluator.Fitness(worse, worse));
    }
}

public sealed class PromptRegistryTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    [Fact]
    public void SeedsV1AndMakesItChampion()
    {
        var registry = new PromptRegistry(_dir);
        var seeded = registry.EnsureSeed("plan", "original text");

        Assert.Equal(1, seeded.Version);
        Assert.Equal("plan@v1", registry.Champion("plan").Id);
    }

    [Fact]
    public void SeedingTwiceDoesNotCreateASecondVersion()
    {
        var registry = new PromptRegistry(_dir);
        registry.EnsureSeed("plan", "original");
        registry.EnsureSeed("plan", "different text entirely");

        Assert.Single(registry.Versions("plan"));
    }

    [Fact]
    public void IdenticalTextIsDeduplicatedRatherThanVersionedAgain()
    {
        var registry = new PromptRegistry(_dir);
        registry.EnsureSeed("plan", "original");

        var again = registry.Add("plan", "original");
        Assert.Equal(1, again.Version);
        Assert.Single(registry.Versions("plan"));
    }

    [Fact]
    public void PromotionMovesTheChampionAndClearsTheChallenger()
    {
        var registry = new PromptRegistry(_dir);
        registry.EnsureSeed("plan", "v1 text");
        var v2 = registry.Add("plan", "v2 text");
        registry.SetChallenger("plan", v2.Version, 0.3);

        Assert.Equal("plan@v2", registry.Challenger("plan")!.Id);

        registry.SetChampion("plan", v2.Version);

        Assert.Equal("plan@v2", registry.Champion("plan").Id);
        Assert.Null(registry.Challenger("plan"));
    }

    [Fact]
    public void TrafficSplitsBetweenChampionAndChallengerUnderTrial()
    {
        var registry = new PromptRegistry(_dir);
        registry.EnsureSeed("plan", "v1 text");
        registry.SetChallenger("plan", registry.Add("plan", "v2 text").Version, share: 0.5);

        var rng = new Random(1234);
        var picks = Enumerable.Range(0, 400).Select(_ => registry.Select("plan", rng).Id).ToList();

        Assert.Contains("plan@v1", picks);
        Assert.Contains("plan@v2", picks);
        // Roughly half, with generous slack for randomness.
        Assert.InRange(picks.Count(p => p == "plan@v2"), 140, 260);
    }

    [Fact]
    public void RollingBackToAnEarlierVersionIsPossible()
    {
        var registry = new PromptRegistry(_dir);
        registry.EnsureSeed("plan", "v1 text");
        registry.SetChampion("plan", registry.Add("plan", "v2 text").Version);
        registry.SetChampion("plan", 1);

        Assert.Equal("v1 text", registry.Champion("plan").Text);
    }
}

public sealed class EvolutionImprovementTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private static RunRecord Run(string version) => new()
    {
        RunId = Ids.New("run"),
        ItemId = "item",
        StationId = "plan",
        PromptVersion = version,
        GatePassed = true,
        Success = true,
        CostUsd = 0.01m,
        Turns = 2,
        Usage = new TokenUsage(100, 50, 0, 0)
    };

    [Fact]
    public async Task WorkTheFactoryFilesAgainstItselfSortsLast()
    {
        var prompts = new PromptRegistry(_dir);
        var champion = prompts.EnsureSeed("plan", "plan carefully");
        prompts.EnsureSeed("evolve", "evolve carefully");

        var transport = new FakeTransport().Respond("evolve",
            """{"proposeChange":false,"rationale":"fine","workItems":[{"title":"fix the flaky gate","intent":"it wastes runs"}]}""");

        var loop = new EvolutionLoop(prompts, new AgentRunner(transport, cache: null));

        var outcome = await loop.RunStationAsync(
            "plan",
            ModelTier.Haiku,
            [.. Enumerable.Range(0, 10).Select(_ => Run(champion.Id))],
            failureTraces: [],
            budgetUsd: 1m);

        var improvement = Assert.Single(outcome.ImprovementItems);
        Assert.Equal(Priorities.Lowest, improvement.Priority);
    }
}
