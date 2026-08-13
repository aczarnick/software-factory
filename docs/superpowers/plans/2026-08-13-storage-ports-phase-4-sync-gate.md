# Sync Gating and Migration (Phase 4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make offline operation safe by gating only the irreversible station on a successful sync, surface degraded and foreign-orphan state, and migrate the existing backlog into beads.

**Architecture:** Every station may run against a stale replica because plan, implement, check and verify are local and cheap to redo. `integrate` — the only station that touches the mainline — requires a successful `Sync()` and a confirmation that no other machine won the claim race. Failures become `Blocked`, which `factory activate` already requeues with the worktree preserved.

**Tech Stack:** .NET 10, beads 1.2.1, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-13-storage-adapters-design.md`

**Depends on:** Phases 1, 2 and 3 complete and merged.

## Global Constraints

- .NET 10 SDK pinned to `10.0.400`. Do not change the pin.
- `Factory.Core` must remain dependency-free.
- One top-level type per file. XML docs on public APIs only.
- Retry a failed build once (`csc.dll exited with code 132`).
- Verification gate: `dotnet build` and `dotnet test` green, output shown.
- **Migration is one-way and touches the real backlog.** Run it against a copy first, and never with a factory running.

## Why Only `integrate` Is Gated

Two machines offline can each claim the same item from their own stale view. On sync, `status` is an ordinary issue cell and resolves last-write-wins, so one claim silently loses and the work ran twice.

Gating every station on connectivity would make the factory useless offline for no benefit — the wasted work is tokens, which are recoverable. Gating `integrate` alone means the worst case of a double-claim is duplicated effort, never a double-merge.

---

### Task 1: Sync outcome and degraded state

**Files:**
- Create: `src/Factory.Core/SyncStatus.cs`
- Modify: `src/Factory.Core/IWorkItemStore.cs` — `Sync()` returns `SyncStatus`
- Modify: `src/Factory.Runtime/Providers/LedgerWorkItemStore.cs`, `.../GuardedWorkItemStore.cs`, `.../Beads/BeadsWorkItemStore.cs`
- Test: `tests/Factory.Tests/SyncStatusTests.cs`

**Interfaces:**
- Produces: `SyncStatus(bool Ok, string Detail)` with `SyncStatus.Success` and `SyncStatus.Unavailable(string)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public class SyncStatusTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    [Fact]
    public void A_local_store_always_reports_a_successful_sync()
    {
        using var history = new JsonlRunHistory(Path.Combine(_dir, "ledger.jsonl"));
        var store = new LedgerWorkItemStore(history, history.Replay());

        Assert.True(store.Sync().Ok);
    }

    [Fact]
    public void A_failing_sync_carries_the_reason()
    {
        var status = SyncStatus.Unavailable("remote unreachable");

        Assert.False(status.Ok);
        Assert.Contains("unreachable", status.Detail);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~SyncStatusTests`
Expected: FAIL — `SyncStatus` does not exist.

- [ ] **Step 3: Create `SyncStatus`**

```csharp
namespace Factory.Core;

/// <summary>Outcome of a backlog synchronisation. A failure is not an error: the local
/// replica is complete, so work continues degraded until the remote returns.</summary>
public sealed record SyncStatus(bool Ok, string Detail)
{
    public static readonly SyncStatus Success = new(true, "");
    public static SyncStatus Unavailable(string detail) => new(false, detail);
}
```

- [ ] **Step 4: Change the port and every implementation**

In `IWorkItemStore`:

```csharp
    /// <summary>Reconciles with the shared backlog. Returns a failed status rather than
    /// throwing: being offline is an expected state, not a fault.</summary>
    SyncStatus Sync();
```

`LedgerWorkItemStore`:

```csharp
    /// <summary>Always succeeds: a local ledger has no remote to reconcile with.</summary>
    public SyncStatus Sync() => SyncStatus.Success;
```

`GuardedWorkItemStore`:

```csharp
    public SyncStatus Sync() => Guard(nameof(Sync), inner.Sync);
```

`BeadsWorkItemStore`:

```csharp
    public SyncStatus Sync()
    {
        var result = cli.Exec("sync");
        return result.Ok ? SyncStatus.Success : SyncStatus.Unavailable(result.Combined.Trim());
    }
```

- [ ] **Step 5: Run them to verify they pass**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~SyncStatusTests`
Expected: PASS, 2 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Factory.Core/SyncStatus.cs src/Factory.Core/IWorkItemStore.cs \
        src/Factory.Runtime/Providers/ tests/Factory.Tests/SyncStatusTests.cs
git commit -m "Report backlog synchronisation outcome instead of discarding it"
```

---

### Task 2: The integrate sync gate

**Files:**
- Modify: `src/Factory.Runtime/PipelineStations.cs` — `IntegrateStation`
- Test: `tests/Factory.Tests/IntegrateSyncGateTests.cs`

**Interfaces:**
- Consumes: `SyncStatus` (Task 1), `IWorkItemStore.Get` for the claim re-check.

Locate `IntegrateStation` in `src/Factory.Runtime/PipelineStations.cs` and add the gate at the very top of its execution, before any git work.

- [ ] **Step 1: Write the failing tests**

```csharp
using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public class IntegrateSyncGateTests
{
    private sealed class OfflineStore : IWorkItemStore
    {
        public WorkItem Add(WorkItem item) => item;
        public WorkItem Update(WorkItem item) => item;
        public WorkItem Transition(WorkItem item, WorkItemState to, string? reason) => item with { State = to };
        public WorkItem? Get(string id) => null;
        public IReadOnlyList<WorkItem> All() => [];
        public WorkItem? TryClaim(string owner) => null;
        public void Heartbeat(string id) { }
        public void Release(string id, string reason) { }
        public SyncStatus Sync() => SyncStatus.Unavailable("remote unreachable");
        public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan) => [];
    }

    [Fact]
    public void Integration_is_refused_while_the_backlog_cannot_be_reached()
    {
        var gate = IntegrateStation.CheckSyncGate(
            new OfflineStore(), WorkItem.Create("thing"), owner: "machine-a");

        Assert.False(gate.Passed);
        Assert.Contains("sync-required", gate.Detail);
    }

    [Fact]
    public void Integration_is_refused_when_another_machine_holds_the_claim()
    {
        var item = WorkItem.Create("thing");
        var store = new StubStore(item, assignee: "machine-b");

        var gate = IntegrateStation.CheckSyncGate(store, item, owner: "machine-a");

        Assert.False(gate.Passed);
        Assert.Contains("machine-b", gate.Detail);
    }

    [Fact]
    public void Integration_proceeds_when_synced_and_still_ours()
    {
        var item = WorkItem.Create("thing");
        var store = new StubStore(item, assignee: "machine-a");

        Assert.True(IntegrateStation.CheckSyncGate(store, item, owner: "machine-a").Passed);
    }

    private sealed class StubStore(WorkItem item, string assignee) : IWorkItemStore
    {
        public WorkItem Add(WorkItem i) => i;
        public WorkItem Update(WorkItem i) => i;
        public WorkItem Transition(WorkItem i, WorkItemState to, string? reason) => i with { State = to };
        public WorkItem? Get(string id) => item with { Labels = [$"assignee:{assignee}"] };
        public IReadOnlyList<WorkItem> All() => [item];
        public WorkItem? TryClaim(string owner) => null;
        public void Heartbeat(string id) { }
        public void Release(string id, string reason) { }
        public SyncStatus Sync() => SyncStatus.Success;
        public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan) => [];
    }
}
```

The stub encodes the assignee as a label because `WorkItem` has no assignee field. Decide in Step 3 whether to add one — see the note there.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~IntegrateSyncGateTests`
Expected: FAIL — `CheckSyncGate` does not exist.

