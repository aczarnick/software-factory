using Factory.Core;

namespace Factory.TestPlugin;

/// <summary>Fails while being constructed, the way a backlog store that probes its backend in
/// the constructor does when that backend is missing. Construction is the point; the members
/// never run.</summary>
[FactoryProvider("exploding-store", Contract = FactoryVersion.ContractVersion)]
public sealed class ExplodingStore : IWorkItemStore
{
    public ExplodingStore() => throw new InvalidOperationException("cannot reach the backlog");

    public WorkItem Add(WorkItem item) => throw new NotSupportedException();
    public WorkItem Update(WorkItem item) => throw new NotSupportedException();
    public WorkItem Transition(WorkItem item, WorkItemState target, string? reason) => throw new NotSupportedException();
    public WorkItem? Get(string id) => throw new NotSupportedException();
    public IReadOnlyList<WorkItem> All() => throw new NotSupportedException();
    public WorkItem? TryClaim(string owner) => throw new NotSupportedException();
    public void Heartbeat(string id) => throw new NotSupportedException();
    public void Release(string id, string reason) => throw new NotSupportedException();
    public void Sync() => throw new NotSupportedException();
    public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan) => throw new NotSupportedException();
}
