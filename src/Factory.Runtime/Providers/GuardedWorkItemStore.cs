using Factory.Core;

namespace Factory.Runtime;

/// <summary>Translates any provider failure into <see cref="WorkItemStoreException"/> so a
/// broken backlog stops the factory loudly instead of silently returning an empty queue.</summary>
public sealed class GuardedWorkItemStore(IWorkItemStore inner, string providerName) : IWorkItemStore
{
    public WorkItem Add(WorkItem item) => Guard(nameof(Add), () => inner.Add(item));
    public WorkItem Update(WorkItem item) => Guard(nameof(Update), () => inner.Update(item));

    public WorkItem Transition(WorkItem item, WorkItemState target, string? reason) =>
        Guard(nameof(Transition), () => inner.Transition(item, target, reason));

    public WorkItem? Get(string id) => Guard(nameof(Get), () => inner.Get(id));
    public IReadOnlyList<WorkItem> All() => Guard(nameof(All), inner.All);
    public WorkItem? TryClaim(string owner) => Guard(nameof(TryClaim), () => inner.TryClaim(owner));
    public void Heartbeat(string id) => Guard(nameof(Heartbeat), () => { inner.Heartbeat(id); return 0; });
    public void Release(string id, string reason) => Guard(nameof(Release), () => { inner.Release(id, reason); return 0; });
    public void Sync() => Guard(nameof(Sync), () => { inner.Sync(); return 0; });

    public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan) =>
        Guard(nameof(Reclaim), () => inner.Reclaim(olderThan));

    private T Guard<T>(string operation, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not WorkItemStoreException)
        {
            throw new WorkItemStoreException(providerName, operation, ex);
        }
    }
}
