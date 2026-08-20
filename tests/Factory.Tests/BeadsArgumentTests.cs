using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

/// <summary>The flag choices below were each derived from probing bd 1.2.1, and each one guards a
/// defect that is silent rather than loud if it regresses.</summary>
public class BeadsArgumentTests
{
    [Fact]
    public void ReadingTheWholeBacklogDefeatsTheDefaultPageSize()
    {
        var args = BeadMapper.AllArgs();

        // bd list pages at 50 by default, so without this a 51st item simply stops existing.
        Assert.Equal("0", ValueAfter(args, "--limit"));
        Assert.Contains("--all", args);
    }

    [Fact]
    public void ReadingOneBeadReachesEveryStatus()
    {
        var args = BeadMapper.GetArgs("wi-aaaa11112222");

        Assert.Contains("--all", args);
        Assert.Equal("wi-aaaa11112222", ValueAfter(args, "--id"));
    }

    [Fact]
    public void ClaimingNamesTheCheckoutAsTheAssignee()
    {
        var args = BeadMapper.ClaimArgs("node-a");

        Assert.Contains("--claim", args);
        Assert.Equal("node-a", ValueAfter(args, "--actor"));
    }

    [Fact]
    public void ReleasingClearsTheAssigneeAsWellAsTheStatus()
    {
        var args = BeadMapper.ReleaseArgs("wi-aaaa11112222", "node-a");

        Assert.Equal("open", ValueAfter(args, "--status"));
        Assert.Equal("", ValueAfter(args, "--assignee"));
        Assert.Equal("node-a", ValueAfter(args, "--actor"));
    }

    [Fact]
    public void ReleasingDoesNotDependOnTheBeadStillBeingClaimed()
    {
        // bd unclaim only works from in_progress; an orphan requeued from review would fail.
        Assert.DoesNotContain("unclaim", BeadMapper.ReleaseArgs("wi-aaaa11112222", "node-a"));
    }

    [Fact]
    public void UpdatingAnItemBackToTheQueueClearsTheAssignee()
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
    public void UpdatingWritesEveryFieldBeadsOwnsNatively()
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
    public void UpdatingSendsAnEmptiedDescriptionRatherThanOmittingTheFlag()
    {
        var args = BeadMapper.UpdateArgs(WorkItem.Create("nothing to say") with { State = WorkItemState.Ready }, "node-a");

        // bd clears the cell for `-d ""` and exits 0, so omitting the flag when the item has nothing
        // is the one shape that leaves behind a stale value claiming the item still says something it
        // no longer says. Safe only because Intent reads back from the bead's own description when the
        // factory has no metadata there, so an emptied Intent means the item really has none.
        Assert.Equal("", ValueAfter(args, "-d"));
    }

    [Fact]
    public void UpdatingAnItemWithNoCriteriaOfItsOwnLeavesTheBeadsAcceptanceCellAlone()
    {
        var args = BeadMapper.UpdateArgs(WorkItem.Create("no criteria") with { State = WorkItemState.Ready }, "node-a");

        // Deliberately asymmetric with -d above: criteria have no read-back fallback, so a bead
        // another tool filed arrives with none, and `--acceptance ""` would destroy its cell.
        Assert.DoesNotContain("--acceptance", args);
    }

    [Fact]
    public void UpdatingAnItemThatIsNotReturningToTheQueueLeavesTheAssigneeAlone()
    {
        var args = BeadMapper.UpdateArgs(WorkItem.Create("in flight") with { State = WorkItemState.InReview }, "node-a");

        // Only a return to the queue drops the claim. Clearing it on every update would hand work
        // still in flight to whichever machine claimed next.
        Assert.DoesNotContain("--assignee", args);
    }

    [Fact]
    public void TheWriteThatFinishesFilingRefusesToOverwriteABeadSomeoneElseClaimed()
    {
        var args = BeadMapper.FilingStatusArgs(WorkItem.Create("a proposal"), "node-a");

        // bd create has no status flag, so filing anything but Ready is two writes and the bead is
        // briefly claimable in between. Unguarded, the second write drags a bead another machine
        // claimed back to draft and drops the lease it is working under — exiting 0 while doing it.
        Assert.Equal("draft", ValueAfter(args, "--status"));
        Assert.Equal("open", ValueAfter(args, "--if-status"));
    }

    [Theory]
    [InlineData(90, "90s")]
    [InlineData(30, "30s")]
    [InlineData(900, "900s")]
    public void TheReclaimGraceWindowSurvivesASubMinuteValue(int seconds, string expected)
    {
        var args = BeadMapper.ReclaimArgs(TimeSpan.FromSeconds(seconds), "node-a");

        Assert.Equal(expected, ValueAfter(args, "--older-than"));
    }

