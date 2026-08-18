using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public class BacklogReconcilerTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private JsonlRunHistory History() => new(Path.Combine(_dir, "ledger.jsonl"));

    private sealed class StubStore(List<WorkItem> items) : IWorkItemStore
    {
        public WorkItem Add(WorkItem item) { items.Add(item); return item; }
        public WorkItem Update(WorkItem item) => item;
        public WorkItem Transition(WorkItem item, WorkItemState to, string? reason) => item with { State = to };
        public WorkItem? Get(string id) => items.FirstOrDefault(i => i.Id == id);
        public IReadOnlyList<WorkItem> All() => items;
        public WorkItem? TryClaim(string owner) => null;
        public void Heartbeat(string id) { }
        public void Release(string id, string reason) { }
        public void Sync() { }
        public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan) => [];
    }

    [Fact]
    public void The_store_wins_when_the_ledger_disagrees()
    {
        using var history = History();
        var item = WorkItem.Create("thing") with { State = WorkItemState.Ready };

        history.Append(new WorkItemFiled(item));
        var state = history.Replay();

        var store = new StubStore([item with { State = WorkItemState.Done }]);
        BacklogReconciler.Reconcile(store, state, history, _ => { });

        Assert.Equal(WorkItemState.Done, state.Items[item.Id].State);
    }

    [Fact]
    public void An_item_only_the_store_knows_about_is_folded_in()
    {
        using var history = History();
        var state = history.Replay();
        var remote = WorkItem.Create("filed elsewhere") with { State = WorkItemState.Ready };

        BacklogReconciler.Reconcile(new StubStore([remote]), state, history, _ => { });

        Assert.True(state.Items.ContainsKey(remote.Id));
    }

    [Fact]
    public void A_correction_survives_a_restart_because_it_is_written_to_the_ledger()
    {
        var item = WorkItem.Create("thing") with { State = WorkItemState.Ready };

        using (var history = History())
        {
            history.Append(new WorkItemFiled(item));
            var state = history.Replay();
            BacklogReconciler.Reconcile(
                new StubStore([item with { State = WorkItemState.Done }]), state, history, _ => { });
        }

        using var reopened = History();
        Assert.Equal(WorkItemState.Done, reopened.Replay().Items[item.Id].State);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("priority")]
    [InlineData("dependencies")]
    [InlineData("criteria")]
    public void The_store_wins_for_every_field_it_owns_not_only_the_state(string field)
    {
        using var history = History();
        var item = WorkItem.Create("original title") with
        {
            State = WorkItemState.Ready,
            Priority = Priorities.Default
        };

        history.Append(new WorkItemFiled(item));
        var state = history.Replay();

        // Same state, one other authoritative field changed on another machine.
        var authoritative = field switch
        {
            "title" => item with { Title = "renamed elsewhere" },
            "priority" => item with { Priority = Priorities.Highest },
            "dependencies" => item with { DependsOn = ["wi-aaaa11112222"] },
            "criteria" => item with { AcceptanceCriteria = [AcceptanceCriterion.Command("runs", "dotnet run")] },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        BacklogReconciler.Reconcile(new StubStore([authoritative]), state, history, _ => { });

        var reconciled = state.Items[item.Id];
        Assert.Equal(authoritative.Title, reconciled.Title);
        Assert.Equal(authoritative.Priority, reconciled.Priority);
        Assert.Equal(authoritative.DependsOn, reconciled.DependsOn);
        Assert.Equal(authoritative.AcceptanceCriteria.Count, reconciled.AcceptanceCriteria.Count);
    }

    [Fact]
    public void A_correction_keeps_the_local_run_state_the_backlog_does_not_store()
    {
        using var history = History();

        // Mid-pipeline work: the station and worktree are how a blocked item resumes without
        // redoing verified work, and beads has no field for either.
        var local = WorkItem.Create("in flight") with
        {
            State = WorkItemState.InProgress,
            Station = "implement",
            Worktree = "/tmp/wt-1",
            Attempts = 2,
            LastError = "flaky gate",
            SpentUsd = 0.42m
        };

        history.Append(new WorkItemFiled(local));
        var state = history.Replay();

        BacklogReconciler.Reconcile(
            new StubStore([local with { State = WorkItemState.Blocked, Station = null, Worktree = null, Attempts = 0, SpentUsd = 0m, LastError = null }]),
            state, history, _ => { });

        var reconciled = state.Items[local.Id];
        Assert.Equal(WorkItemState.Blocked, reconciled.State);
        Assert.Equal("implement", reconciled.Station);
        Assert.Equal("/tmp/wt-1", reconciled.Worktree);
        Assert.Equal(2, reconciled.Attempts);
        Assert.Equal("flaky gate", reconciled.LastError);
        Assert.Equal(0.42m, reconciled.SpentUsd);
    }

    [Fact]
    public void An_agreeing_backlog_writes_nothing()
    {
        using var history = History();
        var item = WorkItem.Create("thing") with { State = WorkItemState.Ready };
        history.Append(new WorkItemFiled(item));
        var state = history.Replay();
        var before = history.ReadFrom(0).Count();

        BacklogReconciler.Reconcile(new StubStore([item]), state, history, _ => { });

        // A reconcile pass over an unchanged backlog must not grow the ledger on every open.
        Assert.Equal(before, history.ReadFrom(0).Count());
    }

    [Fact]
    public void Reconciling_twice_over_the_same_divergence_corrects_once()
    {
        using var history = History();
        var item = WorkItem.Create("thing") with { State = WorkItemState.Ready };
        history.Append(new WorkItemFiled(item));
        var state = history.Replay();
        var store = new StubStore([item with { State = WorkItemState.Done }]);

        BacklogReconciler.Reconcile(store, state, history, _ => { });
        var afterFirst = history.ReadFrom(0).Count();
        BacklogReconciler.Reconcile(store, state, history, _ => { });

        Assert.Equal(afterFirst, history.ReadFrom(0).Count());
    }

    [Fact]
    public void The_number_of_corrections_is_reported()
    {
        using var history = History();
        var item = WorkItem.Create("thing") with { State = WorkItemState.Ready };
        history.Append(new WorkItemFiled(item));
        var state = history.Replay();
        var messages = new List<string>();

        BacklogReconciler.Reconcile(
            new StubStore([item with { State = WorkItemState.Done }]), state, history, messages.Add);

        Assert.Contains(messages, m => m.Contains('1'));
    }
}
