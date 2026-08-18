# Storage Ports Phase 4: Remediation Prelude

**Date:** 2026-08-18
**Spec:** `docs/superpowers/specs/2026-08-13-storage-adapters-design.md`
**Depends on:** phases 1-3 merged (`2a99415`). Phase 3 landed with known critical defects.

> **This is a prelude, not a replacement.** The detailed phase 4 plan already exists at
> `docs/superpowers/plans/2026-08-13-storage-ports-phase-4-sync-gate.md` (639 lines, six tasks: the
> `SyncStatus` ABI change, the integrate gate, surfacing degraded and orphaned state, the doctor
> check, migration, and cutover). **Execute this prelude's tasks 1-5 first, then that plan** — its
> Task 2 recovery path is `factory activate`, which this prelude's Task 1 is what makes work again.
> Section "Handing off to the sync-gate plan" at the end lists the corrections that plan needs,
> because it was written before phase 3 existed and does not know about the types phase 3 added.

## Why this plan opens with remediation

Phase 3 made beads an available backlog authority and was reported complete on a green suite of 282
tests and a working end-to-end run. An independent review then found three critical defects by
probing real `bd`. They are carried here deliberately: the default provider is still `ledger`, so
nothing is broken for anyone who has not opted into beads, but **every one of them must be fixed
before beads can be made the default or used across machines**, which is what the rest of this phase
builds on.

Read before starting:

- `docs/superpowers/notes/2026-08-18-phase-3-handoff.md` — what phase 3 delivered, the verified `bd`
  behaviour, and the review findings with their reasoning.
- `docs/superpowers/notes/2026-08-18-phase-3-review-reports.md` — the verbatim reviews, with
  `file:line` for every finding. This is the working reference for tasks 1-3.
- `docs/superpowers/notes/2026-08-18-phase-3-preflight.md` — the probed `bd` facts. Treat as fact;
  re-probe only if `bd version` differs from 1.2.1.

## The lesson that shapes every task here

All three criticals are invisible to a test that asserts an item's **status** after an operation, and
every phase-3 test did exactly that. The question that finds them is *"and is the item still
claimable / still resumable / still correctly attributed?"* — assert the post-condition the **next
actor** depends on, not the field the operation just set.

So: for every task below, the acceptance test asserts a downstream consequence, and every new test
is mutation-checked by deleting the logic it names.

## Global constraints

- `Factory.Core` stays dependency-free. Zero `ProjectReference`, zero `PackageReference`.
- .NET 10 SDK pinned to `10.0.400`. Do not change the pin.
- One top-level type per file. XML doc `<summary>` on public APIs only.
- `dotnet build --no-incremental` for any warning count — plain `dotnet build` under-reports by
  recompiling nothing. True baseline is **1** pre-existing CA1416 in `DoctorCommandTests.cs`.
- Re-baseline `dotnet test` at session start. Master after the phase-3 merge is **282 passing**.
- Beads tests create their own throwaway `bd init` database under a temp directory and never touch
  the repository's `.beads` or `.factory`. `bd init` costs ~4s, so share one database per test class
  via `IClassFixture` and keep each test to items it filed itself.
- Every `bd` invocation sets `BD_NON_INTERACTIVE=1`.
- Do not run `factory up` or `factory build`.
- `git checkout -- <file>` to undo a mutation also discards uncommitted work. Commit the task first,
  then mutate, then checkout.

---

## Task 1: Returning an item to Ready must leave it claimable

**The defect.** `BeadMapper.UpdateArgs` never emits `--assignee`; only `ReleaseArgs` clears it. And
`bd ready --claim` skips an `open` bead that still carries an assignee — including for the actor
named in that assignee. Verified:

```
claim as machineA            -> assignee: machineA
bd update -s open -p 1 ...   -> status: open, assignee: 'machineA'
bd ready                     -> lists the bead
bd ready --claim machineA    -> NOTHING CLAIMED
bd ready --claim machineB    -> NOTHING CLAIMED
```

Reachable from `factory activate` on a blocked item, Ctrl-C during a run
(`Orchestrator` cancellation → `Transition(Ready, "cancelled")`), and a retried failure. Items are
stranded permanently: Ready everywhere, claimable nowhere. **This blocks D7**, which the spec builds
on `factory activate` requeueing a `sync-required` block.

**Direction.** Any transition whose destination is `Ready` has to clear the assignee, not just set
the status — the same write `ReleaseArgs` already performs. Decide deliberately whether that belongs
in `UpdateArgs` (conditional on the target state), in `Transition`, or by routing Ready-bound
transitions through the release path. Prefer whichever leaves exactly one place that knows "returning
work to the queue means dropping the claim".

**Acceptance.** A test per reachable path — activate-from-blocked, cancel-mid-run, retry-after-fail —
each asserting the item is **claimable again** via `TryClaim`, not merely that its status is Ready.
That assertion is the whole point; a status assertion here proves nothing.

---

## Task 2: `Reclaim` must only reap this checkout's own leases

