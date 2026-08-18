using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

/// <summary>The flag choices below were each derived from probing bd 1.2.1, and each one guards a
/// defect that is silent rather than loud if it regresses.</summary>
public class BeadsArgumentTests
{
    [Fact]
    public void Reading_the_whole_backlog_defeats_the_default_page_size()
    {
        var args = BeadMapper.AllArgs();

        // bd list pages at 50 by default, so without this a 51st item simply stops existing.
        Assert.Equal("0", ValueAfter(args, "--limit"));
        Assert.Contains("--all", args);
    }

    [Fact]
    public void Reading_one_bead_reaches_every_status()
    {
        var args = BeadMapper.GetArgs("wi-aaaa11112222");

        Assert.Contains("--all", args);
        Assert.Equal("wi-aaaa11112222", ValueAfter(args, "--id"));
    }

    [Fact]
    public void Claiming_names_the_checkout_as_the_assignee()
    {
        var args = BeadMapper.ClaimArgs("node-a");

        Assert.Contains("--claim", args);
        Assert.Equal("node-a", ValueAfter(args, "--actor"));
    }

    [Fact]
    public void Releasing_clears_the_assignee_as_well_as_the_status()
    {
        var args = BeadMapper.ReleaseArgs("wi-aaaa11112222", "node-a");

        Assert.Equal("open", ValueAfter(args, "--status"));
        Assert.Equal("", ValueAfter(args, "--assignee"));
        Assert.Equal("node-a", ValueAfter(args, "--actor"));
    }

    [Fact]
    public void Releasing_does_not_depend_on_the_bead_still_being_claimed()
    {
        // bd unclaim only works from in_progress; an orphan requeued from review would fail.
        Assert.DoesNotContain("unclaim", BeadMapper.ReleaseArgs("wi-aaaa11112222", "node-a"));
    }

    [Fact]
    public void Updating_an_item_back_to_the_queue_clears_the_assignee()
    {
        var args = BeadMapper.UpdateArgs(WorkItem.Create("requeued") with { State = WorkItemState.Ready }, "node-a");

        // bd's `ready --claim` skips an open bead that still carries an assignee — even for the
        // actor named in it — so an item updated to Ready with its claim intact is stranded:
        // Ready everywhere and claimable nowhere.
        Assert.Equal("open", ValueAfter(args, "--status"));
        Assert.Equal("", ValueAfter(args, "--assignee"));
        Assert.Equal("node-a", ValueAfter(args, "--actor"));
    }

    [Fact]
    public void Updating_writes_every_field_beads_owns_natively()
    {
        var args = BeadMapper.UpdateArgs(
            WorkItem.Create("a new title", "a new intent", WorkItemKind.Bug) with
            {
                State = WorkItemState.InReview,
                AcceptanceCriteria = [AcceptanceCriterion.Command("builds", "dotnet build")]
            },
            "node-a");

        // A field the update does not send is not merely unsaved: reconcile compares the mapped
        // projection and lets beads win, so the next open reverts the edit locally as well.
        Assert.Equal("a new title", ValueAfter(args, "--title"));
        Assert.Equal("bug", ValueAfter(args, "-t"));
        Assert.Equal("a new intent", ValueAfter(args, "-d"));
        Assert.Contains("builds", ValueAfter(args, "--acceptance"));

        // bd update has no --deps: a post-filing edge needs `bd dep add`, so pretending otherwise
        // here would fail the whole write and lose the fields above with it.
        Assert.DoesNotContain("--deps", args);
    }

    [Fact]
    public void Updating_sends_an_emptied_description_and_criteria_rather_than_omitting_the_flags()
    {
        var args = BeadMapper.UpdateArgs(WorkItem.Create("nothing to say") with { State = WorkItemState.Ready }, "node-a");

        // bd clears the cell for `-d ""` and `--acceptance ""` and exits 0, so omitting the flag
        // when the item has nothing is the one shape that leaves behind a stale value claiming the
        // item still says something it no longer says.
        Assert.Equal("", ValueAfter(args, "-d"));
        Assert.Equal("", ValueAfter(args, "--acceptance"));
    }

    [Fact]
    public void Updating_an_item_that_is_not_returning_to_the_queue_leaves_the_assignee_alone()
    {
        var args = BeadMapper.UpdateArgs(WorkItem.Create("in flight") with { State = WorkItemState.InReview }, "node-a");

        // Only a return to the queue drops the claim. Clearing it on every update would hand work
        // still in flight to whichever machine claimed next.
        Assert.DoesNotContain("--assignee", args);
    }

    [Theory]
    [InlineData(90, "90s")]
    [InlineData(30, "30s")]
    [InlineData(900, "900s")]
    public void The_reclaim_grace_window_survives_a_sub_minute_value(int seconds, string expected)
    {
        var args = BeadMapper.ReclaimArgs(TimeSpan.FromSeconds(seconds), "node-a");

        Assert.Equal(expected, ValueAfter(args, "--older-than"));
    }

    [Fact]
    public void Reclaiming_is_scoped_to_this_checkouts_own_leases()
    {
        var args = BeadMapper.ReclaimArgs(TimeSpan.FromMinutes(15), "node-a");

        // --actor is the audit trail and was already correct; --assignee is bd's scope filter
        // and is what flips the reclaim response's "scoped" field from false to true. Without
        // it, Reclaim reaps every stale lease in the shared store, not just this node's own.
        Assert.Equal("node-a", ValueAfter(args, "--actor"));
        Assert.Equal("node-a", ValueAfter(args, "--assignee"));
    }

    [Fact]
    public void A_reclaim_response_reports_the_leases_it_reverted()
    {
        // Captured verbatim from `bd reclaim --older-than 0s --json` after a lease expired.
        const string json = """
            {"count": 1,
             "reclaimed": [{"id": "wi-aaaa11112222", "previous_owner": "node-a"}],
             "schema_version": 1, "scoped": false}
            """;

        var response = FactoryJson.Read<BeadsReclaimResponse>(json)!;

        Assert.Equal(1, response.Count);
        Assert.Equal("wi-aaaa11112222", Assert.Single(response.Reclaimed!).Id);
        Assert.Equal("node-a", response.Reclaimed![0].PreviousOwner);
    }

    [Fact]
    public void A_reclaim_response_with_nothing_stale_is_not_a_failure()
    {
        // bd reports an absent list rather than an empty one, which a required member would reject.
        const string json = """{"count": 0, "reclaimed": null, "schema_version": 1, "scoped": false}""";

        var response = FactoryJson.Read<BeadsReclaimResponse>(json)!;

        Assert.Equal(0, response.Count);
        Assert.Null(response.Reclaimed);
    }

    private static string ValueAfter(IReadOnlyList<string> args, string flag)
    {
        var index = args.ToList().IndexOf(flag);
        Assert.True(index >= 0 && index + 1 < args.Count, $"{flag} is not present with a value in [{string.Join(" ", args)}]");
        return args[index + 1];
    }
}
