using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

/// <summary>
/// The whole integration, against a real beads database in a temp directory: selecting the provider
/// by config, deploying it, holding a claim open across a run, requeueing an orphan, and returning
/// work to a queue it can actually be claimed from again.
///
/// A deployment per test rather than a shared fixture: each test opens its own factory, and a
/// shared ledger would let one test's Ready item answer another's claim.
///
/// Every test here returns at <c>if (!Available) return;</c> when <c>bd</c> is absent, and xunit
/// 2.9.2 has no dynamic skip, so it reports as passed rather than skipped.
/// <see cref="BeadsAvailabilityTests"/> is the one red that says so.
/// </summary>
public sealed class BeadsBackedFactoryTests : IDisposable
{
    private const string Owner = "test-machine";

    private readonly string _dir = TempDir.Create();
    private static bool Available => Shell.Which("bd");

    public BeadsBackedFactoryTests() => Shell.Run("git", ["init", "-q", "."], _dir);
    public void Dispose() => TempDir.Delete(_dir);

    private const string SingleChild =
        """{"children":[{"key":"a","title":"do it","kind":"Feature","requirements":["works"],"acceptanceCriteria":[]}]}""";

    private const string Plan =
        """{"files":[{"path":"hello.txt","change":"create"}],"steps":["write the file"],"risks":[]}""";

    private static FakeTransport Scripted() =>
        new FakeTransport()
            .Respond("decompose", SingleChild)
            .Respond("plan", Plan)
            .Respond("implement", request =>
            {
                File.WriteAllText(Path.Combine(request.WorkingDirectory!, "hello.txt"), "hi\n");
                return FakeTransport.Success("wrote the file", cost: 0.02m);
            });

    private FactoryHost OpenBeadsBacked(FakeTransport transport, Action<string>? log = null) =>
        FactoryHost.Init(_dir, config: new FactoryConfig
        {
            Name = Owner,
            BlueprintName = Blueprint.Standard().Name,
            MaxConcurrency = 1,
            WorkItemStore = new ProviderRef("beads")
        }, log: log, transport: transport);

    private BeadRecord Bead(string id) =>
        new BeadsCli(_dir, Owner).Json<BeadRecord>([.. BeadMapper.GetArgs(id)]).Single();

    [Fact]
    public void SelectingTheProviderByConfigFilesWorkIntoBeads()
    {
        if (!Available) return;
        using var host = OpenBeadsBacked(Scripted());

        var item = host.Submit(WorkItem.Create("prove the beads backlog works"));

        // Authoritative means the item is really in beads, not only in the ledger fold.
        Assert.Equal("prove the beads backlog works", Bead(item.Id).Title);
        Assert.Contains(item.Id, host.Services.State.Items.Keys);
    }