- [ ] **Step 3: Add an `Owner` to `WorkItem` and implement the gate**

The claim re-check needs to know who holds the item. Add to `src/Factory.Core/WorkItem.cs`:

```csharp
    /// <summary>Machine currently holding the claim, when in flight. Populated by stores
    /// that support claims; null for purely local backlogs.</summary>
    public string? Owner { get; init; }
```

Map it in `BeadMapper.ToWorkItem` from `bead.Assignee`, and update the test stubs above to set `Owner` rather than a label.

Then add to `IntegrateStation`:

```csharp
    /// <summary>Integration is the only irreversible step, so it is the only one that
    /// requires a reconciled backlog. Everything upstream is local and cheap to redo.</summary>
    public static GateResult CheckSyncGate(IWorkItemStore store, WorkItem item, string owner)
    {
        var sync = store.Sync();
        if (!sync.Ok)
            return GateResult.Fail($"sync-required: backlog unreachable ({sync.Detail})");

        var authoritative = store.Get(item.Id);
        if (authoritative is null)
            return GateResult.Fail($"sync-required: {item.Id} is no longer in the backlog");

        if (authoritative.Owner is { } holder && holder != owner)
            return GateResult.Fail($"claim lost to {holder}; another machine is working this item");

        return GateResult.Pass();
    }
```

