# Storage Ports (Phase 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put `IWorkItemStore` and `IRunHistory` between the factory and its storage, with the existing JSONL ledger as the default provider, changing no observable behaviour.

**Architecture:** `Factory.Core` gains the port interfaces and two read-model DTOs; it stays dependency-free and becomes the declared plugin ABI. `Ledger.cs` moves to `Factory.Runtime/Providers/JsonlRunHistory.cs` because it is an adapter, not domain. A `LedgerWorkItemStore` implements the backlog port over the existing `FactoryState` fold, so the ledger remains the backing store for phase 1 and beads can replace it in phase 3 without touching callers.

**Tech Stack:** .NET 10, C# 13, xUnit, `System.Text.Json` with polymorphic type discriminators.

**Spec:** `docs/superpowers/specs/2026-08-13-storage-adapters-design.md`

## Global Constraints

- .NET 10 SDK, pinned to `10.0.400` by `global.json`. Do not change the pin.
- `Factory.Core` must remain **dependency-free**. It is the plugin ABI (spec D5). No project or package references may be added to it.
- One top-level type per file, named after the type. Nested private helpers may share their containing type's file.
- XML doc `<summary>` on **public** APIs only. No explanatory prose or narration on private/internal code.
- Tests assert behaviour, not implementation. Arrange-act-assert.
- **Phase 1 changes no observable behaviour.** Every existing test must pass unmodified except `LedgerTests`, which moves namespace with the type it covers.
- The Roslyn compiler server on this host intermittently dies with `csc.dll exited with code 132`. **Retry a failed build once before believing it.**
- Verification gate: `dotnet build` and `dotnet test` both green, output shown.

## Deviations From The Spec

Two, both deliberate. Raise them if you disagree rather than silently following either.

1. **`InMemoryWorkItemStore` is not built.** The spec lists it for phase 1 to "keep the existing tests offline". The existing tests are already offline — they use a real `Ledger` in a temp directory plus a fake transport. `LedgerWorkItemStore` (built here) serves that need, so a second in-memory implementation is YAGNI until beads becomes the default in phase 3. Build it then, if tests actually need a non-beads option.

2. **`LedgerWorkItemStore` is added, which the spec does not name.** It is required: phase 1 must change no behaviour, and today's behaviour is ledger-backed. Without it there is no phase-1 provider for `IWorkItemStore`.

---

### Task 1: Port interfaces and read-model DTOs

**Files:**
- Create: `src/Factory.Core/IWorkItemStore.cs`
- Create: `src/Factory.Core/IRunHistory.cs`
- Create: `src/Factory.Core/IRunHistorySink.cs`
- Create: `src/Factory.Core/SpendTotals.cs`
- Create: `src/Factory.Core/BudgetRestoreView.cs`
- Test: none — interfaces and records with no behaviour. Task 2 and Task 3 test them through implementations.

**Interfaces:**
- Consumes: `WorkItem`, `WorkItemState`, `FactoryEvent`, `RunRecord`, `TokenUsage` (all existing in `Factory.Core`).
- Produces: `IWorkItemStore`, `IRunHistory`, `IRunHistorySink`, `SpendTotals`, `BudgetRestoreView` — used by every later task.

- [ ] **Step 1: Create `SpendTotals`**

```csharp
namespace Factory.Core;

/// <summary>Aggregate spend across recorded runs, so a provider can answer `factory report`
/// with one query instead of returning every run for the caller to fold.</summary>
public sealed record SpendTotals(int RunCount, decimal TotalUsd, TokenUsage Usage)
{
    public static readonly SpendTotals Empty = new(0, 0m, TokenUsage.Zero);
}
```

- [ ] **Step 2: Create `BudgetRestoreView`**

```csharp
namespace Factory.Core;

/// <summary>The three accumulators <see cref="BudgetGuard.Restore(BudgetRestoreView)"/> needs.
/// Expressed as aggregates rather than raw runs so a database provider can compute them with
/// grouped queries.</summary>
public sealed record BudgetRestoreView(
    IReadOnlyDictionary<string, decimal> PerItemUsd,
    decimal DailyUsd,
    decimal EvolutionDailyUsd)
{
    public static readonly BudgetRestoreView Empty =
        new(new Dictionary<string, decimal>(), 0m, 0m);
}
```

- [ ] **Step 3: Create `IRunHistory`**