    [Fact]
    public async Task AClaimIsRefreshedWhileItsStationWorks()
    {
        if (!Available) return;

        // Observed from inside the run, while the item is still in progress: once it finishes it is
        // closed and has no lease left, so anything asserted afterwards would pass either way.
        string? runItemId = null;
        BeadRecord? atWindowStart = null, afterWindow = null;

        var transport = new FakeTransport()
            .Respond("decompose", SingleChild)
            .Respond("plan", Plan)
            .Respond("implement", request =>
            {
                atWindowStart ??= Bead(runItemId!);
                afterWindow = WaitForARefresh(runItemId!, atWindowStart.HeartbeatAt);
                File.WriteAllText(Path.Combine(request.WorkingDirectory!, "hello.txt"), "hi\n");
                return FakeTransport.Success("wrote the file", cost: 0.02m);
            });

        using var host = OpenBeadsBacked(transport);

        // Named rather than read back as whatever bd currently lists in progress: the assertion below
        // compares two reads of one bead, and only an id fixed up front guarantees they are the same
        // bead.
        runItemId = host.Submit(WorkItem.Create("held across a run")).Id;

        await host.CreateOrchestrator().RunAsync(new OrchestratorOptions
        {
            StopWhenIdle = true,
            MaxItems = 1,
            // The real cadence is minutes, so a run measured in milliseconds needs it lowered for
            // a refresh to fall inside the window this observes.
            LeaseRefreshInterval = TimeSpan.FromMilliseconds(40)
        });

        Assert.NotNull(atWindowStart);
        Assert.Equal(WorkItemState.InProgress, BeadMapper.StateFor(atWindowStart!.Status));

        // Compared against the lease as it stood when the station started work, not against the grant:
        // bd stamps heartbeat_at to the second, so a refresh landing in the same wall-clock second as
        // the claim leaves the two equal and a `> StartedAt` assertion fails a factory that is
        // refreshing perfectly well. Waiting for the stamp to move is the same in-window capture plus
        // bounded fallback the neighbouring refresh test uses, and it fails only if no refresh ever
        // lands — which is what this exists to catch.
        //
        // With MaxItems of 1 the claim loop exits immediately after claiming, so the whole station
        // run happens in the drain that follows it. A refresh driven from the poll loop — which is
        // what the plan specified — would never fire here at all.
        Assert.True(afterWindow!.HeartbeatAt > atWindowStart.HeartbeatAt,
            "the claim should have been refreshed while its station worked: window opened at " +
            $"{atWindowStart.HeartbeatAt:O}, closed at {afterWindow.HeartbeatAt:O}");
    }

    /// <summary>Reads the bead until its lease has been pushed out past <paramref name="since"/>,
    /// giving up after a bound generous enough that only a refresh that never comes ends the wait.
    /// The fallback for a loaded machine that has not landed a refresh inside the fixed window, so it
    /// costs the suite time rather than a false failure; the caller re-reads the item it compares
    /// against afterwards, so extending the window never leaves the two halves measuring different
    /// spans.</summary>
    private BeadRecord WaitForARefresh(string id, DateTimeOffset? since)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        var bead = Bead(id);
        while (!(bead.HeartbeatAt > since) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(300);
            bead = Bead(id);
        }