Use whatever result type `PipelineStations.cs` already uses for gate outcomes — `GateResult.Fail`/`Pass` above is a placeholder for the existing shape. Read the file and match it.

- [ ] **Step 4: Call the gate and block on failure**

At the top of `IntegrateStation`'s execution, before any git operation:

```csharp
        var gate = CheckSyncGate(s.Items, ctx.Item, s.Config.Name);
        if (!gate.Passed) return StationResult.Blocked(gate.Detail);
```

`Blocked` preserves the worktree and `factory activate` requeues it — the behaviour added in commit `f58f28b`. Match the existing blocked-result shape in that file.

- [ ] **Step 5: Run them to verify they pass**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~IntegrateSyncGateTests`
Expected: PASS, 3 tests.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: all pass. The default `ledger` store always syncs successfully and has no owner, so existing pipeline tests are unaffected.

- [ ] **Step 7: Commit**

```bash
git add src/Factory.Core/WorkItem.cs src/Factory.Runtime/PipelineStations.cs \
        src/Factory.Runtime/Providers/Beads/BeadMapper.cs \
        tests/Factory.Tests/IntegrateSyncGateTests.cs
git commit -m "Require a reconciled backlog before integrating to the mainline"
```

---

### Task 3: Surface degraded and orphaned state

**Files:**
- Modify: `src/Factory.Cli/Commands.cs` — `Status` and `Ls`
- Modify: `src/Factory.Runtime/FactoryHost.cs` — retain the open-time sync status
- Test: `tests/Factory.Tests/StatusReportingTests.cs`

**Interfaces:**
- Produces: `FactoryHost.LastSync` (`SyncStatus`).

- [ ] **Step 1: Retain the sync result at open**

In `FactoryHost`, add:

```csharp
    /// <summary>Result of the synchronisation performed when this host was opened. A failed
    /// sync is not fatal — the local replica is complete — but it means other machines cannot
    /// see this factory's work yet.</summary>
    public SyncStatus LastSync { get; private set; } = SyncStatus.Success;
```

and set it where phase 3 Task 5 Step 6 calls `items.Sync()`:

```csharp
        var sync = items.Sync();
        if (!sync.Ok) log2($"  [backlog] degraded — {sync.Detail}");
```

Assign it to the constructed host before returning.

- [ ] **Step 2: Report it in `factory status`**

In `Commands.Status`, after the existing version and toolchain lines:

```csharp
        if (!host.LastSync.Ok)
            Output.Warn($"backlog degraded — not synchronised with the shared remote: {host.LastSync.Detail}");
