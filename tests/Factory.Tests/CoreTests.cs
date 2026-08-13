using Factory.Core;

namespace Factory.Tests;

public class LedgerTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    [Fact]
    public void Appends_and_replays_events_in_order()
    {
        var path = Path.Combine(_dir, "ledger.jsonl");
        var item = WorkItem.Create("build a thing");

        using (var ledger = new Ledger(path))
        {
            ledger.Append(new WorkItemFiled(item));
            ledger.Append(new WorkItemStateChanged(item.Id, WorkItemState.Draft, WorkItemState.Ready));
        }

        using var reopened = new Ledger(path);
        var events = reopened.ReadAll();

        Assert.Equal(2, events.Count);
        Assert.Equal([1, 2], events.Select(e => e.Seq));
        Assert.IsType<WorkItemFiled>(events[0]);
        Assert.Equal(WorkItemState.Ready, reopened.Replay().Items[item.Id].State);
    }

    [Fact]
    public void Continues_sequence_numbers_across_reopen()
    {
        var path = Path.Combine(_dir, "ledger.jsonl");
        using (var first = new Ledger(path)) first.Append(new FactoryNote("one"));
        using (var second = new Ledger(path)) Assert.Equal(2, second.Append(new FactoryNote("two")).Seq);
    }

    [Fact]
    public void Torn_final_line_does_not_lose_earlier_history()
    {
        var path = Path.Combine(_dir, "ledger.jsonl");
        using (var ledger = new Ledger(path)) ledger.Append(new FactoryNote("intact"));

        // Simulate a process killed mid-write.
        File.AppendAllText(path, "{\"type\":\"note\",\"message\":\"trunc");

        using var reopened = new Ledger(path);
        var events = reopened.ReadAll();

        Assert.Single(events);
        Assert.Equal("intact", Assert.IsType<FactoryNote>(events[0]).Message);
    }

    [Fact]
    public void Round_trips_polymorphic_verifications()
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

        using (var ledger = new Ledger(path)) ledger.Append(new WorkItemFiled(item));

        using var reopened = new Ledger(path);
        var restored = reopened.Replay().Items[item.Id];

        Assert.IsType<CommandVerification>(restored.AcceptanceCriteria[0].Verification);
        Assert.IsType<AgentJudgeVerification>(restored.AcceptanceCriteria[1].Verification);
        Assert.Equal("dotnet build", ((CommandVerification)restored.AcceptanceCriteria[0].Verification).Command);
    }
}

public class WorkItemStateTests
{
    [Theory]
    [InlineData(WorkItemState.Draft, WorkItemState.Ready, true)]
    [InlineData(WorkItemState.Ready, WorkItemState.InProgress, true)]
    [InlineData(WorkItemState.InProgress, WorkItemState.Ready, true)]   // crash requeue
    [InlineData(WorkItemState.Verified, WorkItemState.Done, true)]
    [InlineData(WorkItemState.Failed, WorkItemState.Ready, true)]       // retryable
    [InlineData(WorkItemState.Draft, WorkItemState.Done, false)]
    [InlineData(WorkItemState.Done, WorkItemState.Ready, false)]
    [InlineData(WorkItemState.Cancelled, WorkItemState.InProgress, false)]
    public void Transitions_are_enforced(WorkItemState from, WorkItemState to, bool allowed) =>
        Assert.Equal(allowed, WorkItemStates.CanTransition(from, to));

    [Fact]
    public void Done_and_cancelled_are_terminal()
    {
        Assert.True(WorkItemStates.IsTerminal(WorkItemState.Done));
        Assert.True(WorkItemStates.IsTerminal(WorkItemState.Cancelled));
        Assert.False(WorkItemStates.IsTerminal(WorkItemState.Blocked));
    }

    [Fact]
    public void Item_is_fully_deterministic_only_when_every_criterion_is_machine_checkable()
    {
        var machine = WorkItem.Create("a") with
        {
            AcceptanceCriteria = [AcceptanceCriterion.Command("x", "true"), AcceptanceCriterion.FileExists("y", "f")]
        };
        var mixed = machine with
        {
            AcceptanceCriteria = [.. machine.AcceptanceCriteria, AcceptanceCriterion.Judged("z", "vibes")]
        };

        Assert.True(machine.IsFullyDeterministic);
        Assert.False(mixed.IsFullyDeterministic);
        Assert.False(WorkItem.Create("no criteria").IsFullyDeterministic);
    }
}