**The defect.** `ReclaimArgs` passes `--actor owner`, which is `bd`'s audit-trail flag. The scope
filter is `-a/--assignee`. The phase-3 pre-flight recorded that distinction correctly and the code
still got it wrong; a reclaim response reports `"scoped": false`. The cross-replica guard is opt-in
via `BEADS_NODE_ID` / `bd config set node_id` and `BeadsDeployment` never sets it, so it is inert.

`FactoryHost.Open` therefore reaps every stale lease in the shared store. Because heartbeats are
node-local and do not replicate, machine B sees machine A's lease as long expired and re-runs work A
is actively doing — the spec's limitation mitigation #3, which the spec **explicitly deferred** as
"worse than leaving it stuck".

**Direction.** Scope the reclaim with `-a/--assignee <owner>`, and set a per-machine `node_id` during
deployment so the replica guard is armed. Note `bd reclaim --help`'s two invariants: *grace window >
sync interval* and *lease TTL > sync interval*. The TTL is a fixed 5 minutes and `Sync()` is
currently called once at `Open()`, so the effective sync interval is a whole factory run — Task 4
fixes that side.

**Acceptance.** A foreign lease is not reclaimed. Producing a genuinely stale lease costs the fixed
5-minute TTL, so pin the scoping as a pure-argument test (as `BeadsArgumentTests` already does) and
record honestly that the live reap is probe-evidenced rather than suite-covered.

---

## Task 3: Claiming must not wipe local run state

**The defect.** The claim loop does `Update(claimed with { Station = ... })` where `claimed` came
from `BeadMapper.ToWorkItem` and so has no `Attempts`, `LastError`, `SpentUsd` or `Worktree` —
correctly, since beads does not store them. The mirrored `WorkItemUpdated` then replaces the fold
entry wholesale, resetting all four. `BacklogReconciler.WithLocalRunState` exists to prevent exactly
this on the reconcile path; the claim path has no equivalent.

Spend ceilings survive (`BudgetGuard` keeps its own accumulator), but `factory ls` reports `$0.000`
and 0 attempts for anything claimed under beads, and the `Attempt` number handed to station prompts
resets, so a station retrying thrice-failed work is told it is on attempt 1.

**Direction.** There is one rule here — *an item arriving from the backlog store carries no local run
state, and must not be allowed to erase it* — and it is currently expressed in one place
(`WithLocalRunState`) while two paths need it. Put it somewhere both paths use. `TryClaim` and
`Reclaim` in `LedgerMirroringWorkItemStore` both return store-shaped items and are the natural seam.

**Acceptance.** Claim an item that already has `Attempts`, `SpentUsd`, `LastError` and `Worktree` in
the fold; assert all four survive the claim. Mutation-check by removing the merge.

---

## Task 4: The importants from the phase-3 review

Each is small and independent. Batch them, but give each its own test.

1. **`UpdateArgs` sends neither title, type, description nor dependencies.** An `Update` that changes
   any of them is lost in beads and then *reverted locally* by the next reconcile — the same shape as
   the `CreatedAt` bug fixed in phase 3, not generalised. The bead's `description` and
   `acceptance_criteria` cells also go stale after the first update, diverging from the spec's mapping
   table (`Intent → description`). Note `bd update` has no `--deps`; adding an edge after filing needs
   `bd dep add` / `bd link`, so **probe that before designing it**. Test: update each authoritative
   field, reconcile, assert the value survived.
2. **`LedgerMirroringWorkItemStore`'s catch is too narrow.** `JsonlRunHistory.Append` opens a
   `FileStream`, which throws `UnauthorizedAccessException` (a `SystemException`, not an
   `IOException`) on a read-only ledger; `FactoryJson.Write` can throw `JsonException`. Both escape
   and `GuardedWorkItemStore` converts them into a factory-halting `WorkItemStoreException` naming
   the *backlog* provider — after the beads write already committed. D2 says a failed ledger write
   must be tolerated.
3. **A swallowed mirror of `TryClaim` leaks the claim.** Heartbeat targets come from the fold filtered
   to `State == InProgress`, so if that append fails the bead is claimed with a 5-minute lease that
   nothing refreshes — the phase-3 P8 failure re-entering through the audit path. Driving heartbeats
   from the store's own claim bookkeeping rather than a fold query closes this and stops heartbeating
   beads this checkout does not hold.
4. **`Release` does not enforce the port's state-machine precondition** (`LedgerWorkItemStore` does),
   and `bd update -s open` on a *closed* bead exits 0, so it can resurrect integrated work. No caller
   does this today; the guard exists on one provider and not the other, which is a leaky port.
5. **`Add`'s second write can corrupt a bead another machine claimed** — forcing `--status draft` over
   a live `in_progress` claim exits 0. `BeadRecord.Revision` is deserialised, documented as the
   optimistic-concurrency check, and never read, so the collision is undetectable. Either use
   `revision` / `--if-status` to detect it or narrow the window.
6. **`dependency_type` / `type` are deserialised and never read**, so an edge another tool added as
   `related` or `parent-child` is treated as blocking. D1 makes beads authoritative for edges too.