        return bead;
    }

    [Fact]
    public async Task ARefreshFollowsThisRunsOwnClaimsRatherThanTheFoldsInProgressItems()
    {
        if (!Available) return;

        // Observed from inside the run, either side of a window wide enough for several refresh
        // ticks: once the run finishes there is no lease left to observe.
        string? runItemId = null, bystanderId = null;
        BeadRecord? mineAtWindowStart = null, mineAfterWindow = null;
        BeadRecord? bystanderAtWindowStart = null, bystanderAfterWindow = null;
        FactoryHost? opened = null;
        var observed = false;

        var transport = new FakeTransport()
            .Respond("decompose", SingleChild)
            .Respond("plan", Plan)
            .Respond("implement", request =>
            {
                // A failed gate routes the pipeline back through implement, and the window below is
                // only meaningful the first time.
                if (!observed)
                {
                    observed = true;
                    ObserveARefreshWindow();
                }

                File.WriteAllText(Path.Combine(request.WorkingDirectory!, "hello.txt"), "hi\n");
                return FakeTransport.Success("wrote the file", cost: 0.02m);
            });

        void ObserveARefreshWindow()
        {
            var store = opened!.Services.Items;

            // Exactly what a swallowed mirror append leaves behind: the bead is claimed under a live
            // five-minute lease and the fold still holds its pre-claim copy. The mirror tolerates a
            // failed ledger append by design (D2), so this is a state the factory really reaches —
            // and the claim is still this run's to keep alive.
            var folded = opened.Services.State.Items[runItemId!];
            opened.Services.State.Apply(new WorkItemUpdated(folded with { State = WorkItemState.Ready }));

            // A claim this run did not take. Held by this checkout so bd would accept a heartbeat for
            // it, and InProgress in the fold so a fold-driven refresh would send one: a genuinely
            // foreign claim cannot show the difference, because bd refuses a heartbeat on a bead
            // another actor holds (probed against 1.2.1: exit 1, nothing written).
            var filed = store.Add(WorkItem.Create("claimed outside this run") with { State = WorkItemState.Ready });
            bystanderId = store.TryClaim(Owner)?.Id;
            if (bystanderId != filed.Id) return;

            // bd stamps heartbeat_at to the second, so the window has to span one.
            Thread.Sleep(1000);
            mineAtWindowStart = Bead(runItemId!);
            bystanderAtWindowStart = Bead(bystanderId);
            Thread.Sleep(2500);
            mineAfterWindow = Bead(runItemId!);
            bystanderAfterWindow = Bead(bystanderId);

            // Both halves are measured over one window, so the negative half can never pass because
            // the positive one was satisfied somewhere the negative one was not looking. A machine too
            // loaded to have landed a refresh yet extends the window rather than failing the run, and
            // the bystander is then re-read over that same longer window.
            if (!(mineAfterWindow.HeartbeatAt > mineAtWindowStart.HeartbeatAt))
            {
                mineAfterWindow = WaitForARefresh(runItemId!, mineAtWindowStart.HeartbeatAt);
                bystanderAfterWindow = Bead(bystanderId);
            }
        }

        using var host = OpenBeadsBacked(transport);
        opened = host;

        // The one Ready bead in the backlog, so it is the one item the run below claims — read from
        // here rather than from whatever bd currently lists as in progress, which stops identifying
        // it uniquely the moment the window opens a second claim.
        runItemId = host.Submit(WorkItem.Create("held across a run the fold lost track of")).Id;

        await host.CreateOrchestrator().RunAsync(new OrchestratorOptions
        {
            StopWhenIdle = true,
            MaxItems = 1,
            LeaseRefreshInterval = TimeSpan.FromMilliseconds(200)
        });

        Assert.NotNull(mineAtWindowStart);
        Assert.NotEqual(runItemId, bystanderId);

        // Refreshed even though the fold never learned about the claim. Refreshing what the fold
        // calls InProgress instead leaves a lost append running the item with no heartbeats at all,
        // its lease expiring mid-run, and the next Reclaim handing the work to a second claimant
        // while the first is still working on it.
        Assert.True(mineAfterWindow!.HeartbeatAt > mineAtWindowStart.HeartbeatAt,
            "the claim this run holds should have been refreshed: window opened at " +
            $"{mineAtWindowStart.HeartbeatAt:O}, closed at {mineAfterWindow.HeartbeatAt:O}");

        // And nothing sent for a claim this run does not hold, in the very window that just proved
        // the refresh pass was running. A fold-driven pass spends a bd subprocess per tick on it.
        Assert.Equal(WorkItemState.InProgress, BeadMapper.StateFor(bystanderAtWindowStart!.Status));
        Assert.Equal(bystanderAtWindowStart.HeartbeatAt, bystanderAfterWindow!.HeartbeatAt);
        Assert.Equal(bystanderAtWindowStart.LeaseExpiresAt, bystanderAfterWindow.LeaseExpiresAt);
    }

    [Fact]
    public async Task AnOrphanIsRequeuedWithItsClaimDroppedSoAnotherMachineCanTakeIt()
    {
        if (!Available) return;

        using (var host = OpenBeadsBacked(Scripted()))
        {
            var store = host.Services.Items;

            // Stand in for a crash: claimed, in progress, lease held, then the process died.
            store.Add(WorkItem.Create("interrupted") with { State = WorkItemState.Ready });
            var claimed = store.TryClaim(Owner)!;
            Assert.Equal(Owner, Bead(claimed.Id).Assignee);

            await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true, MaxItems = 0 });

            var bead = Bead(claimed.Id);
            Assert.Equal(WorkItemState.Ready, BeadMapper.StateFor(bead.Status));
            Assert.True(string.IsNullOrEmpty(bead.Assignee),
                $"a requeued orphan that keeps its assignee cannot be taken by another machine, was '{bead.Assignee}'");
            Assert.Null(bead.LeaseExpiresAt);
        }

        // The requeue above released this checkout's own orphan through the same Release path a
        // reopen has to agree with. If Release never told the fold the owner was cleared, the
        // fold's stale copy would disagree with the now-unassigned bead on every open from here on.
        var log = new List<string>();
        using var reopened = FactoryHost.Open(_dir, log.Add, transport: new FakeTransport());
        Assert.DoesNotContain(log, message => message.Contains("reconciled"));
    }

    [Fact]
    public async Task AnOrphanTheBacklogWillNotTakeBackIsReportedAndTheRestAreRequeued()
    {
        if (!Available) return;

        var log = new List<string>();
        using var host = OpenBeadsBacked(new FakeTransport(), log.Add);
        var store = host.Services.Items;

        store.Add(WorkItem.Create("closed behind the fold's back") with { State = WorkItemState.Ready });
        var closedElsewhere = store.TryClaim(Owner)!;
        store.Add(WorkItem.Create("my other interrupted work") with { State = WorkItemState.Ready });
        var mine = store.TryClaim(Owner)!;

        // Straight through bd, so the fold keeps calling it in flight: the orphan requeue picks its
        // targets from the fold while the backlog decides whether a release is legal, and this is the
        // divergence between the two — another machine finishing the item between this host's open and
        // the run start below.
        var closed = new BeadsCli(_dir, Owner).Exec("update", closedElsewhere.Id, "--status", "closed", "--actor", Owner);
        Assert.True(closed.Ok, closed.Combined);
        Assert.Equal(WorkItemState.InProgress, host.Services.State.Items[closedElsewhere.Id].State);

        // A release beads refuses arrives here as a WorkItemStoreException, and one orphan that
        // cannot go back on the queue must not stop the factory before it has started.
        await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true, MaxItems = 0 });

        Assert.Contains(log, message => message.Contains(closedElsewhere.Id));

        // Integrated work stays integrated — the failure is reported, not forced through.
        Assert.Equal(WorkItemState.Done, store.Get(closedElsewhere.Id)!.State);

        // And the orphan that could be requeued was, which is what makes this tolerance rather than
        // an abandoned pass: claimable again, not merely Ready.
        Assert.Equal(mine.Id, store.TryClaim(Owner)?.Id);
    }

    [Fact]
    public void ReopeningAnUnchangedBacklogReportsNoCorrections()
    {
        if (!Available) return;

        using (var host = OpenBeadsBacked(Scripted()))
            host.Submit(WorkItem.Create("filed here", "and unchanged since"));

        var log = new List<string>();
        using var reopened = FactoryHost.Open(_dir, log.Add, transport: Scripted());

        // An item filed by this checkout is already in the ledger, so a reopen has nothing to
        // correct. If it corrects anyway, every open rewrites the whole backlog into the ledger
        // for as long as the factory lives.
        Assert.DoesNotContain(log, message => message.Contains("reconciled"));
    }

    [Fact]
    public async Task ReopeningAfterAClaimReturnsToTheQueueReportsNoCorrections()
    {
        if (!Available) return;

        using var cancellation = new CancellationTokenSource();

        // Claimed, then cancelled mid-station and returned to Ready — the same path
        // AnItemACancelledRunReturnsToTheQueueIsClaimableAgain exercises, but this test
        // cares about what the fold learned from it rather than about the bead itself.
        var transport = new FakeTransport().Respond("decompose", _ =>
        {
            cancellation.Cancel();
            return FakeTransport.Success(SingleChild);
        });

        using (var host = OpenBeadsBacked(transport))
        {
            host.Submit(WorkItem.Create("claimed then requeued"));

            await host.CreateOrchestrator().RunAsync(
                new OrchestratorOptions { StopWhenIdle = true, MaxItems = 1 }, cancellation.Token);
        }

        var log = new List<string>();
        using var reopened = FactoryHost.Open(_dir, log.Add, transport: new FakeTransport());

        // The claim and its release both happened inside the first factory's own run, so its own
        // ledger already agrees with the unassigned bead. If the mirror never carried Owner, the
        // fold's stale copy of it would disagree with beads on every open from here on.
        Assert.DoesNotContain(log, message => message.Contains("reconciled"));
    }

    // Every path that puts work back on the queue asserts the item is claimable again rather than
    // that its status is Ready. bd's `ready --claim` skips an open bead that still carries an
    // assignee — even for the actor named in it — so an item returned to Ready with its claim
    // intact is Ready everywhere and claimable nowhere, and a status assertion sees nothing wrong.

    [Fact]
    public void ActivatingABlockedItemLeavesItClaimableAgain()
    {
        if (!Available) return;
        using var host = OpenBeadsBacked(new FakeTransport());
        var store = host.Services.Items;

        store.Add(WorkItem.Create("blocked on a person") with { State = WorkItemState.Ready });
        var blocked = host.Transition(store.TryClaim(Owner)!, WorkItemState.Blocked, "needs a decision");

        host.Activate(blocked);

        Assert.Equal(blocked.Id, store.TryClaim(Owner)?.Id);
    }

    [Fact]
    public void RetryingAFailedItemLeavesItClaimableAgain()
    {
        if (!Available) return;
        using var host = OpenBeadsBacked(new FakeTransport());
        var store = host.Services.Items;

        store.Add(WorkItem.Create("failed on something outside itself") with { State = WorkItemState.Ready });
        var failed = host.Transition(store.TryClaim(Owner)!, WorkItemState.Failed, "verify gate failed");

        host.Activate(failed);

        Assert.Equal(failed.Id, store.TryClaim(Owner)?.Id);
    }

    [Fact]
    public async Task AnItemACancelledRunReturnsToTheQueueIsClaimableAgain()
    {
        if (!Available) return;

        using var cancellation = new CancellationTokenSource();

        // Ctrl-C while the first station is working. The next station's cancellation check unwinds
        // the run through the orchestrator's handler, which returns the item to Ready.
        var transport = new FakeTransport().Respond("decompose", _ =>
        {
            cancellation.Cancel();
            return FakeTransport.Success(SingleChild);
        });

        using var host = OpenBeadsBacked(transport);
        var item = host.Submit(WorkItem.Create("interrupted mid-run"));

        await host.CreateOrchestrator().RunAsync(
            new OrchestratorOptions { StopWhenIdle = true, MaxItems = 1 }, cancellation.Token);

        Assert.Equal(WorkItemState.Ready, host.Services.Items.Get(item.Id)!.State);
        Assert.Equal(item.Id, host.Services.Items.TryClaim(Owner)?.Id);
    }

    [Fact]
    public async Task RequeueingOrphansLeavesAnItemAnotherCheckoutHoldsInProgressAlone()
    {
        if (!Available) return;

        const string elsewhere = "other-machine";
        string foreignId;
        string mineId;

        using (var host = OpenBeadsBacked(new FakeTransport()))
        {
            var store = host.Services.Items;

            store.Add(WorkItem.Create("another checkout's work") with { State = WorkItemState.Ready });
            foreignId = new BeadsWorkItemStore(new BeadsCli(_dir, elsewhere), elsewhere, _ => { }).TryClaim(elsewhere)!.Id;

            store.Add(WorkItem.Create("my interrupted work") with { State = WorkItemState.Ready });
            mineId = store.TryClaim(Owner)!.Id;
        }

        // Reopening folds the foreign claim in from the shared backlog, which is how a claim this
        // checkout never made becomes visible to its orphan requeue in the first place.
        var log = new List<string>();
        using var reopened = FactoryHost.Open(_dir, log.Add, transport: new FakeTransport());
        Assert.Equal(elsewhere, reopened.Services.State.Items[foreignId].Owner);

        // bd refuses to clear the assignee of a bead another actor holds in progress, so requeueing
        // it would throw and take the whole run start down with it.
        await reopened.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true, MaxItems = 0 });

        var foreignBead = Bead(foreignId);
        Assert.Equal(WorkItemState.InProgress, BeadMapper.StateFor(foreignBead.Status));
        Assert.Equal(elsewhere, foreignBead.Assignee);
        // Reported as left alone, and specifically not as a requeue the backlog refused. bd's own
        // refusal text names the holder too, so a log assertion that only looks for the id and the
        // owner is satisfied by either path — and the two are not the same behaviour. Left alone
        // costs no bd call; attempted-and-refused spends a doomed write per foreign item on every run
        // start and rests on bd continuing to refuse it, which is not a guarantee the factory owns.
        Assert.Contains(log, message => message.Contains(foreignId) && message.Contains(elsewhere) &&
                                        message.Contains("still holds it"));
        Assert.DoesNotContain(log, message => message.Contains(foreignId) && message.Contains("refused"));

        // Reported, not reaped — and this checkout's own orphan is still requeued and claimable.
        Assert.Equal(mineId, reopened.Services.Items.TryClaim(Owner)?.Id);
    }

    // Beads stores no Station, Worktree, Attempts, LastError or SpentUsd — those are this checkout's
    // own run state — so an item it hands back carries them blank, and writing one of those into the
    // fold verbatim erases them.

    [Fact]
    public void AClaimKeepsTheRunStateTheBacklogDoesNotStore()
    {
        if (!Available) return;
        using var host = OpenBeadsBacked(new FakeTransport());
        var store = host.Services.Items;

        var filed = store.Add(WorkItem.Create("failed three times already") with { State = WorkItemState.Ready });
        store.Update(filed with
        {
            // Not the head of the standard pipeline, so surviving the claim is distinguishable from
            // Orchestrator's `claimed.Station ?? Pipeline.First()` fallback.
            Station = "verify",
            Attempts = 3,
            LastError = "verify gate failed",
            SpentUsd = 1.25m,
            Worktree = "/tmp/worktrees/failed-three-times"
        });

        var claimed = store.TryClaim(Owner)!;

        // The claim loop hands this very item to the station, whose run record reads Attempts off it,
        // and decides which station to resume at from Station.
        Assert.Equal(filed.Id, claimed.Id);
        Assert.Equal("verify", claimed.Station);
        Assert.Equal(3, claimed.Attempts);
        Assert.Equal("verify gate failed", claimed.LastError);
        Assert.Equal(1.25m, claimed.SpentUsd);
        Assert.Equal("/tmp/worktrees/failed-three-times", claimed.Worktree);

        // What beads is authoritative for still wins over the fold's copy.
        Assert.Equal(WorkItemState.InProgress, claimed.State);
        Assert.Equal(Owner, claimed.Owner);

        // And the fold's own copy, which is what `factory ls` prints in its cost column and
        // `factory show` prints as spend and attempts.
        var folded = host.Services.State.Items[filed.Id];
        Assert.Equal("verify", folded.Station);
        Assert.Equal(3, folded.Attempts);
        Assert.Equal("verify gate failed", folded.LastError);
        Assert.Equal(1.25m, folded.SpentUsd);
        Assert.Equal("/tmp/worktrees/failed-three-times", folded.Worktree);
    }

    [Fact]
    public async Task AStationWorkingARetriedItemIsToldWhichAttemptItIsOn()
    {
        if (!Available) return;
        using var host = OpenBeadsBacked(Scripted());

        var item = host.Submit(WorkItem.Create("failed three times already"));
        host.Update(item with { Attempts = 3, LastError = "verify gate failed" });

        await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true, MaxItems = 1 });

        // Read out of the ledger's run records, not the fold: this is the number the evolution
        // evaluator counts retries from, and the first record is appended before any failure inside
        // the run could have incremented it.
        var runs = host.Services.History.RunsForItem(item.Id);
        Assert.NotEmpty(runs);
        Assert.Equal(3, runs[0].Attempt);
    }
}