public class BudgetTests
{
    private static WorkItem Item(ProvenanceKind kind = ProvenanceKind.Human) =>
        WorkItem.Create("work") with
        {
            Provenance = kind switch
            {
                ProvenanceKind.Evolution => Provenance.FromEvolution("evolve"),
                ProvenanceKind.Agent => Provenance.FromAgent("review"),
                _ => Provenance.Human
            }
        };

    [Fact]
    public void Blocks_spending_past_the_item_ceiling()
    {
        var guard = new BudgetGuard(new BudgetSpec { DailyUsd = 100, PerItemUsd = 1m });
        var item = Item();

        guard.Record(item, 0.9m);
        guard.EnsureCanSpend(item);          // still under

        guard.Record(item, 0.2m);
        var ex = Assert.Throws<BudgetExhaustedException>(() => guard.EnsureCanSpend(item));
        Assert.Contains(item.Id, ex.Scope);
    }

    [Fact]
    public void Blocks_spending_past_the_daily_ceiling()
    {
        var guard = new BudgetGuard(new BudgetSpec { DailyUsd = 1m, PerItemUsd = 100m });
        guard.Record(Item(), 1.5m);
        Assert.Throws<BudgetExhaustedException>(() => guard.EnsureCanSpend(Item()));
    }

    [Fact]
    public void Self_improvement_cannot_starve_user_work()
    {
        var guard = new BudgetGuard(new BudgetSpec { DailyUsd = 10m, PerItemUsd = 100m, EvolutionShare = 0.15m });
        var evolution = Item(ProvenanceKind.Evolution);

        guard.Record(evolution, 1.6m);   // above 15% of $10

        Assert.Throws<BudgetExhaustedException>(() => guard.EnsureCanSpend(evolution));
        guard.EnsureCanSpend(Item());    // human work is unaffected
    }

    [Fact]
    public void Run_ceiling_is_the_smallest_of_station_item_and_daily_remaining()
    {
        var guard = new BudgetGuard(new BudgetSpec { DailyUsd = 10m, PerItemUsd = 2m, PerRunUsd = 5m });
        var item = Item();

        Assert.Equal(1.5m, guard.RemainingForRun(item, 1.5m));   // station cap is smallest
        guard.Record(item, 1.8m);
        Assert.Equal(0.2m, guard.RemainingForRun(item, 1.5m));   // item remainder is smallest
    }

    [Fact]
    public void Restores_spend_from_history_so_restarts_do_not_reset_the_budget()
    {
        var item = Item();
        var guard = new BudgetGuard(new BudgetSpec { DailyUsd = 10m, PerItemUsd = 1m });

        guard.Restore(
            [new RunRecord { RunId = "r", ItemId = item.Id, StationId = "implement", CostUsd = 1.2m }],
            new Dictionary<string, WorkItem> { [item.Id] = item });

        Assert.Equal(1.2m, guard.SpentOn(item.Id));
        Assert.Throws<BudgetExhaustedException>(() => guard.EnsureCanSpend(item));
    }
}

public class FactoryStateTests
{
    [Fact]
    public void Dispatchable_withholds_items_whose_dependencies_are_unmet()
    {
        var first = WorkItem.Create("first") with { State = WorkItemState.Ready };
        var second = WorkItem.Create("second") with { State = WorkItemState.Ready, DependsOn = [first.Id] };

        var state = FactoryState.Replay([new WorkItemFiled(first), new WorkItemFiled(second)]);
        Assert.Equal([first.Id], state.Dispatchable().Select(i => i.Id));

        state.Apply(new WorkItemStateChanged(first.Id, WorkItemState.Ready, WorkItemState.Done));
        Assert.Equal([second.Id], state.Dispatchable().Select(i => i.Id));
    }

