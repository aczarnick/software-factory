using Factory.Core;

namespace Factory.Tests;

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

    [Fact]
    public void Blocked_status_and_reason_round_trip_through_FactoryJson_distinct_from_failed()
    {
        var blocked = WorkItem.Create("needs a newer toolchain") with
        {
            State = WorkItemState.Blocked,
            LastError = "requires dotnet 10.0, 9.0 installed; no remediation attempted"
        };

        var restored = FactoryJson.Read<WorkItem>(FactoryJson.Write(blocked));

        Assert.NotNull(restored);
        Assert.Equal(WorkItemState.Blocked, restored!.State);
        Assert.NotEqual(WorkItemState.Failed, restored.State);
        Assert.Equal(blocked.LastError, restored.LastError);
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

        guard.Restore(new BudgetRestoreView(
            new Dictionary<string, decimal> { [item.Id] = 1.2m },
            DailyUsd: 1.2m,
            EvolutionDailyUsd: 0m));

        Assert.Equal(1.2m, guard.SpentOn(item.Id));
        Assert.Throws<BudgetExhaustedException>(() => guard.EnsureCanSpend(item));
    }
}

public class BudgetRestoreTests
{
    [Fact]
    public void Restore_rehydrates_per_item_and_daily_spend_from_a_view()
    {
        var guard = new BudgetGuard(new BudgetSpec { DailyUsd = 10m, PerItemUsd = 5m });

        guard.Restore(new BudgetRestoreView(
            new Dictionary<string, decimal> { ["wi-a"] = 2.50m },
            DailyUsd: 4m,
            EvolutionDailyUsd: 1m));

        Assert.Equal(2.50m, guard.SpentOn("wi-a"));
        Assert.Equal(4m, guard.DailySpent);
        Assert.Equal(1m, guard.EvolutionSpent);
    }

    [Fact]
    public void Restore_replaces_prior_per_item_spend_rather_than_adding_to_it()
    {
        var guard = new BudgetGuard(new BudgetSpec { DailyUsd = 10m, PerItemUsd = 5m });

        guard.Restore(new BudgetRestoreView(
            new Dictionary<string, decimal> { ["wi-a"] = 2.50m, ["wi-stale"] = 1m },
            DailyUsd: 4m,
            EvolutionDailyUsd: 1m));

        guard.Restore(new BudgetRestoreView(
            new Dictionary<string, decimal> { ["wi-a"] = 0.75m },
            DailyUsd: 0.75m,
            EvolutionDailyUsd: 0m));

        Assert.Equal(0.75m, guard.SpentOn("wi-a"));
        Assert.Equal(0m, guard.SpentOn("wi-stale"));
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
        var low = WorkItem.Create("low") with { State = WorkItemState.Ready, Priority = Priorities.Lowest };
        var high = WorkItem.Create("high") with { State = WorkItemState.Ready, Priority = Priorities.Highest };

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

public class HeartbeatStatusTests
{
    [Fact]
    public void Round_trips_through_FactoryJson_without_loss()
    {
        var status = new HeartbeatStatus
        {
            Pid = 4242,
            StartedAtUtc = new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc),
            Status = "running",
            StoppedAtUtc = null,
            Items =
            [
                new HeartbeatItemStatus
                {
                    Id = "wi-1",
                    Title = "build a thing",
                    Station = "implement",
                    EnteredStationAtUtc = new DateTime(2026, 8, 13, 9, 5, 0, DateTimeKind.Utc),
                    ElapsedSeconds = 123.4,
                    CurrentCommand = "dotnet build",
                    Stalled = false
                }
            ],
            Spend = new SpendTotals(3, 1.23m, new TokenUsage(1000, 2000, 3000)),
            UsageWindows =
            [
                new RateLimitSnapshot
                {
                    Status = RateLimitStatus.Warning,
                    Window = "five_hour",
                    ResetsAt = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero),
                    UsingOverage = false,
                    OverageAvailable = true,
                    ObservedAt = new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.Zero)
                }
            ],
            RecentGates =
            [
                new HeartbeatGateResult
                {
                    ItemId = "wi-1",
                    GateName = "verify",
                    Passed = true,
                    TimestampUtc = new DateTime(2026, 8, 13, 9, 10, 0, DateTimeKind.Utc)
                }
            ]
        };

        var json = FactoryJson.Write(status);
        var restored = FactoryJson.Read<HeartbeatStatus>(json);