    [Fact]
    public void ReclaimingIsScopedToThisCheckoutsOwnLeases()
    {
        var args = BeadMapper.ReclaimArgs(TimeSpan.FromMinutes(15), "node-a");

        // --actor is the audit trail and was already correct; --assignee is bd's scope filter
        // and is what flips the reclaim response's "scoped" field from false to true. Without
        // it, Reclaim reaps every stale lease in the shared store, not just this node's own.
        Assert.Equal("node-a", ValueAfter(args, "--actor"));
        Assert.Equal("node-a", ValueAfter(args, "--assignee"));
    }

    [Fact]
    public void AReclaimResponseReportsTheLeasesItReverted()
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
    public void AReclaimResponseWithNothingStaleIsNotAFailure()
    {
        // bd reports an absent list rather than an empty one, which a required member would reject.
        const string json = """{"count": 0, "reclaimed": null, "schema_version": 1, "scoped": false}""";

        var response = FactoryJson.Read<BeadsReclaimResponse>(json)!;

        Assert.Equal(0, response.Count);
        Assert.Null(response.Reclaimed);
    }

    [Fact]
    public void AddingAnEdgeNamesTheDependentBeforeTheBlocker()
    {
        var args = BeadMapper.DependencyAddArgs("wi-dependent0001", "wi-blocker000001", "node-a");

        // `bd dep add <dependent> <blocker>`. Reversed, the edge is still created and still exits 0 —
        // it just points the other way, so the wrong item is the one beads withholds.
        Assert.Equal(["dep", "add", "wi-dependent0001", "wi-blocker000001"], args.Take(4));
        Assert.Equal("node-a", ValueAfter(args, "--actor"));
    }

    [Fact]
    public void RemovingAnEdgeNamesTheDependentBeforeTheBlocker()
    {
        var args = BeadMapper.DependencyRemoveArgs("wi-dependent0001", "wi-blocker000001", "node-a");

        // Reversed here is worse than on add: `bd dep remove <blocker> <dependent>` prints
        // "✓ Removed dependency" and exits 0 while removing nothing at all, so the exit code cannot
        // be trusted to catch it and only the argument order can.
        Assert.Equal(["dep", "remove", "wi-dependent0001", "wi-blocker000001"], args.Take(4));
        Assert.Equal("node-a", ValueAfter(args, "--actor"));
    }

    [Fact]
    public void AnEdgeIsAddedAsATypeBeadsActuallyTreatsAsBlocking()
    {
        var args = BeadMapper.DependencyAddArgs("wi-dependent0001", "wi-blocker000001", "node-a");

        // bd has ten dependency types and withholds a dependent for exactly one of them. Any --type
        // this passed other than `blocks` (or its `blocked-by`/`depends-on` aliases) would file an
        // edge the factory reads as a blocker and bd ready ignores. bd's own default is `blocks`,
        // so the safe shape is to name no type at all.
        Assert.DoesNotContain("--type", args);
        Assert.DoesNotContain("-t", args);
    }

    [Fact]
    public void AnUpdateLeavesAStatusTheFactoryDoesNotOwnAlone()
    {
        var pinned = WorkItem.Create("a bead a human pinned") with
        {
            State = WorkItemState.Blocked,
            StoreStatus = "pinned"
        };

        var args = BeadMapper.UpdateArgs(pinned, "node-a");

        // bd accepts an update with no --status and leaves the cell untouched (probed against
        // 1.2.1), so the human's `pinned` survives every other field being written over it. Sending
        // the mapped `blocked` instead destroys it on the factory's first touch — the same
        // read-faithfully-then-write-back destruction this branch already closed for issue_type.
        Assert.DoesNotContain("--status", args);
        Assert.Equal("a bead a human pinned", ValueAfter(args, "--title"));
    }

    [Fact]
    public void AnUpdateWritesTheStatusOnceACallerHasMovedTheItemOffIt()
    {
        var activated = WorkItem.Create("a bead a human pinned") with
        {
            State = WorkItemState.Ready,
            StoreStatus = "pinned"
        };

        var args = BeadMapper.UpdateArgs(activated, "node-a");

        // Suppression is about whether anything asked for a change, not about the bead. A state that
        // no longer agrees with the carried status is an explicit request — `factory activate` on a
        // pinned bead — and has to reach beads rather than being silently dropped.
        Assert.Equal("open", ValueAfter(args, "--status"));
    }

    private static string ValueAfter(IReadOnlyList<string> args, string flag)
    {
        var index = args.ToList().IndexOf(flag);
        Assert.True(index >= 0 && index + 1 < args.Count, $"{flag} is not present with a value in [{string.Join(" ", args)}]");
        return args[index + 1];
    }
}