    [Fact]
    public void Dispatchable_orders_by_priority_then_age()
    {
        var low = WorkItem.Create("low") with { State = WorkItemState.Ready, Priority = 500 };
        var high = WorkItem.Create("high") with { State = WorkItemState.Ready, Priority = 10 };

        var state = FactoryState.Replay([new WorkItemFiled(low), new WorkItemFiled(high)]);
        Assert.Equal([high.Id, low.Id], state.Dispatchable().Select(i => i.Id));
    }

    [Fact]
    public void Run_costs_accumulate_onto_their_item()
    {
        var item = WorkItem.Create("thing");
        var state = FactoryState.Replay([
            new WorkItemFiled(item),
            new RunCompleted(new RunRecord { RunId = "a", ItemId = item.Id, StationId = "plan", CostUsd = 0.10m }),
            new RunCompleted(new RunRecord { RunId = "b", ItemId = item.Id, StationId = "implement", CostUsd = 0.25m })
        ]);

        Assert.Equal(0.35m, state.Items[item.Id].SpentUsd);
        Assert.Equal(0.35m, state.TotalSpentUsd);
    }

    [Fact]
    public void Descendants_walks_the_whole_tree()
    {
        var root = WorkItem.Create("root");
        var child = WorkItem.Create("child") with { ParentId = root.Id };
        var grandchild = WorkItem.Create("grandchild") with { ParentId = child.Id };

        var state = FactoryState.Replay(
            [new WorkItemFiled(root), new WorkItemFiled(child), new WorkItemFiled(grandchild)]);

        Assert.Equal(2, state.Descendants(root.Id).Count);
        Assert.Contains(state.Descendants(root.Id), i => i.Id == grandchild.Id);
    }
}

public class BlueprintTests
{
    [Fact]
    public void Standard_blueprint_is_valid()
    {
        Assert.Empty(Blueprint.Standard().Validate());
    }

    [Fact]
    public void Verification_station_costs_no_tokens()
    {
        var verify = Blueprint.Standard().Require("verify");
        Assert.Equal(TokenProfile.None, verify.Profile);
        Assert.Equal(ModelTier.None, verify.Tier);
    }

    [Fact]
    public void Rejects_a_thin_station_that_declares_tools()
    {
        var bp = Blueprint.Standard();
        var broken = bp with
        {
            Stations = [.. bp.Stations.Where(s => s.Id != "plan"),
                bp.Require("plan") with { Tools = ["Bash"] }]
        };

        Assert.Contains(broken.Validate(), e => e.Contains("thin but declares tools"));
    }

    [Fact]
    public void Rejects_a_pipeline_referencing_an_unknown_station()
    {
        var broken = Blueprint.Standard() with { Pipeline = ["decompose", "nonexistent"] };
        Assert.Contains(broken.Validate(), e => e.Contains("unknown station 'nonexistent'"));
    }

    [Fact]
    public void Rejects_a_delegate_to_an_unlinked_factory()
    {
        var bp = Blueprint.Standard();
        var broken = bp with
        {
            Stations = [.. bp.Stations, new StationDef
            {
                Id = "child", Role = StationRole.Delegate, DelegateTo = "missing",
                Tier = ModelTier.None, Profile = TokenProfile.None
            }]
        };

        Assert.Contains(broken.Validate(), e => e.Contains("unlinked factory 'missing'"));
    }

    [Fact]
    public void NextAfter_walks_the_pipeline_and_stops_at_the_end()
    {
        var bp = Blueprint.Standard();
        Assert.Equal("decompose", bp.NextAfter(null));
        Assert.Equal("plan", bp.NextAfter("decompose"));
        Assert.Null(bp.NextAfter("integrate"));
    }

    [Fact]
    public void Composite_routes_through_its_children_and_is_itself_valid()
    {
        var composite = Blueprint.Composite("platform", new Dictionary<string, string>
        {
            ["api"] = "/tmp/api",
            ["web"] = "/tmp/web"
        });

        Assert.Empty(composite.Validate());
        Assert.Equal(["decompose", "api", "web"], composite.Pipeline);
        Assert.Equal(StationRole.Delegate, composite.Require("api").Role);
        // A composite spends nothing itself: its children do the work.
        Assert.Equal(TokenProfile.None, composite.Require("web").Profile);
    }
}

internal static class TempDir
{
    public static string Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "factory-tests", Guid.NewGuid().ToString("n")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Delete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
    }
}