```

Use the existing warning helper in `Output`; if there is none, use `Output.Info` with a `Red`/`Yellow` prefix consistent with the file.

- [ ] **Step 3: Report foreign orphans in `factory ls`**

In `Commands.Ls`, when rendering an item that is `InProgress` with an `Owner` that is not this factory's `Config.Name`:

```csharp
        var foreign = item.State == WorkItemState.InProgress &&
                      item.Owner is { } holder && holder != host.Config.Name;
```

Append a marker to that row, e.g. `held by {holder}`. Do **not** requeue it — auto-reaping another machine's work is explicitly deferred (spec Limitations, mitigation 3).

- [ ] **Step 4: Write the test**

```csharp
using Factory.Core;

namespace Factory.Tests;

public class StatusReportingTests
{
    [Fact]
    public void A_foreign_owner_marks_an_item_as_held_elsewhere()
    {
        var item = WorkItem.Create("thing") with
        {
            State = WorkItemState.InProgress,
            Owner = "machine-b"
        };

        Assert.True(Commands.IsHeldElsewhere(item, "machine-a"));
        Assert.False(Commands.IsHeldElsewhere(item with { Owner = "machine-a" }, "machine-a"));
    }
}
```

Extract the predicate from Step 3 into `internal static bool IsHeldElsewhere(WorkItem item, string self)` so it is testable without running the CLI. Add `InternalsVisibleTo` for the test assembly if `Factory.Cli` does not already expose internals — check `Directory.Build.props` first.

- [ ] **Step 5: Run the suite and commit**

Run: `dotnet test`

```bash
git add src/Factory.Runtime/FactoryHost.cs src/Factory.Cli/Commands.cs \
        tests/Factory.Tests/StatusReportingTests.cs
git commit -m "Surface degraded synchronisation and items held by another machine"
```

---

### Task 4: Doctor checks the beads runtime

**Files:**
- Modify: the doctor command (locate via `grep -rn "Doctor" --include="*.cs" src`)
- Test: extend the existing `DoctorCommandTests`

Beads auto-starts a local `dolt sql-server` per project, so the factory now has an implicit background-process dependency. Doctor is where that becomes visible instead of surfacing as a confusing failure.

- [ ] **Step 1: Write the failing test**

Extend `tests/Factory.Tests/DoctorCommandTests.cs` following its existing style:

```csharp
    [Fact]
    public void Reports_the_backlog_provider_and_its_prerequisites()
    {
        var output = RunDoctor();   // use whatever helper the existing tests use

        Assert.Contains("backlog", output, StringComparison.OrdinalIgnoreCase);
    }
```

- [ ] **Step 2: Add the checks**

In the doctor command, add a backlog section reporting:

- which provider is configured (`config.WorkItemStore.Provider`);
- when it is `beads`: whether `bd` is on PATH (`Shell.Which("bd")`), whether `bd info` succeeds, whether `bd dolt status` reports a running server, and whether a remote is configured;
- when it is `ledger`: that the backlog is local-only and not replicated — which is a finding, not a pass, given the whole point of this work.

Report each as a pass/warn line matching the command's existing format. A missing `bd` while `beads` is configured is a **failure**; a missing remote is a **warning**.

- [ ] **Step 3: Run the suite and commit**

Run: `dotnet test`

```bash
git add src/Factory.Cli/ tests/Factory.Tests/DoctorCommandTests.cs
git commit -m "Check the backlog provider and its prerequisites in doctor"
```

---

### Task 5: Migrate the existing backlog

**Files:**
- Create: `src/Factory.Cli/BacklogMigration.cs`
- Modify: `src/Factory.Cli/Commands.cs` and `CommandLine.cs` — add `factory migrate-backlog`
- Test: `tests/Factory.Tests/BacklogMigrationTests.cs`

**Interfaces:**
- Produces: `BacklogMigration.ToJsonl(IEnumerable<WorkItem>)` returning beads-import JSONL, and a `migrate-backlog` command.

Export from the ledger fold, rewrite `wi_` ids to `wi-`, and hand the result to `bd import`, which upserts and accepts explicit ids.

- [ ] **Step 1: Write the failing tests**

```csharp
using Factory.Core;
using Factory.Cli;

