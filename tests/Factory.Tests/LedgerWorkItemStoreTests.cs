using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public sealed class LedgerWorkItemStoreTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private (LedgerWorkItemStore Store, JsonlRunHistory History) Open()
    {
        var history = new JsonlRunHistory(Path.Combine(_dir, "ledger.jsonl"));
        return (new LedgerWorkItemStore(history, history.Replay()), history);
    }

    [Fact]
    public void AddMakesTheItemReadable()
    {
        var (store, history) = Open();
        using var _ = history;

        var added = store.Add(WorkItem.Create("build a thing") with { State = WorkItemState.Ready });

        Assert.Equal("build a thing", store.Get(added.Id)!.Title);
        Assert.Single(store.All());
    }

    [Fact]
    public void TransitionRecordsTheNewState()
    {
        var (store, history) = Open();
        using var _ = history;

        var item = store.Add(WorkItem.Create("thing") with { State = WorkItemState.Ready });
        store.Transition(item, WorkItemState.InProgress, "dispatched");

        Assert.Equal(WorkItemState.InProgress, store.Get(item.Id)!.State);
    }

    [Fact]
    public void TransitionRejectsAnIllegalMove()
    {
        var (store, history) = Open();
        using var _ = history;

        var item = store.Add(WorkItem.Create("thing") with { State = WorkItemState.Ready });

        Assert.Throws<InvalidOperationException>(
            () => store.Transition(item, WorkItemState.Done, "skipping the pipeline"));
    }

    [Fact]
    public void TryClaimTakesTheHighestPriorityReadyItemAndMarksItInProgress()
    {
        var (store, history) = Open();
        using var _ = history;

        store.Add(WorkItem.Create("low") with { State = WorkItemState.Ready, Priority = 3 });
        store.Add(WorkItem.Create("high") with { State = WorkItemState.Ready, Priority = 0 });

        var claimed = store.TryClaim("machine-a");

        Assert.Equal("high", claimed!.Title);
        Assert.Equal(WorkItemState.InProgress, store.Get(claimed.Id)!.State);
    }

    [Fact]
    public void TryClaimWithholdsAnItemWhoseDependencyIsUnmet()
    {
        var (store, history) = Open();
        using var _ = history;

        var blocker = store.Add(WorkItem.Create("first") with { State = WorkItemState.Ready });
        store.Add(WorkItem.Create("second") with
        {
            State = WorkItemState.Ready,
            DependsOn = [blocker.Id]
        });

        var first = store.TryClaim("machine-a");
        var second = store.TryClaim("machine-a");

        Assert.Equal("first", first!.Title);
        Assert.Null(second);
    }

    [Fact]
    public void TryClaimReturnsNullWhenNothingIsReady()
    {
        var (store, history) = Open();
        using var _ = history;

        var proposal = store.Add(WorkItem.Create("proposal"));   // Draft, not Ready

        Assert.Null(store.TryClaim("machine-a"));
        Assert.Equal(WorkItemState.Draft, store.Get(proposal.Id)!.State);
    }

    [Fact]
    public void UpdatePersistsTheNewFieldValues()
    {
        var (store, history) = Open();
        using var _ = history;

        var item = store.Add(WorkItem.Create("thing") with { State = WorkItemState.Ready });

        var updated = store.Update(item with { Title = "renamed thing" });

        Assert.Equal("renamed thing", store.Get(item.Id)!.Title);
        Assert.True(updated.UpdatedAt > item.UpdatedAt);
    }

    [Fact]
    public void ReleaseReturnsAClaimedItemToTheQueue()
    {
        var (store, history) = Open();
        using var _ = history;

        store.Add(WorkItem.Create("thing") with { State = WorkItemState.Ready });
        var claimed = store.TryClaim("machine-a")!;

        store.Release(claimed.Id, "requeued after restart");

        Assert.Equal(WorkItemState.Ready, store.Get(claimed.Id)!.State);
    }

    [Fact]
    public void ItemsSurviveAReopen()
    {
        var (store, history) = Open();
        var added = store.Add(WorkItem.Create("durable") with { State = WorkItemState.Ready });
        history.Dispose();

        var (reopened, reopenedHistory) = Open();
        using var _ = reopenedHistory;

        Assert.Equal("durable", reopened.Get(added.Id)!.Title);
    }
}
