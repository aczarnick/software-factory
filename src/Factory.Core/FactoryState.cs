namespace Factory.Core;

/// <summary>
/// Materialised view of the ledger. Never mutated directly by callers — it is rebuilt by
/// folding events, which is what makes the factory crash-resumable.
/// </summary>
public sealed class FactoryState
{
    // Stations run concurrently, so every read and write of the fold is guarded. Queries
    // return snapshots rather than live views for the same reason.
    private readonly Lock _gate = new();
    private readonly Dictionary<string, WorkItem> _items = [];
    private readonly List<RunRecord> _runs = [];
    private readonly Dictionary<string, string> _champions = [];
    private readonly Dictionary<string, string> _linkedFactories = [];

    public IReadOnlyDictionary<string, WorkItem> Items
    {
        get { lock (_gate) return new Dictionary<string, WorkItem>(_items); }
    }

    public IReadOnlyList<RunRecord> Runs
    {
        get { lock (_gate) return [.. _runs]; }
    }

    /// <summary>Current champion prompt version per station.</summary>
    public IReadOnlyDictionary<string, string> Champions
    {
        get { lock (_gate) return new Dictionary<string, string>(_champions); }
    }

    public IReadOnlyDictionary<string, string> LinkedFactories
    {
        get { lock (_gate) return new Dictionary<string, string>(_linkedFactories); }
    }

    public long LastSeq { get; private set; }
    public decimal TotalSpentUsd { get { lock (_gate) return _runs.Sum(r => r.CostUsd); } }
    public TokenUsage TotalUsage { get { lock (_gate) return _runs.Aggregate(TokenUsage.Zero, (a, r) => a + r.Usage); } }

    public static FactoryState Replay(IEnumerable<FactoryEvent> events)
    {
        var state = new FactoryState();
        foreach (var e in events) state.Apply(e);
        return state;
    }

    public void Apply(FactoryEvent evt)
    {
        lock (_gate) ApplyLocked(evt);
    }

    private void ApplyLocked(FactoryEvent evt)
    {
        LastSeq = Math.Max(LastSeq, evt.Seq);

        switch (evt)
        {
            case WorkItemFiled f:
                _items[f.Item.Id] = f.Item;
                break;

            case WorkItemUpdated u:
                _items[u.Item.Id] = u.Item;
                break;

            case WorkItemStateChanged c when _items.TryGetValue(c.ItemId, out var item):
                _items[c.ItemId] = item with { State = c.To, UpdatedAt = c.At };
                break;

            case RunCompleted r:
                _runs.Add(r.Record);
                if (_items.TryGetValue(r.Record.ItemId, out var ri))
                    _items[r.Record.ItemId] = ri with { SpentUsd = ri.SpentUsd + r.Record.CostUsd };
                break;

            case PromptPromoted p:
                _champions[p.StationId] = p.ToVersion;
                break;

            case PromptDemoted d:
                _champions[d.StationId] = d.ToVersion;
                break;

            case FactoryLinked l:
                _linkedFactories[l.ChildName] = l.ChildPath;
                break;
        }
    }

    /// <summary>Items ready to be dispatched: Ready state, no unmet dependencies.</summary>
    public IReadOnlyList<WorkItem> Dispatchable()
    {
        lock (_gate)
            return _items.Values
                .Where(i => i.State is WorkItemState.Ready)
                .Where(i => i.DependsOn.All(DependencySatisfiedLocked))
                .OrderBy(i => i.Priority)
                .ThenBy(i => i.CreatedAt)
                .ToList();
    }

    public bool DependencySatisfied(string id)
    {
        lock (_gate) return DependencySatisfiedLocked(id);
    }

    private bool DependencySatisfiedLocked(string id)
    {
        if (!_items.TryGetValue(id, out var dep)) return true;

        return dep.State switch
        {
            WorkItemState.Done or WorkItemState.Verified => true,
            WorkItemState.Superseded => ChildrenSatisfyLocked(id),
            _ => false
        };
    }

    /// <summary>A superseded parent stands in for the children that replaced it, so it settles a
    /// dependency only once every one of them has. A parent superseded by nothing is a hole rather
    /// than a completion, and must not release work that was waiting on it.</summary>
    private bool ChildrenSatisfyLocked(string parentId)
    {
        var children = _items.Values.Where(i => i.ParentId == parentId).ToList();
        return children.Count > 0 && children.All(c => DependencySatisfiedLocked(c.Id));
    }

    public IReadOnlyList<WorkItem> InFlight()
    {
        lock (_gate)
            return _items.Values
                .Where(i => i.State is WorkItemState.InProgress or WorkItemState.InReview)
                .ToList();
    }

    public bool HasOpenWork()
    {
        lock (_gate)
            return _items.Values.Any(i => i.State is not (WorkItemState.Done or WorkItemState.Cancelled or WorkItemState.Superseded));
    }

    public IReadOnlyList<RunRecord> RunsFor(string stationId)
    {
        lock (_gate) return _runs.Where(r => r.StationId == stationId).ToList();
    }

    public IReadOnlyList<WorkItem> Children(string parentId)
    {
        lock (_gate)
            return _items.Values.Where(i => i.ParentId == parentId).OrderBy(i => i.CreatedAt).ToList();
    }

    /// <summary>All descendants of an item, used to tell when a decomposed request is finished.</summary>
    public IReadOnlyList<WorkItem> Descendants(string rootId)
    {
        lock (_gate)
        {
            var result = new List<WorkItem>();
            var frontier = new Queue<string>([rootId]);
            var seen = new HashSet<string> { rootId };

            while (frontier.Count > 0)
            {
                var parent = frontier.Dequeue();
                foreach (var child in _items.Values.Where(i => i.ParentId == parent))
                {
                    if (!seen.Add(child.Id)) continue;
                    result.Add(child);
                    frontier.Enqueue(child.Id);
                }
            }
            return result;
        }
    }
}
