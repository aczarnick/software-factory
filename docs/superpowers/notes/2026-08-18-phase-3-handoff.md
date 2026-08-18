# Storage Ports: Phase 3 Handoff

> **STATUS CORRECTION (2026-08-18, after independent review): this branch is NOT mergeable.**
> The suite is 282 green and the end-to-end run works, and both of those concealed **three critical
> defects** in the beads provider. An independent correctness review found them by probing real `bd`
> rather than by reading the code. Do not merge, and do not treat the "delivered" section below as
> finished work. The defects and the reasoning are recorded in "Review findings" near the end of this
> note. My own completion report for this phase was wrong, and the reason it was wrong is instructive:
> every one of the three criticals is invisible to a test that asserts an item's *status* after an
> operation, and all of my tests did exactly that.

Phase 3 (beads backlog provider, spec decisions **D1-D3** and **D6**) implemented 2026-08-18 on
branch `storage-ports-phase-3`, in a second checkout at `../software-factory-phase3`. **Not merged
and not pushed.** Seven commits on top of `a0e536e`; suite **282 passing**, build clean apart from
the one pre-existing CA1416.

Read `docs/superpowers/notes/2026-08-18-phase-3-preflight.md` first if you are picking up phase 4 —
it holds the probed facts about `bd` 1.2.1 that this phase is built on, and re-probing them is
wasted effort unless `bd version` has moved.

## What phase 3 delivered

`Shell.Run` (synchronous, bounded-drain) and `Shell.MaxCapturedOutputChars` in `Factory.Runtime`.
`Priorities` and a beads-compatible `Ids.New` in `Factory.Core`. Under
`Factory.Runtime/Providers/Beads/`: `BeadMapper`, `BeadRecord`, `BeadDependency`, `BeadMetadata`,
`BeadsCli`, `BeadsWorkItemStore`, `BeadsDeployment`, `BeadsReclaimResponse`, `ReclaimedLease`. Under
`Providers/`: `BacklogReconciler` and `LedgerMirroringWorkItemStore`. `Leases` names the measured
lease and refresh cadence. `FactoryHost.Open` registers `"beads"`, wraps any non-ledger store in the
audit mirror, then syncs, reconciles and reclaims. `Orchestrator` refreshes claims from a
`HeartbeatTimer` and requeues orphans through `Release`.

**The default provider is still `ledger`.** D1 is delivered as an *available* authority, not as the
default: flipping it is a migration, which is phase 4. Selecting beads is a one-line config change,
`"workItemStore": { "provider": "beads" }`, and that path is proven end to end.

## The two defects the plan did not contain, and how they were found

Both were found by running the thing, not by reading it. Neither is visible in a single write.

1. **There was no audit copy at all.** `LedgerWorkItemStore` records every write as a ledger event;
   `BeadsWorkItemStore` wrote only to beads. So with beads selected, nothing reached the ledger or
   the fold *during a session* — `State.Items` stayed empty, silently breaking everything that reads
   the fold: `factory ls`, dependency queries, `InFlight()` (so orphan requeue), the heartbeat's
   in-progress scan, and the budget. Reconcile-on-open papered over it at the *next* open, which is
   exactly why reasoning about one write cannot see it. This is spec D1 and D2, and the plan
   implemented neither. Now `LedgerMirroringWorkItemStore`, applied by `FactoryHost` to any store
   that is not the built-in ledger: backlog write first and its failure propagates, ledger append
   second and caught, which is D2's asymmetry exactly.

2. **`CreatedAt` did not round-trip, so reconcile churned on every open.** Beads stamps its own
   `created_at` at write time, which can never equal the moment the factory constructed the item, so
   every locally filed item read back as changed and reconcile appended a correction for the whole
   backlog on every open. It also reorders the queue: `Dispatchable()` breaks priority ties on
   `CreatedAt`. The filing time now travels in the bead's metadata and is preferred on read, falling
   back to the bead's own stamp for work another tool filed.

   Instructive detail: the integration test named
   `Reconciling_a_real_backlog_twice_writes_nothing_the_second_time` **passed throughout**, because
   it compared a beads read against a beads read. The divergence only exists between a *locally
   filed* item and its beads copy. `Reopening_an_unchanged_backlog_reports_no_corrections` is the
   test that actually catches it.