        // Records compare their List<T> members by reference, not by content, so a
        // structural comparison is needed to catch data loss across the round trip.
        Assert.Equivalent(status, restored, strict: true);
    }

    [Fact]
    public void Defaults_Spend_UsageWindows_and_RecentGates_to_empty()
    {
        var status = new HeartbeatStatus { Pid = 4242, StartedAtUtc = DateTime.UtcNow };

        Assert.Equal(SpendTotals.Empty, status.Spend);
        Assert.Empty(status.UsageWindows);
        Assert.Empty(status.RecentGates);
        Assert.True(status.RecentGates.Count <= HeartbeatStatus.MaxRecentGates);
    }

    [Fact]
    public void HeartbeatStatus_RoundTrips_HeartbeatWorkItemStatus()
    {
        var status = new HeartbeatStatus
        {
            Pid = 4242,
            StartedAtUtc = new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc),
            WorkItems =
            [
                new HeartbeatWorkItemStatus
                {
                    WorkItemId = "wi-1",
                    Station = "implement",
                    EnteredStationAtUtc = new DateTime(2026, 8, 13, 9, 5, 0, DateTimeKind.Utc),
                    ElapsedSeconds = 123.4,
                    CurrentCommand = "dotnet build"
                }
            ]
        };

        var json = FactoryJson.Write(status);
        var restored = FactoryJson.Read<HeartbeatStatus>(json);

        var entry = Assert.Single(restored!.WorkItems);
        Assert.Equal("wi-1", entry.WorkItemId);
        Assert.Equal("implement", entry.Station);
        Assert.Equal(new DateTime(2026, 8, 13, 9, 5, 0, DateTimeKind.Utc), entry.EnteredStationAtUtc);
        Assert.Equal(123.4, entry.ElapsedSeconds);
        Assert.Equal("dotnet build", entry.CurrentCommand);
    }

    [Fact]
    public void HeartbeatStatus_WithoutWorkItemsField_DeserializesToEmptyCollection()
    {
        var json = """{"pid":4242,"startedAtUtc":"2026-08-13T09:00:00Z","status":"running"}""";

        var restored = FactoryJson.Read<HeartbeatStatus>(json);

        Assert.Empty(restored!.WorkItems);
    }
}

public class IdFormatTests
{
    [Fact]
    public void New_emits_a_beads_compatible_identifier()
    {
        var id = Ids.New("wi");

        Assert.StartsWith("wi-", id);
        Assert.DoesNotContain("_", id);
    }

    [Fact]
    public void Work_items_default_to_the_middle_priority_band()
    {
        Assert.Equal(2, WorkItem.Create("thing").Priority);
    }
}

public class PriorityBandTests
{
    [Fact]
    public void Below_files_derived_work_one_step_less_urgent()
    {
        Assert.Equal(Priorities.Default + 1, Priorities.Below(Priorities.Default));
    }

    [Fact]
    public void Below_never_leaves_the_band_for_already_lowest_work()
    {
        Assert.Equal(Priorities.Lowest, Priorities.Below(Priorities.Lowest));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Below_stays_inside_the_band_the_backlog_store_accepts(int priority)
    {
        var derived = Priorities.Below(priority);

        Assert.InRange(derived, Priorities.Highest, Priorities.Lowest);
    }

    [Theory]
    [InlineData(5, 4)]
    [InlineData(100, 4)]
    [InlineData(150, 4)]
    [InlineData(200, 4)]
    [InlineData(-1, 0)]
    [InlineData(int.MinValue, 0)]
    public void Clamp_brings_a_value_outside_the_band_to_its_nearest_edge(int given, int expected)
    {
        Assert.Equal(expected, Priorities.Clamp(given));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void Clamp_leaves_a_value_already_in_the_band_alone(int priority)
    {
        Assert.Equal(priority, Priorities.Clamp(priority));
    }

    [Fact]
    public void No_caller_can_construct_a_work_item_outside_the_band()
    {
        // bd refuses -p 5 and -p -1 outright (exit 1, probed), so an out-of-band value on an item is
        // not a cosmetic inconsistency: it is a backlog write that fails, and BeadsWorkItemStore.Write
        // turns that into a halt. The band is enforced where the field lives so no construction path
        // can produce one -- object initialiser, `with`, or deserialisation.
        Assert.Equal(Priorities.Lowest, (WorkItem.Create("filed") with { Priority = 100 }).Priority);
        Assert.Equal(Priorities.Highest, (WorkItem.Create("filed") with { Priority = -7 }).Priority);
        Assert.Equal(Priorities.Lowest, new WorkItem { Id = "wi-1", Title = "built", Priority = 200 }.Priority);
    }

    [Fact]
    public void A_ledger_line_carrying_a_legacy_priority_is_normalised_as_the_fold_replays_it()
    {
        // The seam that matters for the cutover: this repository's own fold holds 87 items at
        // priorities 100, 150 and 200, and every one of them arrives through Replay rather than
        // through a constructor. The line is written as text for that reason -- an item built in C#
        // would already have been normalised and would prove nothing about the 87.
        const string legacyLine = """
            {"type":"work_item_filed","item":{"id":"wi_97db7ca6a29b","title":"legacy work",
             "intent":"filed before the band was narrowed","kind":"Feature","state":"Ready",
             "priority":100,"createdAt":"2026-08-13T06:23:37+00:00",
             "updatedAt":"2026-08-13T06:23:37+00:00"},
             "eventId":"evt_645fd1b2a793","at":"2026-08-13T06:23:37+00:00","seq":1}
            """;

        var replayed = FactoryState.Replay([FactoryJson.Read<FactoryEvent>(legacyLine)!]);

        Assert.Equal(Priorities.Lowest, replayed.Items["wi_97db7ca6a29b"].Priority);
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