```csharp
namespace Factory.Core;

/// <summary>Durable local record of everything the factory did. Always present: this is the
/// copy the prompt promotion gate mines, so it must survive a sink being unreachable.</summary>
public interface IRunHistory : IDisposable
{
    void Append(FactoryEvent evt);

    /// <summary>Events with a sequence strictly greater than <paramref name="afterSeq"/>,
    /// in order. Pass 0 for the whole history.</summary>
    IEnumerable<FactoryEvent> ReadFrom(long afterSeq);

    IReadOnlyList<RunRecord> RunsForItem(string itemId);
    IReadOnlyList<RunRecord> RunsForStation(string stationId);
    SpendTotals Totals();
    BudgetRestoreView ForBudget();

    /// <summary>Current champion prompt version per station.</summary>
    IReadOnlyDictionary<string, string> Champions();
}
```

- [ ] **Step 4: Create `IRunHistorySink`**

```csharp
namespace Factory.Core;

/// <summary>An additional, best-effort destination for events — a tracing backend or
/// evaluator. Deliberately write-only: a sink receives traces, it never answers the
/// factory's queries, so an unreachable sink can never block a read.</summary>
public interface IRunHistorySink
{
    void Emit(FactoryEvent evt);
    void Flush();
}
```

- [ ] **Step 5: Create `IWorkItemStore`**

```csharp
namespace Factory.Core;

/// <summary>The backlog. Exactly one provider is active: item state has a single authority,
/// and two stores would mean two truths.</summary>
public interface IWorkItemStore
{
    WorkItem Add(WorkItem item);
    WorkItem Update(WorkItem item);
    WorkItem Transition(WorkItem item, WorkItemState to, string? reason);

    WorkItem? Get(string id);
    IReadOnlyList<WorkItem> All();

    /// <summary>Atomically takes the highest-priority dispatchable item and marks it
    /// in progress. Returns null when nothing is ready.</summary>
    WorkItem? TryClaim(string owner);

    void Heartbeat(string id);
    void Release(string id, string reason);

    void Sync();
    IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan);
}
```

- [ ] **Step 6: Build**

Run: `dotnet build src/Factory.Core/Factory.Core.csproj`
Expected: succeeds. Retry once on `csc.dll exited with code 132`.

- [ ] **Step 7: Commit**

```bash
git add src/Factory.Core/IWorkItemStore.cs src/Factory.Core/IRunHistory.cs \
        src/Factory.Core/IRunHistorySink.cs src/Factory.Core/SpendTotals.cs \
        src/Factory.Core/BudgetRestoreView.cs
git commit -m "Add storage port interfaces and read-model DTOs to Factory.Core"
```

---

### Task 2: `JsonlRunHistory` provider

Moves `Ledger` into `Factory.Runtime/Providers` and implements the query surface. The append/replay behaviour is unchanged — only the type's home and its extra read methods are new.

**Files:**
- Create: `src/Factory.Runtime/Providers/JsonlRunHistory.cs`
- Delete: `src/Factory.Core/Ledger.cs`
- Create: `tests/Factory.Tests/JsonlRunHistoryTests.cs`
- Modify: `tests/Factory.Tests/CoreTests.cs:1-75` — remove the `LedgerTests` class (it moves to the new test file)

**Interfaces:**
- Consumes: `IRunHistory`, `SpendTotals`, `BudgetRestoreView` (Task 1).
- Produces: `JsonlRunHistory(string path, TimeProvider? clock = null)` with `string Path { get; }` and `FactoryState Replay()`.

- [ ] **Step 1: Write the failing test for the query surface**

Create `tests/Factory.Tests/JsonlRunHistoryTests.cs`:

```csharp
using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public class JsonlRunHistoryTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private JsonlRunHistory Open(TimeProvider? clock = null) =>
        new(Path.Combine(_dir, "ledger.jsonl"), clock);

    private static RunRecord Run(string itemId, string stationId, decimal cost) => new()
    {
        RunId = Ids.New("run"),
        ItemId = itemId,
        StationId = stationId,
        CostUsd = cost,
        Usage = new TokenUsage(InputTokens: 10, OutputTokens: 5)
    };

    [Fact]
    public void RunsForItem_returns_only_that_items_runs()
    {
        using var history = Open();
        history.Append(new RunCompleted(Run("wi-a", "implement", 0.10m)));
        history.Append(new RunCompleted(Run("wi-b", "implement", 0.20m)));

        var runs = history.RunsForItem("wi-a");

        Assert.Single(runs);
        Assert.Equal(0.10m, runs[0].CostUsd);
    }

    [Fact]
    public void RunsForStation_returns_only_that_stations_runs()
    {
        using var history = Open();
        history.Append(new RunCompleted(Run("wi-a", "implement", 0.10m)));
        history.Append(new RunCompleted(Run("wi-a", "review", 0.20m)));

        var runs = history.RunsForStation("review");

        Assert.Single(runs);
        Assert.Equal(0.20m, runs[0].CostUsd);
    }

    [Fact]
    public void Totals_aggregates_count_cost_and_usage()
    {
        using var history = Open();
        history.Append(new RunCompleted(Run("wi-a", "implement", 0.10m)));
        history.Append(new RunCompleted(Run("wi-b", "review", 0.25m)));

        var totals = history.Totals();

        Assert.Equal(2, totals.RunCount);
        Assert.Equal(0.35m, totals.TotalUsd);
        Assert.Equal(20, totals.Usage.InputTokens);
    }

    [Fact]
    public void Champions_reflects_the_latest_promotion_per_station()
    {
        using var history = Open();
        history.Append(new PromptPromoted("implement", "v1", "v2", 0.1, "better"));
        history.Append(new PromptPromoted("implement", "v2", "v3", 0.2, "better still"));
        history.Append(new PromptPromoted("review", "v1", "v4", 0.1, "better"));

        var champions = history.Champions();

        Assert.Equal("v3", champions["implement"]);
        Assert.Equal("v4", champions["review"]);
    }

    [Fact]
    public void ForBudget_sums_per_item_always_and_daily_only_for_today()
    {
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        using var history = Open(clock);

        history.Append(new RunCompleted(Run("wi-a", "implement", 0.10m) with { At = now }));
        history.Append(new RunCompleted(Run("wi-a", "review", 0.20m) with { At = now.AddDays(-3) }));

        var view = history.ForBudget();

        Assert.Equal(0.30m, view.PerItemUsd["wi-a"]);
        Assert.Equal(0.10m, view.DailyUsd);
    }

    [Fact]
    public void ForBudget_attributes_evolution_spend_from_item_provenance()
    {
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        using var history = Open(clock);

        var item = WorkItem.Create("self-improvement") with
        {
            Provenance = Provenance.FromEvolution("optimiser")
        };
        history.Append(new WorkItemFiled(item));
        history.Append(new RunCompleted(Run(item.Id, "implement", 0.40m) with { At = now }));

        var view = history.ForBudget();

        Assert.Equal(0.40m, view.EvolutionDailyUsd);
    }
}
```

- [ ] **Step 2: Add the `FakeTimeProvider` test helper**