A third, smaller one, also only reachable by running it: **`ProviderRef.Options` deserialised to
null.** `[method: JsonConstructor]` binds the primary constructor, so `{"provider":"beads"}` — what
the spec's example and `factory init` both write — left `Options` null and the first provider to
read its own options threw `Value cannot be null. (Parameter 'dictionary')`. A latent phase-2 ABI
defect that only a provider *with* options could surface. Normalised in `Factory.Core` so no
provider has to guard.

## Verified beads behaviour phase 4 inherits

Probed against 1.2.1; treat as fact.

- **`bd list` and `bd show` report dependencies in different shapes.** `show` embeds the blocking
  issue (`id`, `dependency_type`); `list` reports the edge (`issue_id`, `depends_on_id`, `type`).
  `BeadDependency` accepts both. My pre-flight had only ever captured the `show` shape and *inferred*
  that `list` matched — a failing test caught it. Do not infer a second shape from a first.
- **`bd list` pages at 50 by default.** `--limit 0` is required or a backlog silently truncates.
- **`--actor` is the only lever that sets the assignee.** `--assignee` on `bd ready` filters.
- **`bd unclaim` only works from `in_progress`** ("no matching row" otherwise), and moving a bead to
  a `wip` custom status like `in_review` **drops its lease**. `bd update <id> -s open --assignee ""`
  clears status, assignee and lease together from any state, which is what `Release` uses.
- **`bd show <missing>` exits 1; `bd list --id <missing>` exits 0 with `[]`.** The latter is how
  `Get` distinguishes absent from broken without matching on stderr text. `--all` is required or a
  closed or draft bead reads as missing.
- **`bd reclaim --json` returns a summary object** `{count, reclaimed:[{id, previous_owner}]}`, not
  beads, and reclaiming **clears the assignee** — so filtering by assignee afterwards finds nothing.
- **Out-of-band priorities are rejected outright** (`-p 5` exits 1), not clamped.
- **`bd create` has no status flag**, so a non-Ready item is two writes and is briefly claimable in
  between. Accepted.
- **`bd init` writes `AGENTS.md`, `CLAUDE.md`, `.claude/`, `.cursor/`, `.codex/`, `.agents/` and
  appends to `.gitignore`** in the target directory, inside marked regions rather than clobbering.
  `--init-if-missing` is the idempotent form and avoids the destroy-token re-init hazard.
- **The lease is 5 minutes from the last heartbeat** with no config key to change it.

## Traps that cost real time

- **`git checkout -- <file>` to undo a mutation also discards uncommitted implementation work.** It
  bit twice, silently reverting real edits mid-mutation-check and producing confusing extra reds.
  Commit the task first, then mutate, then checkout.
- **A test that compares two reads from the same source proves nothing about a round trip.** See
  defect 2 above. When the question is "does X survive a trip through the store", one side of the
  comparison must not come from the store.
- **`bd init` costs about four seconds**, so a per-test database makes a suite crawl.
  `BeadsWorkItemStoreTests` shares one via `IClassFixture` and the claim tests call
  `DrainReadyQueue()` first so their result cannot depend on execution order. The beads-backed tests
  add roughly 45s to the suite; it is now about 55s in total.
- **Records compare `IReadOnlyList` members by reference**, so a field-by-field `WorkItem`
  comparison silently under-compares. `BacklogReconciler` compares a serialised projection instead.

## Known gaps, deliberately left

- **`Reclaim`'s resolution of reclaimed ids back into `WorkItem`s is not exercised by the suite.**
  Producing a genuinely stale lease needs the fixed 5-minute TTL to elapse and no key shortens it.
  The response parsing is unit tested from captured real JSON, `Get` is heavily tested, and the live
  path was probe-verified by hand. Phase 4 exercises reclaim for real.