7. **Minors:** a bead deleted from beads is never removed from the fold; reconcile's own append is not
   failure-tolerant while the mirror's identical call is — make the two agree deliberately;
   `Transition`'s note write is fire-and-forget; `BeadsCli.Captured` rejects a complete
   64,000-character document as truncated (compare `>` with the 4096-char overshoot, or check against
   the parse); the redundant exception filter shared by `Shell.Run` and `Shell.Which`
   (`IOException or InvalidOperationException or SystemException` reduces to `SystemException`).

---

## Task 5: Close the phase-3 test-quality questions

The test-quality reviewer never finished. These are unanswered and worth answering before adding more
tests on the same foundation:

- **The mutation table it never produced.** Attack the phase-3 tests it flagged as most doubtful.
- **Which tests assert an absence** and therefore cannot redden at all — they are guards, not proofs.
  `Volatile_run_state_is_not_sent_to_the_backlog` is the known one.
- **Is `BeadsWorkItemStoreTests`' shared-`bd`-database isolation sound?** It relies on a
  `DrainReadyQueue()` helper. The reviewer was mid-experiment with a custom xunit test-case orderer
  when it stopped. Construct an ordering that breaks a test, or show none exists.
- **Is `A_claim_is_refreshed_while_its_station_works` timing-fragile?** It uses `Thread.Sleep(300)`
  against a 40ms refresh interval.
- **The `bd`-absent early-return guard is unverified.** `Shell.Which` resolves through
  `/bin/sh -c "command -v bd"` and picks up the login shell's PATH, so hiding `bd` from it was not
  possible. Either find a way to exercise it or make the guard testable.
- **`Reclaim`'s id-to-item resolution is not covered** by the suite (needs a genuinely stale lease).

---

## Handing off to the sync-gate plan

Once tasks 1-5 are green, execute
`docs/superpowers/plans/2026-08-13-storage-ports-phase-4-sync-gate.md`. **Pre-flight it first** —
every plan in this series has contained code that could not compile or flags that do not exist, and
this one was written before phase 3 and cannot know what phase 3 actually built. Confirmed deltas:

- **It changes `IWorkItemStore.Sync()` to return `SyncStatus`.** That is an ABI change to the plugin
  contract, so it must also update the implementations phase 3 added, which the plan does not
  mention: `BeadsWorkItemStore` and **`LedgerMirroringWorkItemStore`** (a decorator that forwards
  `Sync`). It does already know about `GuardedWorkItemStore` and `LedgerWorkItemStore`. Consider
  whether it warrants a `FactoryProviderAttribute.Contract` bump, since a third-party plugin
  compiled against contract 1 will no longer satisfy the interface.
- **It knows nothing of `BacklogReconciler`, `LedgerMirroringWorkItemStore`, `ReclaimArgs` or
  `BeadRecord.Revision`.** Its Task 2 re-reads the item and compares `revision` and `assignee` to
  detect a lost claim race — `Revision` is already deserialised and currently unread, so that task is
  its first real consumer. Check the field's semantics before relying on ordering: it is a large,
  often **negative** int64, so compare for equality only.
- **Its second sync point matters more than it looks.** The spec has `Sync()` on item completion as
  well as at `Open()`; only `Open()` is implemented. `bd reclaim --help` requires *grace window > sync
  interval* and *lease TTL > sync interval*, and with sync only at open the effective interval is a
  whole factory run against a fixed 5-minute TTL. This prelude's Task 2 and that sync point are two
  halves of the same problem.
- **Migration must rewrite `wi_` ids to `wi-`.** The plan already covers this. Phase 3 changed
  `Ids.New`, so new work is already compatible, but pre-existing items are not and `bd` rejects the
  `wi_` form outright with `invalid ID format (expected prefix-hash)`.
- **Cutover is where the default provider flips** to beads. Do not flip it until this prelude's tasks
  1-3 are done and the migration has run: every one of those three criticals becomes live the moment
  a deployment opts in.
- Two open decisions the spec asks for and phase 3 did not make: **beads telemetry**
  (`metrics.disabled = false`, endpoint `https://gastownhall-eventsapi.com/mp/collect`; disable with
  `bd config set metrics.disabled true`) and whether `RunHistoryConfig.Writer` should accept a full
  `ProviderRef` so a third-party writer can carry options — currently a bare string, which is a spec
  amendment plus a config migration rather than a code fix.

## Verification gate

- `grep -c "ProjectReference\|PackageReference" src/Factory.Core/Factory.Core.csproj` → `0`.
- Clean tree; no `.beads` and no `.factory` in the repository; `AGENTS.md`, `CLAUDE.md` and
  `.gitignore` unmodified — `bd init` writes all of those into whatever directory it runs in, so a
  test that escaped its temp directory shows up there.
- `dotnet build --no-incremental && dotnet test`, both green, real output pasted.
- End to end in a scratch repository with `"workItemStore": { "provider": "beads" }`: the item appears
  in both `factory ls` and `bd list`, a change made directly in beads is adopted on the next open, and
  a reopen of an unchanged backlog reports **no** corrections.
- For tasks 1-3, the acceptance assertions above — claimable, scoped, run-state-preserving — not
  status assertions.
