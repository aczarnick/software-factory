using Factory.Core;

namespace Factory.Tests;

/// <summary>A parent that decompose replaced with children was never verified against its own
/// acceptance criteria, so it must not be reported as Done. It leaves the pipeline as Superseded.</summary>
public class SupersededStateTests
{
    [Fact]
    public void ADecomposedParentMayLeaveThePipelineAsSuperseded() =>
        Assert.True(WorkItemStates.CanTransition(WorkItemState.InProgress, WorkItemState.Superseded));

    [Fact]
    public void SupersededIsTerminal() =>
        Assert.True(WorkItemStates.IsTerminal(WorkItemState.Superseded));

    [Fact]
    public void SupersededIsNotReachableFromDone() =>
        Assert.False(WorkItemStates.CanTransition(WorkItemState.Done, WorkItemState.Superseded));

    [Fact]
    public void OpenWorkExcludesASupersededParent()
    {
        var parent = WorkItem.Create("a big thing") with { State = WorkItemState.Ready };
        var state = FactoryState.Replay([new WorkItemFiled(parent)]);

        state.Apply(new WorkItemStateChanged(parent.Id, WorkItemState.Ready, WorkItemState.InProgress));
        state.Apply(new WorkItemStateChanged(parent.Id, WorkItemState.InProgress, WorkItemState.Superseded));

        Assert.False(state.HasOpenWork(), "a superseded parent is finished; the daemon must be able to idle");
    }

    [Fact]
    public void ASupersededParentDoesNotSatisfyADependencyWhileAChildIsOutstanding()
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
    public void ASupersededParentSatisfiesADependencyOnceEveryChildIsDone()
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
    public void ASupersededParentWithNoChildrenSatisfiesNothing()
    {
        var parent = WorkItem.Create("a big thing") with { State = WorkItemState.Ready };
        var state = FactoryState.Replay([new WorkItemFiled(parent)]);

        state.Apply(new WorkItemStateChanged(parent.Id, WorkItemState.Ready, WorkItemState.InProgress));
        state.Apply(new WorkItemStateChanged(parent.Id, WorkItemState.InProgress, WorkItemState.Superseded));

        Assert.False(state.DependencySatisfied(parent.Id),
            "a parent superseded by nothing is a hole, not a completion");
    }
}