- **The `bd init` side effect is accepted, not solved.** Selecting the beads provider means opening a
  factory writes agent-instruction files into the repository. Nothing runs it unless an operator
  selects beads.
- **Beads telemetry is still on** (`metrics.disabled = false`). The spec flags this as needing a
  deliberate decision; phase 3 did not make it. `bd config set metrics.disabled true` disables it.
- **`Release` reads the item before releasing** only to decide whether to no-op, costing an extra bd
  call per release.
- **`IRunHistory.Champions()` still has no caller**, unchanged from phase 1.
- The ledger provider is excluded from the audit mirror by a type test. The cleaner shape is a
  ledger store that writes no events of its own and is always mirrored; that is a phase-4 tidy, not
  a defect.

## What phase 4 still owes the spec

The `integrate` sync gate (D7), `Degraded` in `factory status`, foreign-orphan reporting, the
`factory doctor` dolt-server check, and the one-time migration of the existing items. Note the
migration also has to rewrite `wi_` ids to `wi-`: `Ids.New` changed in this phase, so new work is
already compatible, but the 23 existing items are not.

## Review findings — why this branch is not mergeable

An independent review (two reviewers, split between correctness and test quality) ran after the
phase was reported complete. Standards and the reconciler came back clean and independently
verified: `Factory.Core` has zero dependencies and no beads reference, 13/13 new production files
declare one top-level type, and a re-run of the gate reproduced 282 passing with exactly the one
pre-existing CA1416. `BacklogReconciler` was checked hard and holds — beads wins unconditionally, a
correction cannot flow the wrong way, it cannot loop, and it cannot lose local run state.

The three criticals are all in the store and the claim path.

### Critical 1 — `Transition(→Ready)` strands work permanently

`BeadMapper.UpdateArgs` never emits `--assignee`; only `ReleaseArgs` clears it. And `bd ready
--claim` **skips an `open` bead that still carries an assignee — including for the actor named in
that assignee.** Verified directly:

```
claim as machineA            -> assignee: machineA
bd update -s open -p 1 ...   -> status: open, assignee: 'machineA'   (Transition's exact shape)
bd ready                     -> lists wi-aaaa11112222
bd ready --claim machineA    -> NOTHING CLAIMED
bd ready --claim machineB    -> NOTHING CLAIMED
```

The item shows as Ready in both `factory ls` and `bd ready` and can never be worked again. Reachable
from three ordinary paths: `factory activate` on a blocked item, Ctrl-C during a run
(`Orchestrator` cancellation → `Transition(Ready, "cancelled")`), and a retried failure. Only
`RequeueOrphans` escapes, because it is the single `Release` caller and `Release` does clear the
assignee. This also blocks **D7**, which the spec builds on `factory activate` requeueing a
`sync-required` block.

### Critical 2 — `Reclaim` reaps leases other machines granted

`ReclaimArgs` passes `--actor owner`, which is `bd`'s **audit-trail** flag. The scope filter is
`-a/--assignee`. The pre-flight note recorded that distinction correctly and the code still got it
wrong. The cross-replica guard that would otherwise catch it is opt-in via `BEADS_NODE_ID` /
`bd config set node_id`, and `BeadsDeployment` never sets it, so it is inert — a reclaim response
reports `"scoped": false`. `FactoryHost.Open` therefore reaps every stale lease in the shared store.
Because heartbeats are node-local and do not replicate, machine B sees machine A's lease as long
expired and re-runs work A is actively doing. That is the spec's limitation mitigation #3, which the
spec **explicitly deferred** as "worse than leaving it stuck".

Related: `bd reclaim --help` requires "grace window > sync interval, and lease TTL > sync interval".
`Sync()` is called once, at `Open()`, so the effective sync interval is a whole factory run against
a fixed 5-minute TTL. The spec's second sync point ("item completes → `Sync()`") is not implemented.