The tests above need a deterministic clock. Add it as a nested type in the same file, after the test class (a private helper may share its consumer's file):

```csharp
file sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~JsonlRunHistoryTests`
Expected: FAIL — `JsonlRunHistory` does not exist.

- [ ] **Step 4: Move `Ledger.cs` to the provider, preserving history**

```bash
git mv src/Factory.Core/Ledger.cs src/Factory.Runtime/Providers/JsonlRunHistory.cs
```

`git mv` rather than delete-and-create so the file's history follows it.

- [ ] **Step 5: Rewrite the moved file as the provider**

Replace the entire contents of `src/Factory.Runtime/Providers/JsonlRunHistory.cs`:

```csharp
using System.Text;
using Factory.Core;

namespace Factory.Runtime;

/// <summary>
/// Append-only JSONL event log. Current state is a fold over these events, so the factory
/// is crash-resumable and fully auditable: every item, every model call, every gate verdict,
/// and every prompt promotion is recorded in order.
/// </summary>
public sealed class JsonlRunHistory : IRunHistory
{
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private FileStream? _out;
    private long _seq;

    public string Path => _path;

    public JsonlRunHistory(string path, TimeProvider? clock = null)
    {
        _path = path;
        _clock = clock ?? TimeProvider.System;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _seq = ReadFrom(0).Select(e => e.Seq).DefaultIfEmpty(0).Max();
    }

    public void Append(FactoryEvent evt)
    {
        lock (_gate)
        {
            evt.Seq = ++_seq;
            var line = FactoryJson.Write<FactoryEvent>(evt) + "\n";
            _out ??= new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = Encoding.UTF8.GetBytes(line);
            _out.Write(bytes, 0, bytes.Length);
            _out.Flush(true);
        }
    }

    /// <summary>Reads every event after the given sequence. A torn final line (process killed
    /// mid-write) is skipped rather than treated as corruption.</summary>
    public IEnumerable<FactoryEvent> ReadFrom(long afterSeq)
    {
        if (!File.Exists(_path)) yield break;

        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            FactoryEvent? evt;
            try
            {
                evt = FactoryJson.Read<FactoryEvent>(line);
            }
            catch (System.Text.Json.JsonException)
            {
                // Torn or unreadable line: skip it and keep the rest of the history.
                continue;
            }

            if (evt is not null && evt.Seq > afterSeq) yield return evt;
        }
    }

    public IReadOnlyList<RunRecord> RunsForItem(string itemId) =>
        [.. Runs().Where(r => r.ItemId == itemId)];

    public IReadOnlyList<RunRecord> RunsForStation(string stationId) =>
        [.. Runs().Where(r => r.StationId == stationId)];

    public SpendTotals Totals()
    {
        var runs = Runs().ToList();
        return runs.Count == 0
            ? SpendTotals.Empty
            : new SpendTotals(
                runs.Count,
                runs.Sum(r => r.CostUsd),
                runs.Aggregate(TokenUsage.Zero, (a, r) => a + r.Usage));
    }

    public BudgetRestoreView ForBudget()
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var state = Replay();

        var perItem = new Dictionary<string, decimal>();
        decimal daily = 0m, evolutionDaily = 0m;

        foreach (var run in state.Runs)
        {
            perItem[run.ItemId] = perItem.GetValueOrDefault(run.ItemId) + run.CostUsd;
            if (DateOnly.FromDateTime(run.At.UtcDateTime) != today) continue;

            daily += run.CostUsd;
            if (state.Items.TryGetValue(run.ItemId, out var item) &&
                item.Provenance.Kind == ProvenanceKind.Evolution)
                evolutionDaily += run.CostUsd;
        }

        return new BudgetRestoreView(perItem, daily, evolutionDaily);
    }

    public IReadOnlyDictionary<string, string> Champions() => Replay().Champions;

    public FactoryState Replay() => FactoryState.Replay(ReadFrom(0));

    private IEnumerable<RunRecord> Runs() =>
        ReadFrom(0).OfType<RunCompleted>().Select(e => e.Record);

    public void Dispose()
    {
        lock (_gate)
        {
            _out?.Dispose();
            _out = null;
        }
    }
}
```

Note the two intentional signature changes: `Append` no longer returns the event (no caller used the return value except one test, updated in Step 7), and `ReadAll()` becomes `ReadFrom(0)`.

- [ ] **Step 6: Change `FactoryState.Replay` to accept the streaming signature**

`FactoryState.Replay(IEnumerable<FactoryEvent>)` already takes `IEnumerable`, so no change is needed. Verify by inspection at `src/Factory.Core/FactoryState.cs:42`.

- [ ] **Step 7: Move `LedgerTests` into the new test file**

Cut the entire `LedgerTests` class from `tests/Factory.Tests/CoreTests.cs` (lines 1-75, through the end of `Round_trips_polymorphic_verifications`) and paste it into `JsonlRunHistoryTests.cs`, renaming the type and applying three mechanical edits:

- `new Ledger(path)` becomes `new JsonlRunHistory(path)`
- `reopened.ReadAll()` becomes `reopened.ReadFrom(0).ToList()`
- `Continues_sequence_numbers_across_reopen` no longer asserts on `Append`'s return value; assert on the reread instead:

```csharp
[Fact]
public void Continues_sequence_numbers_across_reopen()
{
    var path = Path.Combine(_dir, "ledger.jsonl");
    using (var first = new JsonlRunHistory(path)) first.Append(new FactoryNote("one"));
    using (var second = new JsonlRunHistory(path)) second.Append(new FactoryNote("two"));

    using var reopened = new JsonlRunHistory(path);
    Assert.Equal([1, 2], reopened.ReadFrom(0).Select(e => e.Seq));
}
```

Keep `using Factory.Core;` in `CoreTests.cs` — the remaining tests in that file still need it.

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~JsonlRunHistoryTests`
Expected: PASS, 10 tests (6 new + 4 moved).

- [ ] **Step 9: Commit**

```bash
git add src/Factory.Core/Ledger.cs src/Factory.Runtime/Providers/JsonlRunHistory.cs \
        tests/Factory.Tests/JsonlRunHistoryTests.cs tests/Factory.Tests/CoreTests.cs
git commit -m "Move the JSONL ledger into a run-history provider and add its query surface"
```

---

### Task 3: `LedgerWorkItemStore` provider

**Files:**
- Create: `src/Factory.Runtime/Providers/LedgerWorkItemStore.cs`
- Create: `tests/Factory.Tests/LedgerWorkItemStoreTests.cs`

**Interfaces:**
- Consumes: `IWorkItemStore` (Task 1), `IRunHistory` (Task 1), `JsonlRunHistory` (Task 2), existing `FactoryState`.
- Produces: `LedgerWorkItemStore(IRunHistory history, FactoryState state)`.

The store owns the write path that `FactoryHost.Submit`/`Update`/`Transition` performs today: append the event, then fold it into live state. It deliberately does not own dependency *policy* — `FactoryState.Dispatchable()` already encodes that and stays where it is.

- [ ] **Step 1: Write the failing tests**

Create `tests/Factory.Tests/LedgerWorkItemStoreTests.cs`:

```csharp
using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public class LedgerWorkItemStoreTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private (LedgerWorkItemStore Store, JsonlRunHistory History) Open()
    {
        var history = new JsonlRunHistory(Path.Combine(_dir, "ledger.jsonl"));
        return (new LedgerWorkItemStore(history, history.Replay()), history);
    }

    [Fact]
    public void Add_makes_the_item_readable()
    {
        var (store, history) = Open();
        using var _ = history;

        var added = store.Add(WorkItem.Create("build a thing") with { State = WorkItemState.Ready });

        Assert.Equal("build a thing", store.Get(added.Id)!.Title);
        Assert.Single(store.All());
    }

    [Fact]
    public void Transition_records_the_new_state()
    {
        var (store, history) = Open();
        using var _ = history;

        var item = store.Add(WorkItem.Create("thing") with { State = WorkItemState.Ready });
        store.Transition(item, WorkItemState.InProgress, "dispatched");

        Assert.Equal(WorkItemState.InProgress, store.Get(item.Id)!.State);
    }

    [Fact]
    public void Transition_rejects_an_illegal_move()
    {
        var (store, history) = Open();
        using var _ = history;

        var item = store.Add(WorkItem.Create("thing") with { State = WorkItemState.Ready });

        Assert.Throws<InvalidOperationException>(
            () => store.Transition(item, WorkItemState.Done, "skipping the pipeline"));
    }

    [Fact]
    public void TryClaim_takes_the_highest_priority_ready_item_and_marks_it_in_progress()
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
    public void TryClaim_withholds_an_item_whose_dependency_is_unmet()
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
    public void TryClaim_returns_null_when_nothing_is_ready()
    {
        var (store, history) = Open();
        using var _ = history;

        store.Add(WorkItem.Create("proposal"));   // Draft, not Ready

        Assert.Null(store.TryClaim("machine-a"));
    }

    [Fact]
    public void Release_returns_a_claimed_item_to_the_queue()
    {
        var (store, history) = Open();
        using var _ = history;

        store.Add(WorkItem.Create("thing") with { State = WorkItemState.Ready });
        var claimed = store.TryClaim("machine-a")!;

        store.Release(claimed.Id, "requeued after restart");

        Assert.Equal(WorkItemState.Ready, store.Get(claimed.Id)!.State);
    }

    [Fact]
    public void Items_survive_a_reopen()
    {
        var (store, history) = Open();
        var added = store.Add(WorkItem.Create("durable") with { State = WorkItemState.Ready });
        history.Dispose();

        var (reopened, reopenedHistory) = Open();
        using var _ = reopenedHistory;

        Assert.Equal("durable", reopened.Get(added.Id)!.Title);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~LedgerWorkItemStoreTests`
Expected: FAIL — `LedgerWorkItemStore` does not exist.

- [ ] **Step 3: Implement the store**

Create `src/Factory.Runtime/Providers/LedgerWorkItemStore.cs`:

```csharp
using Factory.Core;

namespace Factory.Runtime;

/// <summary>
/// Backlog stored in the factory's own event ledger. This is the default provider and
/// preserves the behaviour the factory had before the backlog was a port: every write is an
/// event, and current state is the fold over those events.
/// </summary>
public sealed class LedgerWorkItemStore(IRunHistory history, FactoryState state) : IWorkItemStore
{
    private readonly Lock _gate = new();

    public WorkItem Add(WorkItem item)
    {
        Record(new WorkItemFiled(item));
        return item;
    }

    public WorkItem Update(WorkItem item)
    {
        var updated = item with { UpdatedAt = DateTimeOffset.UtcNow };
        Record(new WorkItemUpdated(updated));
        return updated;
    }

    public WorkItem Transition(WorkItem item, WorkItemState to, string? reason)
    {
        if (!WorkItemStates.CanTransition(item.State, to))
            throw new InvalidOperationException(
                $"Illegal transition {item.State} -> {to} for {item.Id}.");

        Record(new WorkItemStateChanged(item.Id, item.State, to, reason));
        return item with { State = to, UpdatedAt = DateTimeOffset.UtcNow };
    }

    public WorkItem? Get(string id) => state.Items.GetValueOrDefault(id);

    public IReadOnlyList<WorkItem> All() => [.. state.Items.Values];

    public WorkItem? TryClaim(string owner)
    {
        lock (_gate)
        {
            if (state.Dispatchable().FirstOrDefault() is not { } next) return null;
            return Transition(next, WorkItemState.InProgress, $"claimed by {owner}");
        }
    }

    /// <summary>No-op: a ledger-backed backlog has no lease to refresh. Beads does, and
    /// implements this in phase 3.</summary>
    public void Heartbeat(string id) { }

    public void Release(string id, string reason)
    {
        if (Get(id) is not { } item) return;
        Transition(item, WorkItemState.Ready, reason);
    }

    /// <summary>No-op: a local ledger has no remote to reconcile with.</summary>
    public void Sync() { }

    /// <summary>Nothing to reclaim: without leases there is no staleness to detect. The
    /// orchestrator's own restart requeue already covers in-process crashes.</summary>
    public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan) => [];

    private void Record(FactoryEvent evt)
    {
        history.Append(evt);
        state.Apply(evt);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~LedgerWorkItemStoreTests`
Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Factory.Runtime/Providers/LedgerWorkItemStore.cs \
        tests/Factory.Tests/LedgerWorkItemStoreTests.cs
git commit -m "Add a ledger-backed work item store behind the backlog port"
```

---

### Task 4: Route `FactoryServices` and `FactoryHost` through the ports

**Files:**
- Modify: `src/Factory.Runtime/Station.cs:8-35` — `FactoryServices.Ledger` becomes `History`, add `Items`
- Modify: `src/Factory.Runtime/FactoryHost.cs:13,20,85,96,110,131-175,203`
- Modify: `src/Factory.Core/Budget.cs:114-134` — `Restore` takes a `BudgetRestoreView`
- Modify: `src/Factory.Runtime/EvolutionService.cs:38,105`
- Modify: `src/Factory.Cli/Commands.cs:427,553,564`

**Interfaces:**
- Consumes: everything from Tasks 1-3.
- Produces: `FactoryServices.History` (`IRunHistory`), `FactoryServices.Items` (`IWorkItemStore`), `BudgetGuard.Restore(BudgetRestoreView)`.

- [ ] **Step 1: Write the failing test for budget restore**

Add to `tests/Factory.Tests/CoreTests.cs`:

```csharp
public class BudgetRestoreTests
{
    [Fact]
    public void Restore_rehydrates_per_item_and_daily_spend_from_a_view()
    {
        var guard = new BudgetGuard(new BudgetSpec { DailyUsd = 10m, PerItemUsd = 5m });

        guard.Restore(new BudgetRestoreView(
            new Dictionary<string, decimal> { ["wi-a"] = 2.50m },
            DailyUsd: 4m,
            EvolutionDailyUsd: 1m));

        Assert.Equal(2.50m, guard.SpentOn("wi-a"));
        Assert.Equal(4m, guard.DailySpent);
        Assert.Equal(1m, guard.EvolutionSpent);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~BudgetRestoreTests`
Expected: FAIL — no `Restore` overload takes `BudgetRestoreView`.

- [ ] **Step 3: Replace `BudgetGuard.Restore`**

In `src/Factory.Core/Budget.cs`, replace the existing `Restore` method (lines 113-134) entirely:

```csharp
    /// <summary>Rehydrate accumulators from recorded history so restarts do not reset spend.</summary>
    public void Restore(BudgetRestoreView view)
    {
        lock (_gate)
        {
            _day = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
            _daily = view.DailyUsd;
            _evolutionDaily = view.EvolutionDailyUsd;
            _perItem.Clear();
            foreach (var (itemId, usd) in view.PerItemUsd) _perItem[itemId] = usd;
        }
    }
```

The day-bucketing that used to live here now lives in `JsonlRunHistory.ForBudget()`, which is why both take the same `TimeProvider`.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~BudgetRestoreTests`
Expected: PASS.

- [ ] **Step 5: Rewire `FactoryServices`**

In `src/Factory.Runtime/Station.cs`, replace the `Ledger` property and the `Record` method:

```csharp
    public required IRunHistory History { get; init; }
    public required IWorkItemStore Items { get; init; }
```

```csharp
    /// <summary>Appends to run history and folds the event into live state in one step, so
    /// the in-memory view never drifts from the durable log.</summary>
    public T Record<T>(T evt) where T : FactoryEvent
    {
        History.Append(evt);
        State.Apply(evt);
        return evt;
    }
```

- [ ] **Step 6: Rewire `FactoryHost`**

In `src/Factory.Runtime/FactoryHost.cs`:

Replace the field and constructor (lines 13, 20-25):

```csharp
    private readonly IRunHistory _history;
```
```csharp
    private FactoryHost(FactoryServices services, IRunHistory history, FactoryPaths paths)
    {
        Services = services;
        _history = history;
        Paths = paths;
    }
```

Replace lines 85-86:

```csharp
        var history = new JsonlRunHistory(paths.LedgerFile);
        var state = history.Replay();
        var items = new LedgerWorkItemStore(history, state);
```

Replace line 96:

```csharp
        budget.Restore(history.ForBudget());
```

Replace line 110 in the `FactoryServices` initialiser:

```csharp
            History = history,
            Items = items,
```

Replace the body of `Submit` (lines 131-139) so writes go through the store:

```csharp
    public WorkItem Submit(WorkItem item, bool activate = true)
    {
        var filed = activate && item.State == WorkItemState.Draft
            ? item with { State = WorkItemState.Ready, UpdatedAt = DateTimeOffset.UtcNow }
            : item;

        return Services.Items.Add(filed);
    }
```

Replace `Transition` and `Update` (lines 159-175) so they delegate:

```csharp
    public WorkItem Transition(WorkItem item, WorkItemState to, string? reason = null) =>
        Services.Items.Transition(item, to, reason);

    public WorkItem Update(WorkItem item) => Services.Items.Update(item);
```

Replace line 203:

```csharp
    public void Dispose() => _history.Dispose();
```

- [ ] **Step 7: Update `EvolutionService`**

In `src/Factory.Runtime/EvolutionService.cs`, replace line 38:

```csharp
        var runs = _s.State.Runs;
```

with nothing — delete it, and change the `RunStationAsync` call (line 55-56) to fetch per station:

```csharp
            var outcome = await loop.RunStationAsync(
                station.Id, tier, _s.History.RunsForStation(station.Id), stationTraces,
                perStationBudget, settings, ct).ConfigureAwait(false);
```

This is behaviour-preserving: `RunStationAsync` immediately calls `Evaluator.ByVersion(runs, stationId)` (`src/Factory.Evolution/EvolutionLoop.cs:71`), which filters by the same station id.

Replace line 105:

```csharp
        foreach (var evt in _s.History.ReadFrom(0))
```

- [ ] **Step 8: Update `Commands.cs`**

Replace line 427:

```csharp
        var runs = host.Services.History.RunsForItem(item.Id);
```

Replace line 553:

```csharp
        var runs = host.Services.History.Totals().RunCount;
```

Replace line 564:

```csharp
        var runs = host.Services.History.ReadFrom(0).OfType<RunCompleted>().Select(e => e.Record).ToList();
```

Line 564's `runs` is grouped and folded immediately afterwards, so a list is what the following code already expects. Leave `Commands.cs:78`, `:301`, `:361`, `:391`, and `:607` alone — they read `State.Items`, which is still correct.

- [ ] **Step 9: Build and run the full suite**

Run: `dotnet build && dotnet test`
Expected: build succeeds; **all tests pass**. Retry once on `csc.dll exited with code 132`.

If any test outside `JsonlRunHistoryTests` / `LedgerWorkItemStoreTests` / `BudgetRestoreTests` fails, phase 1's no-behaviour-change contract is broken. Fix the production code, not the test.

- [ ] **Step 10: Commit**

```bash
git add src/Factory.Runtime/Station.cs src/Factory.Runtime/FactoryHost.cs \
        src/Factory.Core/Budget.cs src/Factory.Runtime/EvolutionService.cs \
        src/Factory.Cli/Commands.cs tests/Factory.Tests/CoreTests.cs
git commit -m "Route the factory host and its readers through the storage ports"
```

---

### Task 5: Orchestrator claims through the port

**Files:**
- Modify: `src/Factory.Runtime/Orchestrator.cs:112-125`
- Test: `tests/Factory.Tests/RuntimeTests.cs` — existing pipeline tests cover this; no new test file

**Interfaces:**
- Consumes: `IWorkItemStore.TryClaim` (Task 1, implemented in Task 3).
- Produces: no new public surface.

- [ ] **Step 1: Replace the dispatch batch with a claim loop**

In `src/Factory.Runtime/Orchestrator.cs`, replace lines 112-125:

```csharp
            var claimable = throttled ? 0 : concurrency - running.Count;
            for (var i = 0; i < claimable && started < opts.MaxItems; i++)
            {
                if (_s.Items.TryClaim(_s.Config.Name) is not { } claimed) break;

                claimed = _s.Items.Update(claimed with
                {
                    Station = claimed.Station ?? _s.Blueprint.Pipeline.FirstOrDefault()
                });
                started++;
                running.Add(ProcessItemAsync(claimed, opts, ct));
            }
```

`TryClaim` performs the `Ready -> InProgress` transition that `host.Transition(item, WorkItemState.InProgress, "dispatched")` did, so the explicit transition is gone. The claim is still made before dispatch, so the next poll cannot pick the item up again.

- [ ] **Step 2: Run the full suite**

Run: `dotnet test`
Expected: PASS. `RuntimeTests` exercises the whole pipeline against the fake transport, including `Assert.All(host.Services.State.Items.Values, i => Assert.Equal(WorkItemState.Done, i.State))` at `RuntimeTests.cs:160` — that assertion passing is the proof that claiming still dispatches every item.

- [ ] **Step 3: Commit**

```bash
git add src/Factory.Runtime/Orchestrator.cs
git commit -m "Claim work through the backlog port instead of reading a dispatch batch"
```

---

### Task 6: Verification gate

**Files:** none modified.

- [ ] **Step 1: Confirm `Factory.Core` gained no dependencies**

Run: `grep -c "ProjectReference\|PackageReference" src/Factory.Core/Factory.Core.csproj`
Expected: `0`. A non-zero count breaks the plugin ABI constraint (spec D5).

- [ ] **Step 2: Confirm no caller still references the old type**

Run: `grep -rn "\bLedger\b" --include="*.cs" src tests | grep -v "/obj/\|/bin/" | grep -v "LedgerFile\|LedgerWorkItemStore"`
Expected: no output.

- [ ] **Step 3: Run the full gate and show the output**

Run: `dotnet build && dotnet test`
Expected: build succeeds, all tests pass. Paste the actual summary line into the completion report — a claim of success without it does not count.

- [ ] **Step 4: Commit any fixes and push the branch**

```bash
git push -u origin storage-adapters
```

---

## Self-Review

**Spec coverage (phase 1 scope only):** `IWorkItemStore`, `IRunHistory`, `IRunHistorySink` — Task 1. `Ledger.cs` moves to `Providers/JsonlRunHistory` — Task 2. Call-site-shaped query surface (D4) — Task 2, all five methods traced to their call sites. `FactoryServices` routed through ports — Task 4. `Dispatchable()` delegating to `TryClaim` — Task 5. `Factory.Core` stays dependency-free — Task 6 Step 1.

**Deferred to later phases, correctly out of scope here:** `FactoryProviderAttribute`, `PluginCatalog`, `PluginLoadContext`, `ProviderRegistry`, the guard decorators, and config binding are all phase 2. `BeadsWorkItemStore`, the mapping, priority narrowing (D6), and reconcile-on-open are phase 3. The `integrate` sync gate (D7) and migration are phase 4.

**Known gap carried forward:** `IRunHistorySink` is defined in Task 1 but nothing consumes it until phase 2. It is defined here deliberately so the plugin ABI is established in one contract version rather than split across two.

**Type consistency:** `IRunHistory.ReadFrom(long afterSeq)` is used with argument `0` in `JsonlRunHistory.Replay`, `EvolutionService:105`, and `Commands.cs:564`. `SpendTotals.RunCount` is used at `Commands.cs:553`. `BudgetRestoreView`'s three members match `BudgetGuard.Restore`'s three assignments. `LedgerWorkItemStore` takes `(IRunHistory, FactoryState)` in both its test helper and `FactoryHost.Open`.
