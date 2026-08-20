using System.Diagnostics.CodeAnalysis;

namespace Factory.Core;

/// <summary>The backlog. Exactly one provider is active: item state has a single authority,
/// and two stores would mean two truths.</summary>
public interface IWorkItemStore
{
    WorkItem Add(WorkItem item);
    WorkItem Update(WorkItem item);
    WorkItem Transition(WorkItem item, WorkItemState target, string? reason);

    // Renaming this member is a plugin ABI break: FactoryProviderAttribute-marked plugins
    // implement this interface as a typed C# member, so an already-compiled plugin binary
    // would no longer satisfy IWorkItemStore and would fail to cast at ProviderRegistry.Resolve.
    [SuppressMessage("Naming", "CA1716", Justification =
        "Renaming Get would break already-compiled plugins implementing this interface (see FactoryProviderAttribute).")]
    WorkItem? Get(string id);
    IReadOnlyList<WorkItem> All();

    /// <summary>Atomically takes the highest-priority dispatchable item and marks it
    /// in progress. Returns null when nothing is ready.</summary>
    WorkItem? TryClaim(string owner);

    void Heartbeat(string id);

    /// <summary>Returns a claimed item to the queue. Silently does nothing when no item
    /// has that id; rejects an item whose current state cannot reach Ready. Providers raise
    /// that as <see cref="InvalidOperationException"/>, but the host wraps every store in a
    /// guard, so the exception a caller of <c>Services.Items</c> observes is always
    /// <see cref="WorkItemStoreException"/> with the provider's own exception inside.</summary>
    void Release(string id, string reason);

    void Sync();
    IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan);
}
