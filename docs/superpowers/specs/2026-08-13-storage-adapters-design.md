# Pluggable Storage Adapters: Backlog and Run History

**Date:** 2026-08-13
**Status:** Approved design, pending implementation plan

## Problem

`.factory/` is gitignored (`.gitignore:4`). It holds the entire backlog — 23 ready items,
every dependency edge, every acceptance criterion — and nothing replicates it. A dead
machine, a fresh clone, or a stray `rm -rf` loses work that only exists because the
decompose station once ran. `HANDOFF.md` documents this with a comment ("must not be
deleted") where a mechanism belongs.

A second problem sits behind the first. The ledger conflates two things with opposite
storage requirements:

| | Backlog spec | Run history |
|---|---|---|
| Content | title, intent, deps, criteria, priority, provenance, state | model calls, cost, tokens, gate verdicts, prompt promotions |
| Size | ~30 items | 1.5 MB, append-heavy |
| Churn | slow | every turn |
| Loss cost | catastrophic — unrecoverable intent | painful — the promotion gate loses its evidence base |
| Wants | replicated, conflict-aware, shared | local, append-only, compactable |

One file cannot serve both, which is why neither is stored well today: too churny to
commit, too valuable to lose.

## Goals

1. The backlog survives loss of any single machine.
2. Multiple machines, each with its own checkout, share one backlog without double-executing work.
3. Both stores are pluggable, so a third party can supply a different backing store without forking the factory.
4. Run history can be teed to a high-value evaluator without risking the local durable copy.
5. The 114 existing tests keep running offline with no external dependency.

## Non-goals

- Multiple factory processes against a single checkout. The toolchain-contention constraint in `HANDOFF.md` (concurrency 1 per checkout) stands.
- Auto-reaping work orphaned by a *different* machine. Deferred deliberately; see Limitations.
- Replacing the ledger's event-sourced model. It remains the local audit log.

## Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | Beads is authoritative for item state; the ledger keeps an audit copy | Single source of truth for the backlog, while local replay still works for forensics |
| D2 | Write order is beads first, ledger second | A failed beads write aborts the transition; a failed ledger write is tolerable and self-heals at reconcile |
| D3 | Separate checkouts share one backlog | Matches how the work actually runs; preserves the concurrency-1 toolchain constraint |
| D4 | Run-history port exposes queries shaped by real call sites | A port that only offers `ReadAll()` makes a database impl pointless |
| D5 | Plugins are in-process .NET assemblies loaded from `.factory/plugins/` | Chosen over stdio subprocesses and DI-only; full type fidelity, no serialization boundary |
| D6 | `WorkItem.Priority` narrows from open int to 0–4 | `bd ready --claim` dispatches in beads' order, so a lossy bucket would silently reorder work |
| D7 | `integrate` requires a successful sync; every other station may run degraded | Confines the offline double-claim hazard to wasted tokens, never a double-merge |

## Architecture

`Factory.Core` becomes the declared ABI. It is already dependency-free and is the policy
layer, so the ports belong there under the dependency rule.

```
Factory.Core                        contract assembly, versioned, no dependencies
  IWorkItemStore                    one-of, authoritative
  IRunHistory / IRunHistorySink     one writer + N sinks
  FactoryProviderAttribute          discovery marker + contract version
  WorkItem, FactoryEvent, RunRecord unchanged

Factory.Runtime                     details
  PluginCatalog                     scan, load, validate, register
  PluginLoadContext                 AssemblyLoadContext + dependency resolver
  ProviderRegistry                  name -> provider; built-ins pre-seeded
  Providers/JsonlRunHistory         Ledger.cs moves here; it is an adapter
  Providers/BeadsWorkItemStore      shells `bd --json` via Shell.cs
  Providers/InMemoryWorkItemStore   keeps the existing tests offline
```

`Ledger.cs` leaving `Factory.Core` is the only structural churn. It is file I/O sitting in
the domain; `LedgerTests` (`tests/Factory.Tests/CoreTests.cs:5`) moves with it.

### Why the two ports differ

`IWorkItemStore` admits **exactly one** provider. Item state has a single authority by
definition; two stores would mean two truths.

`IRunHistory` is **one durable writer plus N optional sinks**. "Local or plugin" is worse
than "local always, plugin additionally": a network blip must not drop the run records the
Wilson-bound promotion gate mines. The failure stories differ accordingly — a broken
work-item plugin halts the factory; a broken sink degrades to a warning.

### Existing seam

`FactoryServices.Record()` (`src/Factory.Runtime/Station.cs:29`) is already the single write
path, and `Services.State` the single read path. Both ports slot in behind those two
members, so most of the ~20 call sites in `Commands.cs`, `Orchestrator.cs`, and
`EvolutionService.cs` do not change.

`FactoryState` keeps folding item events from the ledger. It stops being authoritative, and
exactly one method moves:

| Member | Before | After |
|---|---|---|
| `State.Items` | fold of ledger | fold of ledger, corrected by reconcile-on-open |
| `State.Runs`, `Champions` | fold of ledger | unchanged — ledger is still truth |
| `State.Dispatchable()` | local dependency-graph query | delegates to `IWorkItemStore.TryClaim()` |

`Dispatchable()` is the only place local computation is now wrong: two machines folding
their own ledgers would both hand out the same item.

## Port surfaces

Every member traces to an existing call site.

```csharp
public interface IWorkItemStore
{
    WorkItem Add(WorkItem item);
    WorkItem Update(WorkItem item);
    WorkItem Transition(WorkItem item, WorkItemState to, string? reason);

    WorkItem? Get(string id);
    IReadOnlyList<WorkItem> All();

    WorkItem? TryClaim(string owner);       // replaces FactoryState.Dispatchable()
    void Heartbeat(string id);
    void Release(string id, string reason);

    void Sync();
    IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan);
}

public interface IRunHistory                // durability floor; always local
{
    void Append(FactoryEvent evt);
    IEnumerable<FactoryEvent> ReadFrom(long seq);

    IReadOnlyList<RunRecord> RunsForItem(string itemId);       // Commands.cs:427
    IReadOnlyList<RunRecord> RunsForStation(string stationId); // EvolutionService.cs:38
    SpendTotals Totals();                                      // Commands.cs:553,564
    BudgetRestoreView ForBudget();                             // FactoryHost.cs:96
    IReadOnlyDictionary<string, string> Champions();           // FactoryState fold
}

public interface IRunHistorySink            // optional, best-effort, write-only
{
    void Emit(FactoryEvent evt);
    void Flush();
}
```

`IRunHistorySink` is deliberately write-only. An evaluator receives traces; it does not
answer the factory's queries. Keeping reads off that interface means a network-backed
plugin can never block `factory report`.

`SpendTotals` and `BudgetRestoreView` are new read-model DTOs introduced by this design.
They exist so a database provider can answer `Commands.cs:553` and `FactoryHost.cs:96`
with an aggregate query instead of returning every `RunRecord` for the caller to fold.
The JSONL provider computes them by folding in memory, exactly as today.

`TryClaim(string owner)` maps `owner` to the bead `assignee`. Beads otherwise derives it
from `$BEADS_ACTOR`, `git user.name`, or `$USER`; the factory passes it explicitly so the
value is stable across environments and identifies the checkout, not the human.

`TryClaim` replacing `Dispatchable()` is the one orchestrator semantic change:
`Orchestrator.cs:114` currently does `Dispatchable().Take(claimable)` and becomes a claim
loop. That is also what makes concurrency-1-per-checkout enforceable without a local lock.

## Plugin model

### Discovery

```csharp
[FactoryProvider("postgres", Contract = 1)]
public sealed class PgRunHistory : IRunHistory { ... }
```

`PluginCatalog` scans `.factory/plugins/*.dll`, loads each in a `PluginLoadContext`,
finds `[FactoryProvider]` types, validates the contract major, and registers by name.
Built-ins are pre-registered under the same naming scheme, so switching from a built-in to
a plugin is a config change.

### Configuration

```jsonc
// .factory/factory.json
"workItemStore": { "provider": "beads" },
"runHistory": {
  "writer": "jsonl",
  "sinks": [ { "provider": "postgres", "options": { "conn": "..." } } ]
}
```

Default is `beads` + `jsonl` with no sinks. Trying the factory out needs no plugins and no
Dolt remote.

### Risk mitigations

These three risks were named and accepted when the in-process model was chosen.

**Core becomes a public ABI.** Made explicit rather than accidental:
`FactoryProviderAttribute.Contract` declares the major version the plugin was built
against, and the catalog refuses to load on mismatch with a named error instead of a
`MissingMethodException` at first call.

**A bad plugin can take the host down.** Different boundaries per port:

| Port | On plugin exception |
|---|---|
| `IWorkItemStore` | wrap in typed `WorkItemStoreException` and halt — a wrong backlog is worse than a stopped factory |
| `IRunHistorySink` | catch, warn, count, disable the sink after N failures, keep running |

The sink decorator is what makes pointing a sink at a network service safe.

**Load-context / diamond dependencies.** `PluginLoadContext` uses
`AssemblyDependencyResolver` for the plugin's own dependencies, but `Factory.Core` always
resolves from the **default** context. Without that, a plugin loads its own copy and its
`IRunHistory` is a different type than the host's — presenting as an unhelpful cast error.

## Beads mapping

Verified against beads 1.2.1 by probe, not assumed.

| WorkItem | bead | Notes |
|---|---|---|
| `Id` | `id` | `Ids.New` separator changes `_` to `-`; `wi-cba5198e7c96` accepted via `--id` |
| `Title` | `title` | |
| `Intent` | `description` | |
| `Kind` | `issue_type` | Feature/Bug/Chore/Spike native; Refactor and Improvement need `types.custom` |
| `State` | `status` | all 9 values map via `status.custom` |
| `Priority` | `priority` | narrowed to 0–4, default 2 (D6) |
| `DependsOn` | `--deps depends-on:` | dependents verified excluded from `bd ready` |
| `ParentId` | epic / `bd children` | |
| `AcceptanceCriteria` | `acceptance_criteria` + `metadata` | text for `bd show`; structured polymorphic graph in metadata |
| `BudgetUsd`, `Provenance`, `Assumptions`, `Requirements`, `Labels` | `metadata` | arbitrary JSON, exact round-trip verified |
| `Station`, `Worktree`, `Attempts`, `LastError`, `SpentUsd` | stay local | volatile per-run state; belongs in the ledger |
| — | `revision` | optimistic-concurrency check on write |

Vocabulary installed at init:

```
bd config set status.custom "draft:frozen,in_review:wip,verified:wip,failed:frozen,cancelled:done"
bd config set types.custom  "refactor,improvement"
```

The `frozen` category on `draft` and `failed` is load-bearing: it keeps proposals and failed
work out of `bd ready`, preserving today's `--include-proposed` semantics exactly.

## Claim protocol

```
TryClaim         -> bd ready --claim --json   (atomic; sets in_progress + assignee + lease)
  station runs
  Heartbeat      -> every TTL/3 while in flight
done             -> Transition(Done) -> bd update --status closed
```

The observed default lease is 5 minutes, shorter than an implement-station run, so
heartbeating is mandatory. No lease-TTL configuration key was found in `bd config list`, so
the cadence must be derived conservatively from the observed expiry rather than assumed
tunable: read `lease_expires_at` from the claim response and heartbeat at a fraction of the
remaining window. Confirming whether the TTL is configurable is an implementation task.

This subsumes most of the queued heartbeat/stall work (`wi_7515c73d98c4`,
`wi_b8ff883da323`, `wi_10c155c8a0c8`), which should be re-scoped rather than built as filed.

## Reconcile on open

```
Open() -> store.Sync()
       -> compare beads.All() against the ledger fold
       -> emit correction events into the ledger (beads wins, unconditionally)
       -> Reclaim() own-node stale leases
```

Self-healing by construction: the failed-ledger-write risk from D2 is corrected at the next
open, and no correction can flow the wrong direction.

## Degraded operation

Beads is a distributed replica model, not client-server. The embedded Dolt engine holds a
complete local database; `bd sync` is a separate federation step. Verified with no remote
configured: `bd create` and `bd ready --claim` both succeed, and `bd sync` exits 0 with
guidance rather than failing.

There is therefore no outbox to build — local Dolt commits are the queue, pushed when the
remote returns. The remote may be a plain GitHub repo over SSH
(`git+ssh://git@github.com/org/repo.git`), DoltHub, or Azure Blob.

Sync points, both non-fatal:

```
Open()          -> Sync()  -> on failure: warn, set Degraded, continue
item completes  -> Sync()  -> on failure: warn, continue
```

`Degraded` is surfaced in `factory status` so an unshared backlog is visible rather than
inferred.

### The offline double-claim hazard

Two machines offline can each claim the same item from their own stale view. On sync,
`status` is an ordinary issue cell and resolves last-write-wins, so one claim silently
loses and the same work ran twice.

D7 closes this using the pipeline's existing shape. `integrate` gates on:

1. `Sync()` succeeds, else `Blocked` with reason `sync-required` (which `factory activate`
   already requeues — no new state needed).
2. Re-read the item; `revision` and `assignee` still match what we claimed. If another
   machine won the race, `Blocked` with the worktree preserved.

Both reuse the behavior added in commit `f58f28b` ("Block instead of failing when
integration cannot land, and keep the worktree"). The worst case of a double-claim becomes
wasted tokens, never a double-merge.

## Limitations

**Cross-machine dead-worker recovery is not automatic.** Beads leases are node-local —
heartbeats write no Dolt commit and no history. `status` and `assignee` replicate, so mutual
exclusion across machines is sound, but lease *expiry* does not, so machine B cannot reclaim
machine A's dead work.

Mitigations, in order of cost:

1. Each host runs `Reclaim()` at `Open()` for its own node — recovers the common case with no new machinery. **In scope.**
2. Cross-machine orphans surface in `factory ls` as `in_progress` with a foreign assignee and a stale `started_at` (which does replicate) — reported, not reaped. **In scope.**
3. Auto-reap foreign orphans past a configurable age. **Deferred** — auto-requeueing work another machine is actively doing is worse than leaving it stuck.

The honest residual gap: a dead machine's item needs an operator to requeue it.

**Beads ships with telemetry enabled** (`metrics.disabled = false`,
`metrics.endpoint = https://gastownhall-eventsapi.com/mp/collect`). What it transmits was
not investigated. Adopting beads as core infrastructure should include a deliberate
decision here; `bd config set metrics.disabled true` disables it.

**Beads auto-starts a local `dolt sql-server` process per project.** The factory gains an
implicit background-process dependency, relevant for CI containers. `factory doctor` should
check it.

## Migration

The 23 existing items export to beads JSONL and import via `bd import` (upsert semantics,
accepts explicit ids). Ids rewrite `wi_` to `wi-`. Existing git history keeps the old form;
that is historical text and needs no rewrite.

## Implementation phasing

This spec is too large for a single plan. It decomposes into four phases, each independently
buildable and independently verifiable, each leaving the factory working:

1. **Extract the ports.** Define `IWorkItemStore`, `IRunHistory`, `IRunHistorySink` in
   `Factory.Core`. Move `Ledger.cs` to `Providers/JsonlRunHistory`. Add
   `InMemoryWorkItemStore`. Route `FactoryServices` through the ports. No behavior change;
   the 114 tests must pass untouched apart from the `LedgerTests` move. This phase alone
   delivers D4 and makes every later phase a drop-in.

2. **Plugin infrastructure.** `FactoryProviderAttribute`, `PluginLoadContext`,
   `PluginCatalog`, `ProviderRegistry`, config binding, the two guard boundaries, and the
   fixture-DLL tests. Delivers D5. Still no beads.

3. **Beads provider.** `BeadsWorkItemStore`, the mapping, vocabulary installation, claim and
   heartbeat, reconcile-on-open, `Reclaim()`, `Degraded` in `factory status`. Delivers
   D1–D3. Priority narrowing (D6) lands here because the mapping depends on it.

4. **Sync gating and migration.** The `integrate` sync gate (D7), foreign-orphan reporting,
   the `factory doctor` dolt-server check, and the one-time migration of the 23 existing
   items.

Phase 1 is worth doing regardless of whether beads is ultimately adopted, which is a useful
property if phase 3 turns up a blocker.

## Testing

- `InMemoryWorkItemStore` keeps the existing 114 tests offline with no `bd` dependency.
- `PluginCatalog` is tested against a real fixture DLL built from a small plugin project, not a mock — assembly loading is the part most likely to break, and a mock would not exercise it.
- Contract-version mismatch, sink-failure disabling, and `WorkItemStoreException` propagation each get a test; they are the three accepted risks.
- Beads adapter tests run against a throwaway `bd init` database in a temp directory, exercising claim, dependency gating, custom statuses, and metadata round-trip.
- Offline behavior is tested with no remote configured; the sync-required integrate gate is tested by pointing at an unreachable remote.