namespace Factory.Tests;

public class BacklogMigrationTests
{
    [Fact]
    public void Underscore_identifiers_are_rewritten_to_the_beads_form()
    {
        var item = WorkItem.Create("thing") with { Id = "wi_cba5198e7c96" };

        var jsonl = BacklogMigration.ToJsonl([item]);

        Assert.Contains("wi-cba5198e7c96", jsonl);
        Assert.DoesNotContain("wi_cba5198e7c96", jsonl);
    }

    [Fact]
    public void Dependency_references_are_rewritten_too()
    {
        var blocker = WorkItem.Create("first") with { Id = "wi_aaa111aaa111" };
        var dependent = WorkItem.Create("second") with
        {
            Id = "wi_bbb222bbb222",
            DependsOn = [blocker.Id]
        };

        var jsonl = BacklogMigration.ToJsonl([blocker, dependent]);

        Assert.Contains("wi-aaa111aaa111", jsonl);
        Assert.DoesNotContain("wi_aaa111aaa111", jsonl);
    }

    [Fact]
    public void Terminal_items_are_not_migrated()
    {
        var done = WorkItem.Create("finished") with { State = WorkItemState.Done };
        var cancelled = WorkItem.Create("dropped") with { State = WorkItemState.Cancelled };
        var ready = WorkItem.Create("live") with { State = WorkItemState.Ready };

        var lines = BacklogMigration.ToJsonl([done, cancelled, ready])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(lines);
    }