### Critical 3 — claiming wipes local run state out of the fold

The claim loop does `Update(claimed with { Station = ... })`, where `claimed` came from
`BeadMapper.ToWorkItem` and therefore has no `Attempts`, `LastError`, `SpentUsd` or `Worktree` —
correctly, since beads does not store them. The mirrored `WorkItemUpdated` then replaces the fold
entry wholesale, resetting all four. `BacklogReconciler.WithLocalRunState` exists to prevent exactly
this on the reconcile path; the claim path has no equivalent. Spend ceilings survive because
`BudgetGuard` keeps its own accumulator, but `factory ls` reports `$0.000` and 0 attempts for
anything claimed under beads, and the `Attempt` number handed to station prompts resets, so a
station retrying thrice-failed work is told it is on attempt 1.

### The importants, briefly

- **`UpdateArgs` sends neither title, type, description nor dependencies**, so any `Update` that
  changes them is lost in beads and then *reverted locally* by the next reconcile — the same shape
  as the `CreatedAt` bug fixed in `1a2ec53`, not generalised. The bead's `description` and
  `acceptance_criteria` cells also go stale after the first update, diverging from the spec's
  mapping table.
- **`LedgerMirroringWorkItemStore`'s catch is too narrow.** `JsonlRunHistory.Append` opens a
  `FileStream`, which throws `UnauthorizedAccessException` (a `SystemException`, not an
  `IOException`) on a read-only ledger, and `FactoryJson.Write` can throw `JsonException`. Both
  escape, and `GuardedWorkItemStore` converts them into a factory-halting `WorkItemStoreException`
  naming the *backlog* provider — after the beads write already committed. D2 says a failed ledger
  write must be tolerated.
- **A swallowed mirror of `TryClaim` leaks the claim.** Heartbeat targets are selected from the fold
  by `State == InProgress`, so if that append fails the bead is claimed with a 5-minute lease that
  nothing ever refreshes — P8 re-entering through the audit-copy path. Driving heartbeats from the
  store's own claim bookkeeping rather than a fold query would close this.
- **The fold and beads can diverge unrepairably** in the five local fields: reconcile's
  `WithLocalRunState` deliberately copies the stale local value forward, so the mirror's log promise
  ("will be corrected at the next open") is false for exactly those fields. Consequence: resuming at
  a station the item had already passed.
- **`Release` does not enforce the port's state-machine precondition** (`LedgerWorkItemStore` does),
  and `bd update -s open` on a *closed* bead exits 0, so it can resurrect integrated work. No caller
  does this today.
- **`Add`'s second write can corrupt a bead another machine claimed** — forcing `--status draft` over
  a live `in_progress` claim exits 0. `BeadRecord.Revision` is deserialised and documented as the
  optimistic-concurrency check and is never read anywhere, so the collision is undetectable.
- Minors: a bead deleted from beads is never removed from the fold; reconcile's own append is not
  failure-tolerant while the mirror's identical call is; `Transition`'s note write is
  fire-and-forget; `BeadsCli.Captured` rejects a complete 64,000-character document as truncated;
  `dependency_type`/`type` are deserialised and never read, so an edge another tool added as
  `related` or `parent-child` is treated as blocking.

### The lesson worth carrying into phase 4

Every critical here is invisible to a test that asserts an item's **status** after an operation, and
every test I wrote did that. The question that finds them is "and is the item still *claimable*?" —
i.e. assert the post-condition the next actor depends on, not the field the operation set. The one
bug I did catch this way (`CreatedAt`) I caught by reading end-to-end output, not from an assertion.

## Repository state

`master` is unchanged at `a0e536e` and the main checkout is still on it and clean — the factory was
stopped for this phase and no test ever wrote to the repository's `.beads` or `.factory`. The
worktree at `../software-factory-phase3` holds `storage-ports-phase-3`, which is `master` plus seven
commits and nothing else; there is no divergence to untangle this time, unlike phase 2. Nothing has
been pushed and `master` remains local-only.
