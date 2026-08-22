using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

/// <summary>
/// A daily spend ceiling is a window, not a verdict: it clears on its own when the day rolls.
/// Work that hits it must be parked back on the queue and the dispatch loop must hold, because
/// blocking the item instead makes a human the only thing that can restart the factory.
/// </summary>
public sealed class BudgetWindowTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private static readonly DateTimeOffset Midday = new(2026, 8, 21, 13, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextMidnight = new(2026, 8, 22, 0, 0, 0, TimeSpan.Zero);

    private static WorkItem Item(ProvenanceKind kind = ProvenanceKind.Human) =>
        WorkItem.Create("work") with
        {
            Provenance = kind == ProvenanceKind.Evolution ? Provenance.FromEvolution("evolve") : Provenance.Human
        };

    [Fact]
    public void DailyExhaustionReportsWhenItsWindowRolls()
    {
        var guard = new BudgetGuard(new BudgetSpec { DailyUsd = 1m, PerItemUsd = 100m }, new FakeClock(Midday));
        guard.Record(Item(), 1.5m);

        var ex = Assert.Throws<BudgetExhaustedException>(() => guard.EnsureCanSpend(Item()));

        Assert.Equal(NextMidnight, ex.ResetsAt);
    }

    [Fact]
    public void EvolutionShareExhaustionRollsWithTheDayToo()
    {
        var guard = new BudgetGuard(
            new BudgetSpec { DailyUsd = 10m, PerItemUsd = 100m, EvolutionShare = 0.15m }, new FakeClock(Midday));
        var evolution = Item(ProvenanceKind.Evolution);
        guard.Record(evolution, 1.6m);

        var ex = Assert.Throws<BudgetExhaustedException>(() => guard.EnsureCanSpend(evolution));

        Assert.Equal(NextMidnight, ex.ResetsAt);
    }

    [Fact]
    public void ItemExhaustionReportsNoResetBecauseThatCeilingNeverRolls()
    {
        var guard = new BudgetGuard(new BudgetSpec { DailyUsd = 100m, PerItemUsd = 1m }, new FakeClock(Midday));
        var item = Item();
        guard.Record(item, 1.5m);

        var ex = Assert.Throws<BudgetExhaustedException>(() => guard.EnsureCanSpend(item));

        Assert.Null(ex.ResetsAt);
    }

    [Fact]
    public void DispatchHoldsUntilMidnightWhileTheDailyWindowIsSpent()
    {
        var guard = new BudgetGuard(new BudgetSpec { DailyUsd = 1m }, new FakeClock(Midday));
        guard.Record(Item(), 1.5m);

        Assert.True(guard.ShouldHold(out var wait, out var reason));
        Assert.Equal(NextMidnight - Midday, wait);
        Assert.Contains("daily budget", reason);
    }

    [Fact]
    public void NothingHoldsWhileTheDailyWindowStillHasRoom()
    {
        var guard = new BudgetGuard(new BudgetSpec { DailyUsd = 1m }, new FakeClock(Midday));
        guard.Record(Item(), 0.4m);

        Assert.False(guard.ShouldHold(out _, out _));
    }

    [Fact]
    public void TheHoldClearsWhenTheDayRolls()
    {
        var clock = new FakeClock(Midday);
        var guard = new BudgetGuard(new BudgetSpec { DailyUsd = 1m }, clock);
        guard.Record(Item(), 1.5m);
        Assert.True(guard.ShouldHold(out _, out _));

        clock.Advance(NextMidnight - Midday);

        Assert.False(guard.ShouldHold(out _, out _));
    }

    [Fact]
    public async Task AnItemThatSpendsTheDailyWindowMidPipelineIsParkedReadyNotBlocked()
    {
        // Two stations at $0.02 spend past a $0.03 day, so the ceiling is hit at implement —
        // with the item mid-pipeline and its worktree already claimed.
        using var host = OpenWithDailyCeiling(0.03m);

        host.Submit(WorkItem.Create("create hello.txt"));

        var report = await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true });

        var item = Assert.Single(host.Services.State.Items.Values);
        Assert.Equal(WorkItemState.Ready, item.State);
        Assert.Equal("implement", item.Station);
        Assert.Equal(0, report.Blocked);
        Assert.Equal(1, report.Parked);
    }

    [Fact]
    public async Task AParkedItemKeepsNoClaimSoTheNextPassCanPickItUp()
    {
        using var host = OpenWithDailyCeiling(0.03m);
        host.Submit(WorkItem.Create("create hello.txt"));

        await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true });

        var parked = Assert.Single(host.Services.State.Items.Values);
        Assert.True(string.IsNullOrEmpty(parked.Owner), $"a parked item must hold no claim, but {parked.Owner} does");
        Assert.NotNull(host.Services.Items.TryClaim("someone-else"));
    }

    [Fact]
    public async Task ALongRunningFactoryHoldsInsteadOfBurningTheBacklogIntoBlocked()
    {
        // Nothing is affordable at all, and the run is the daemon shape: no StopWhenIdle, so
        // before this change the loop claimed and blocked every ready item in turn.
        var transport = ScriptedPipeline();
        using var host = OpenWithDailyCeiling(0m, transport);
        host.Submit(WorkItem.Create("first thing"));
        host.Submit(WorkItem.Create("second thing"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await host.CreateOrchestrator().RunAsync(
            new OrchestratorOptions
            {
                StopWhenIdle = false,
                PollInterval = TimeSpan.FromMilliseconds(20),
                LeaseRefreshInterval = TimeSpan.FromMilliseconds(50)
            },
            cts.Token);

        Assert.All(host.Services.State.Items.Values, i => Assert.Equal(WorkItemState.Ready, i.State));
        Assert.Equal(0, transport.Calls);
    }

    private FactoryHost OpenWithDailyCeiling(decimal dailyUsd, FakeTransport? transport = null)
    {
        var blueprint = Blueprint.Standard() with
        {
            Budget = new BudgetSpec { DailyUsd = dailyUsd, PerItemUsd = 100m, PerRunUsd = 100m }
        };

        return FactoryHost.Init(_dir, blueprint, transport: transport ?? ScriptedPipeline());
    }

    private static FakeTransport ScriptedPipeline() =>
        new FakeTransport()
            .Respond("decompose",
                """{"children":[{"key":"a","title":"do it","kind":"Feature","requirements":["works"],"acceptanceCriteria":[]}]}""",
                cost: 0.02m)
            .Respond("plan",
                """{"files":[{"path":"hello.txt","change":"create"}],"steps":["write the file"],"risks":[]}""",
                cost: 0.02m)
            .Respond("implement", request =>
            {
                File.WriteAllText(Path.Combine(request.WorkingDirectory!, "hello.txt"), "hi\n");
                return FakeTransport.Success("wrote the file", cost: 0.02m);
            });
}
