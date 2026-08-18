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