    [Fact]
    public void Each_line_is_independently_valid_json()
    {
        var jsonl = BacklogMigration.ToJsonl([WorkItem.Create("a"), WorkItem.Create("b")]);

        foreach (var line in jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            System.Text.Json.JsonDocument.Parse(line);
    }
}
```

Migrating terminal items would import closed issues that only add noise; the ledger keeps their history either way.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~BacklogMigrationTests`
Expected: FAIL — `BacklogMigration` does not exist.

- [ ] **Step 3: Implement the migration**

```csharp
using System.Text;
using Factory.Core;
using Factory.Runtime;

namespace Factory.Cli;

/// <summary>One-way export of a ledger-backed backlog into the JSONL shape `bd import`
/// upserts. Terminal work is skipped: the ledger keeps its history and importing closed
/// issues only adds noise to the shared backlog.</summary>
public static class BacklogMigration
{
    public static string ToJsonl(IEnumerable<WorkItem> items)
    {
        var builder = new StringBuilder();

        foreach (var item in items.Where(i => !WorkItemStates.IsTerminal(i.State)))
        {
            builder.Append(FactoryJson.Write(new
            {
                id = Rewrite(item.Id),
                title = item.Title,
                description = item.Intent,
                status = BeadMapper.StatusFor(item.State),
                priority = Math.Clamp(item.Priority, 0, 4),
                issue_type = BeadMapper.TypeFor(item.Kind),
                acceptance_criteria = string.Join("\n",
                    item.AcceptanceCriteria.Select(c => $"- {c.Statement} ({c.Verification.Describe})")),
                metadata = FactoryJson.Read<object>(BeadMapper.MetadataFor(item)),
                dependencies = item.DependsOn.Select(Rewrite).ToArray()
            }));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string Rewrite(string id) => id.Replace('_', '-');
}
```

Confirm the dependency field name `bd import` expects by running `bd export` on a database that has a dependency and matching the emitted schema. `bd import`'s help says it accepts every field `bd export` emits.

- [ ] **Step 4: Add the command**

Wire `factory migrate-backlog` into `Commands.cs` and `CommandLine.cs` following how `cancel` was added (commit `c2d6679`). Behaviour:

1. Refuse to run if any item is `InProgress` — a migration mid-flight would strand a worktree.
2. Write the JSONL to `.factory/backlog-migration.jsonl` and print the path.
3. Print the exact `bd import` command rather than running it, unless `--yes` is passed.

Printing by default keeps a one-way operation under the operator's hand.

- [ ] **Step 5: Run the tests and commit**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~BacklogMigrationTests`
Expected: PASS, 4 tests.

```bash
git add src/Factory.Cli/ tests/Factory.Tests/BacklogMigrationTests.cs
git commit -m "Add a one-way backlog migration into the beads import format"
```

---

### Task 6: Verification gate and cutover

- [ ] **Step 1: Full gate**

Run: `dotnet build && dotnet test`
Expected: build succeeds, all tests pass. Paste the summary line.

- [ ] **Step 2: Dry-run the migration against a copy**

```bash
cp -R .factory /tmp/factory-backup
factory migrate-backlog          # prints the path and the command, does not import
head -2 .factory/backlog-migration.jsonl
```

Expected: one line per non-terminal item, ids in `wi-` form.

- [ ] **Step 3: Import into a scratch beads database and verify the count**

```bash
cd "$(mktemp -d)" && git init -q . && BD_NON_INTERACTIVE=1 bd init --prefix wi
BD_NON_INTERACTIVE=1 bd config set status.custom "draft:frozen,in_review:wip,verified:wip,failed:frozen,cancelled:done"
BD_NON_INTERACTIVE=1 bd config set types.custom "refactor,improvement"
BD_NON_INTERACTIVE=1 bd import /path/to/backlog-migration.jsonl
BD_NON_INTERACTIVE=1 bd ready --json | python3 -c "import sys,json;print(len(json.load(sys.stdin)))"
```

Expected: the ready count matches `factory ls | grep -c ready` from before the migration. A mismatch means the status mapping or the dependency rewrite is wrong — fix it before touching the real backlog.

- [ ] **Step 4: Configure the remote and cut over**

Only after Step 3 matches:

```bash
bd dolt set remote git+ssh://git@github.com/<org>/<repo>.git
```

Then set `"workItemStore": { "provider": "beads" }` in `.factory/factory.json`, run `factory status`, and confirm the backlog is not reported as degraded.

- [ ] **Step 5: Decide on telemetry**

Beads ships with `metrics.disabled = false` and an endpoint at `https://gastownhall-eventsapi.com/mp/collect`. What it transmits was never investigated. Make an explicit decision now that beads holds the backlog:

```bash
bd config set metrics.disabled true
```

Record the decision either way — leaving it at the default by accident is the outcome to avoid.

---

## Self-Review

**Spec coverage:** D7 integrate gate with both conditions — Task 2. `Degraded` in `factory status` — Task 3. Foreign-orphan reporting, limitation mitigation 2 — Task 3. `factory doctor` dolt-server check — Task 4. Migration of the existing items — Task 5. Telemetry decision — Task 6 Step 5.

**Deliberately not built:** auto-reap of foreign orphans (spec Limitations, mitigation 3). Task 3 reports them and stops. The residual gap — a dead machine's item needs an operator to requeue it — is the accepted cost.

**Placeholders that must be resolved during implementation, not left as-is:** the gate result type in Task 2 Step 3 (`GateResult.Pass`/`Fail`) is a stand-in for whatever `PipelineStations.cs` actually uses; the doctor output helper in Task 4 Step 2; and the `bd import` dependency field name in Task 5 Step 3. Each says how to find the real answer.

**Type consistency:** `SyncStatus` is returned by `Sync()` in all three stores and consumed in `IntegrateStation.CheckSyncGate` and `FactoryHost.LastSync`. `WorkItem.Owner` is written by `BeadMapper.ToWorkItem` from `bead.Assignee` and read in both `CheckSyncGate` and `Commands.IsHeldElsewhere`. `BeadMapper.StatusFor` and `TypeFor` are reused by `BacklogMigration` rather than re-implemented.
