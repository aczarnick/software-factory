using Factory.Core;

namespace Factory.Tests;

/// <summary>Facts the ledger derives — what an item cost, which of its criteria passed — belong to
/// the replay, not to the WorkItem record. WorkItemUpdated replaces that record wholesale from a
/// caller's snapshot, so anything accumulated onto it is destroyed by the next update.</summary>
public class LedgerProjectionTests
{
    private static AcceptanceCriterion Command(string id, string statement) =>
        AcceptanceCriterion.Command(statement, "true") with { Id = id };

    [Fact]
    public void Spend_survives_an_update_that_carries_a_stale_item()
    {
        var item = WorkItem.Create("thing");
        var state = FactoryState.Replay([
            new WorkItemFiled(item),
            new RunCompleted(new RunRecord { RunId = "a", ItemId = item.Id, StationId = "plan", CostUsd = 0.10m }),
            new RunCompleted(new RunRecord { RunId = "b", ItemId = item.Id, StationId = "implement", CostUsd = 0.25m })
        ]);

        // Exactly what the orchestrator does after a transition, carrying a snapshot taken before
        // the runs were recorded. This is what zeroed every cost in `factory ls`.
        state.Apply(new WorkItemUpdated(item with { Station = null }));

        Assert.Equal(0.35m, state.SpentFor(item.Id));
    }

    [Fact]
    public void An_item_with_no_runs_has_spent_nothing()
    {
        var item = WorkItem.Create("thing");
        var state = FactoryState.Replay([new WorkItemFiled(item)]);

        Assert.Equal(0m, state.SpentFor(item.Id));
    }

    [Fact]
    public void Spend_is_attributed_to_the_item_that_incurred_it()
    {
        var mine = WorkItem.Create("mine");
        var yours = WorkItem.Create("yours");
        var state = FactoryState.Replay([
            new WorkItemFiled(mine),
            new WorkItemFiled(yours),
            new RunCompleted(new RunRecord { RunId = "a", ItemId = mine.Id, StationId = "plan", CostUsd = 0.10m }),
            new RunCompleted(new RunRecord { RunId = "b", ItemId = yours.Id, StationId = "plan", CostUsd = 0.99m })
        ]);

        Assert.Equal(0.10m, state.SpentFor(mine.Id));
    }

    [Fact]
    public void An_item_that_was_never_verified_has_no_verdict()
    {
        var item = WorkItem.Create("thing") with { AcceptanceCriteria = [Command("c1", "it works")] };
        var state = FactoryState.Replay([new WorkItemFiled(item)]);

        // The distinction that matters: no verdict is not a passing verdict. A decomposed parent
        // that skipped verification must be visibly different from one that passed.
        Assert.Null(state.VerdictFor(item.Id));
    }

    [Fact]
    public void A_recorded_verdict_counts_the_criteria_that_actually_passed()
    {
        var item = WorkItem.Create("thing") with
        {
            AcceptanceCriteria = [Command("c1", "one"), Command("c2", "two")]
        };
        var state = FactoryState.Replay([
            new WorkItemFiled(item),
            new CriteriaVerified(item.Id, [
                CriterionResult.Pass("c1", "`true` passed"),
                CriterionResult.Fail("c2", "`false` exited 1")
            ])
        ]);

        var verdict = state.VerdictFor(item.Id);

        Assert.NotNull(verdict);
        Assert.Equal(2, verdict!.Results.Count);
        Assert.Single(verdict.Results.Where(r => r.Passed));
        Assert.False(verdict.AllPassed);
    }

    [Fact]
    public void A_verdict_survives_an_update_that_carries_a_stale_item()
    {
        var item = WorkItem.Create("thing") with { AcceptanceCriteria = [Command("c1", "one")] };
        var state = FactoryState.Replay([
            new WorkItemFiled(item),
            new CriteriaVerified(item.Id, [CriterionResult.Pass("c1", "passed")])
        ]);

        state.Apply(new WorkItemUpdated(item with { Station = null }));

        Assert.NotNull(state.VerdictFor(item.Id));
    }

    [Fact]
    public void A_later_verdict_replaces_an_earlier_one()
    {
        var item = WorkItem.Create("thing") with { AcceptanceCriteria = [Command("c1", "one")] };
        var state = FactoryState.Replay([
            new WorkItemFiled(item),
            new CriteriaVerified(item.Id, [CriterionResult.Fail("c1", "failed")]),
            new CriteriaVerified(item.Id, [CriterionResult.Pass("c1", "passed after a retry")])
        ]);

        Assert.True(state.VerdictFor(item.Id)!.AllPassed);
    }
}
