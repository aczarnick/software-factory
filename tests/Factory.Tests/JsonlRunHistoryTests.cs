using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public sealed class JsonlRunHistoryTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private JsonlRunHistory Open(TimeProvider? clock = null) =>
        new(Path.Combine(_dir, "ledger.jsonl"), clock);

    private static RunRecord Run(string itemId, string stationId, decimal cost) => new()
    {
        RunId = Ids.New("run"),
        ItemId = itemId,
        StationId = stationId,
        CostUsd = cost,
        Usage = new TokenUsage(InputTokens: 10, OutputTokens: 5)
    };

    [Fact]
    public void RunsForItemReturnsOnlyThatItemsRuns()
    {
        using var history = Open();
        history.Append(new RunCompleted(Run("wi-a", "implement", 0.10m)));
        history.Append(new RunCompleted(Run("wi-b", "implement", 0.20m)));

        var runs = history.RunsForItem("wi-a");

        Assert.Single(runs);
        Assert.Equal(0.10m, runs[0].CostUsd);
    }

    [Fact]
    public void RunsForStationReturnsOnlyThatStationsRuns()
    {
        using var history = Open();
        history.Append(new RunCompleted(Run("wi-a", "implement", 0.10m)));
        history.Append(new RunCompleted(Run("wi-a", "review", 0.20m)));

        var runs = history.RunsForStation("review");

        Assert.Single(runs);
        Assert.Equal(0.20m, runs[0].CostUsd);
    }

    [Fact]
    public void TotalsAggregatesCountCostAndUsage()
    {
        using var history = Open();
        history.Append(new RunCompleted(Run("wi-a", "implement", 0.10m)));
        history.Append(new RunCompleted(Run("wi-b", "review", 0.25m)));

        var totals = history.Totals();

        Assert.Equal(2, totals.RunCount);
        Assert.Equal(0.35m, totals.TotalUsd);
        Assert.Equal(20, totals.Usage.InputTokens);
    }

    [Fact]
    public void ChampionsReflectsTheLatestPromotionPerStation()
    {
        using var history = Open();
        history.Append(new PromptPromoted("implement", "v1", "v2", 0.1, "better"));
        history.Append(new PromptPromoted("implement", "v2", "v3", 0.2, "better still"));
        history.Append(new PromptPromoted("review", "v1", "v4", 0.1, "better"));

        var champions = history.Champions();

        Assert.Equal("v3", champions["implement"]);
        Assert.Equal("v4", champions["review"]);
    }

    [Fact]
    public void ForBudgetSumsPerItemAlwaysAndDailyOnlyForToday()
    {
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        using var history = Open(clock);

        history.Append(new RunCompleted(Run("wi-a", "implement", 0.10m) with { At = now }));
        history.Append(new RunCompleted(Run("wi-a", "review", 0.20m) with { At = now.AddDays(-3) }));

        var view = history.ForBudget();

        Assert.Equal(0.30m, view.PerItemUsd["wi-a"]);
        Assert.Equal(0.10m, view.DailyUsd);
        Assert.Equal(0m, view.EvolutionDailyUsd);
    }

    [Fact]
    public void ForBudgetAttributesEvolutionSpendFromItemProvenance()
    {
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        using var history = Open(clock);

        var evolutionItem = WorkItem.Create("self-improvement") with
        {
            Provenance = Provenance.FromEvolution("optimiser")
        };
        var humanItem = WorkItem.Create("human work");

        history.Append(new WorkItemFiled(evolutionItem));
        history.Append(new WorkItemFiled(humanItem));
        history.Append(new RunCompleted(Run(evolutionItem.Id, "implement", 0.40m) with { At = now }));
        history.Append(new RunCompleted(Run(humanItem.Id, "implement", 0.15m) with { At = now }));

        var view = history.ForBudget();

        Assert.Equal(0.55m, view.DailyUsd);
        Assert.Equal(0.40m, view.EvolutionDailyUsd);
    }

    [Fact]
    public void AppendsAndReplaysEventsInOrder()
    {
        var path = Path.Combine(_dir, "ledger.jsonl");
        var item = WorkItem.Create("build a thing");

        using (var history = new JsonlRunHistory(path))
        {
            history.Append(new WorkItemFiled(item));
            history.Append(new WorkItemStateChanged(item.Id, WorkItemState.Draft, WorkItemState.Ready));
        }

        using var reopened = new JsonlRunHistory(path);
        var events = reopened.ReadFrom(0).ToList();

        Assert.Equal(2, events.Count);
        Assert.Equal([1, 2], events.Select(e => e.Seq));
        Assert.IsType<WorkItemFiled>(events[0]);
        Assert.Equal(WorkItemState.Ready, reopened.Replay().Items[item.Id].State);
    }

    [Fact]
    public void ReadFromReturnsOnlyEventsStrictlyAfterTheGivenSequence()
    {
        using var history = Open();
        history.Append(new FactoryNote("one"));
        history.Append(new FactoryNote("two"));
        history.Append(new FactoryNote("three"));

        var events = history.ReadFrom(1).ToList();

        Assert.Equal([2, 3], events.Select(e => e.Seq));
    }

    [Fact]
    public void ContinuesSequenceNumbersAcrossReopen()
    {
        var path = Path.Combine(_dir, "ledger.jsonl");
        using (var first = new JsonlRunHistory(path)) first.Append(new FactoryNote("one"));
        using (var second = new JsonlRunHistory(path)) second.Append(new FactoryNote("two"));

        using var reopened = new JsonlRunHistory(path);
        Assert.Equal([1, 2], reopened.ReadFrom(0).Select(e => e.Seq));
    }

    [Fact]
    public void TornFinalLineDoesNotLoseEarlierHistory()
    {
        var path = Path.Combine(_dir, "ledger.jsonl");
        using (var history = new JsonlRunHistory(path)) history.Append(new FactoryNote("intact"));

        // Simulate a process killed mid-write.
        File.AppendAllText(path, "{\"type\":\"note\",\"message\":\"trunc");

        using var reopened = new JsonlRunHistory(path);
        var events = reopened.ReadFrom(0).ToList();

        Assert.Single(events);
        Assert.Equal("intact", Assert.IsType<FactoryNote>(events[0]).Message);
    }

    [Fact]
    public void RoundTripsPolymorphicVerifications()
    {
        var path = Path.Combine(_dir, "ledger.jsonl");
        var item = WorkItem.Create("thing") with
        {
            AcceptanceCriteria =
            [
                AcceptanceCriterion.Command("compiles", "dotnet build"),
                AcceptanceCriterion.Judged("reads well", "prose is clear")
            ]
        };

        using (var history = new JsonlRunHistory(path)) history.Append(new WorkItemFiled(item));

        using var reopened = new JsonlRunHistory(path);
        var restored = reopened.Replay().Items[item.Id];

        Assert.IsType<CommandVerification>(restored.AcceptanceCriteria[0].Verification);
        Assert.IsType<AgentJudgeVerification>(restored.AcceptanceCriteria[1].Verification);
        Assert.Equal("dotnet build", ((CommandVerification)restored.AcceptanceCriteria[0].Verification).Command);
    }
}

file sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
