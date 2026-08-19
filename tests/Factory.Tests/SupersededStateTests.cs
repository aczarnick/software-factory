using Factory.Core;

namespace Factory.Tests;

/// <summary>A parent that decompose replaced with children was never verified against its own
/// acceptance criteria, so it must not be reported as Done. It leaves the pipeline as Superseded.</summary>
public class SupersededStateTests
{
    [Fact]
    public void A_decomposed_parent_may_leave_the_pipeline_as_superseded() =>
        Assert.True(WorkItemStates.CanTransition(WorkItemState.InProgress, WorkItemState.Superseded));

    [Fact]
    public void Superseded_is_terminal() =>
        Assert.True(WorkItemStates.IsTerminal(WorkItemState.Superseded));

    [Fact]
    public void Superseded_is_not_reachable_from_done() =>
        Assert.False(WorkItemStates.CanTransition(WorkItemState.Done, WorkItemState.Superseded));

    [Fact]
    public void Open_work_excludes_a_superseded_parent()
    {
        var parent = WorkItem.Create("a big thing") with { State = WorkItemState.Ready };
        var state = FactoryState.Replay([new WorkItemFiled(parent)]);

        state.Apply(new WorkItemStateChanged(parent.Id, WorkItemState.Ready, WorkItemState.InProgress));
        state.Apply(new WorkItemStateChanged(parent.Id, WorkItemState.InProgress, WorkItemState.Superseded));

        Assert.False(state.HasOpenWork(), "a superseded parent is finished; the daemon must be able to idle");
    }

    [Fact]
    public void A_superseded_parent_does_not_satisfy_a_dependency_while_a_child_is_outstanding()
    {
        var parent = WorkItem.Create("a big thing") with { State = WorkItemState.Ready };
        var child = WorkItem.Create("the real work") with { State = WorkItemState.Ready, ParentId = parent.Id };
        var dependent = WorkItem.Create("needs the big thing") with
        {
            State = WorkItemState.Ready,
            DependsOn = [parent.Id]
        };

        var state = FactoryState.Replay(
            [new WorkItemFiled(parent), new WorkItemFiled(child), new WorkItemFiled(dependent)]);

        state.Apply(new WorkItemStateChanged(parent.Id, WorkItemState.Ready, WorkItemState.InProgress));
        state.Apply(new WorkItemStateChanged(parent.Id, WorkItemState.InProgress, WorkItemState.Superseded));

        Assert.False(state.DependencySatisfied(parent.Id),
            "the parent was replaced by work that has not happened yet");
    }

    [Fact]
    public void A_superseded_parent_satisfies_a_dependency_once_every_child_is_done()
    {
        var parent = WorkItem.Create("a big thing") with { State = WorkItemState.Ready };
        var child = WorkItem.Create("the real work") with { State = WorkItemState.Ready, ParentId = parent.Id };

        var state = FactoryState.Replay([new WorkItemFiled(parent), new WorkItemFiled(child)]);

        state.Apply(new WorkItemStateChanged(parent.Id, WorkItemState.Ready, WorkItemState.InProgress));
        state.Apply(new WorkItemStateChanged(parent.Id, WorkItemState.InProgress, WorkItemState.Superseded));
        state.Apply(new WorkItemStateChanged(child.Id, WorkItemState.Ready, WorkItemState.Done));

        Assert.True(state.DependencySatisfied(parent.Id));
    }

    [Fact]
    public void A_superseded_parent_with_no_children_satisfies_nothing()
    {
        var parent = WorkItem.Create("a big thing") with { State = WorkItemState.Ready };
        var state = FactoryState.Replay([new WorkItemFiled(parent)]);

        state.Apply(new WorkItemStateChanged(parent.Id, WorkItemState.Ready, WorkItemState.InProgress));
        state.Apply(new WorkItemStateChanged(parent.Id, WorkItemState.InProgress, WorkItemState.Superseded));

        Assert.False(state.DependencySatisfied(parent.Id),
            "a parent superseded by nothing is a hole, not a completion");
    }
}
