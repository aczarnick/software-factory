# Phase 4 Handoff: the storage-ports remediation prelude

Written at the end of the prelude, for whoever picks up the sync-gate plan and the beads cutover.
Phases 1, 2 and 3 each committed one of these; phase 4 had committed only its pre-flight, which left
every ruling, accepted cost and deferral in an untracked `.superpowers/` ledger that dies with the
worktree. This is the tracked copy of the parts that outlive the phase.

The companion document is `2026-08-18-phase-4-preflight.md`, which holds the **rulings binding on the
next plans** (PF1-PF7, SG1-SG11). This note deliberately does not repeat them: pre-flight = what the
next plan must do, handoff = what this phase delivered and what it knowingly left behind. SG8-SG11
were added to the pre-flight at the end of this phase and are the three next-plan facts plus the
legacy priority band; read them there.

---

## What the prelude delivered

Five tasks against three criticals a phase-3 review found after merge, then two whole-branch reviews
(correctness, and test quality) and one fix wave closing what they found.

**The three original criticals.**

1. **A requeued item was Ready everywhere and claimable nowhere.** `bd ready --claim` skips an open
   bead that still carries an assignee — even for the actor named in it — and `--claim` refuses to
   combine with `--assignee`, so clearing the assignee is the only fix. `BeadMapper.UpdateArgs` now
   sends `--assignee ""` whenever the target status is `open`, which is the single place both
   `Update(item with { State = Ready })` and `Transition(item, Ready, …)` pass through.
2. **A reclaim reaped another machine's live lease.** Now belt-and-braces: `ReclaimArgs` passes
   `--assignee <owner>` as bd's scope filter, and `BeadsCli` sets `BEADS_NODE_ID` in the environment
   so bd's own cross-replica guard is armed. **The two mask each other** — either alone spares a
   foreign lease — which is why both argument-level tests stay alongside the live one.
3. **A claim blanked this checkout's own run state.** `LocalRunState` defines the five locally-owned
   fields (`Station`, `Worktree`, `Attempts`, `LastError`, `SpentUsd`) exactly once, and both paths
   that take an item from the store apply it: the reconciler over a correction, the mirroring store
   over a claim or reclaim. The complement of that set against `WorkItem`'s mapped fields is closed,
   so the two cannot drift apart.

**Four more of the same family, found by the two final reviews and fixed in the fix wave.** Each is
silent divergence on a shared authority that a status-shaped assertion cannot see:

- An unmapped bd status (`deferred`, `pinned`, `hooked` — each one human command away, exit 0) threw
  and took down every factory command on every machine sharing the backlog.
- `WorkItem.Priority` was unenforced against bd's 0-4 band, and **87 of 87 items in this
  repository's real fold violate it** (100 ×80, 150 ×5, 200 ×2).
- A refused `bd dep add` halted the factory, triggered by a human retyping an edge.
- A human's edit to a bead's own `description` was unread, unreported by reconcile, and silently
  overwritten by the next factory write.

Plus five Importants: a heartbeat failure and a `Sync()` failure were both invisible; one bad id
starved the rest of a heartbeat tick; and a tolerated ledger append also skipped the in-memory fold
for the rest of the run.

---

## Accepted costs

Each of these is a decision, not an oversight. Do not "fix" one without reopening the trade.

### A factory item's own criteria still overwrite a human's `acceptance_criteria` cell

`--acceptance` is sent only when the item has criteria of its own. That protects the common case: a
bead another tool filed arrives with empty `AcceptanceCriteria` (the factory reads criteria only from
its metadata blob), and an unconditional write would render that emptiness over the human's cell.

The accepted cost is the reverse: an item that *has* criteria renders them over a human's cell, and
clearing a factory item's criteria leaves the bead's cell stale while the metadata blob — the
authority for what the factory believes — is correct. Losing another tool's data was judged worse
than a stale human-facing cell. **Both halves now have tests**, so the trade cannot be flipped by
accident.

Note this is *not* how `description`/`Intent` works. That field's read was made faithful instead (the
bead's own cell wins, metadata is the fallback), which is available there precisely because `-d` is
unconditional and so the two agree by construction after any factory write. The two fields differ
because their reads differ, and the reasoning is recorded at both flags.

### A blocking edge another actor adds mid-flight is deleted

`bd update` has no `--deps`, so edges are a diff: read the bead's current blocking edges, add what
the item has and remove what it does not. The item is a snapshot with no base to diff against, so a
blocking edge another actor adds after this checkout read the item is indistinguishable from one this
checkout dropped, and is removed as a local removal.

Narrower than it first appears: the diff is driven entirely from `WorkItem.DependsOn`, which by
construction holds only blocking edges, so a `related` or `parent-child` edge a human filed is in
neither set and can never be named to `bd dep remove` (which has no `--type` flag and would delete
it). And the window is roughly one process-start per write, not one station run, because both halves
of the diff read inside a single `Update`.

Every removal is logged, because deleting another actor's row from a shared database must leave a
trace. **The real fix — a base revision so a remote add can be told from a local removal — needs a
three-way merge the port cannot express today. It is on the sync-gate decision list below.**

### One orphan that cannot be requeued is stepped over, not retried or forced

`RequeueOrphans` picks its targets from the fold while the backlog decides whether a release is legal,
so the two can disagree: a bead another machine closed between this host's open and now is released
as a Done item, which the port is right to refuse. A single refusal must not stop a factory before it
has started, nor cost every other orphan its requeue. The log line is the whole report — nothing is
retried and nothing is forced.

The same shape now guards the heartbeat tick per id, for the same reason.

An item another checkout holds is reported and left alone rather than reaped (spec Limitations
mitigation 2). Never `--force`: its holder may be working on it right now, and only its own restart
or an expired lease can safely return it. **The residual gap is the spec's stated one**: a genuinely
dead foreign machine's item needs an operator.

### A reason string is lost on any mirror that changed the owner

`FactoryState.ApplyLocked` applies a `WorkItemStateChanged` as `State` + `UpdatedAt` only, so a claim
or a Ready-bound release that changed who holds the item would never reach the fold at all if
mirrored that way. Those are mirrored as a whole-record `WorkItemUpdated` instead, which carries the
owner and drops the reason. The reason survives on every other transition.

### A status the factory cannot name reads as `Blocked`

For `deferred` that is faithful. For `pinned` and `hooked` it is honest about the operational fact —
not available to be worked on, and probed: `bd ready` withholds all three — and imprecise about
intent. The imprecision is purely local: `WorkItem.StoreStatus` carries the bead's own word, the
write leaves that cell alone, and every other reader of the backlog still sees `pinned`.
**Distinguishing the meanings would need a new factory state, which is a design decision nobody has
made.**

### The legacy priority band is flattened as the fold replays

`WorkItem.Priority` clamps into 0-4 at every construction path, deserialisation included, so the 87
legacy items all arrive as `4` and their relative order is gone by the time anything reads the fold.
This is safe — every value lands in band and bd accepts it — but it reorders the real backlog. See
pre-flight SG11: **the migration must read the raw ledger lines, not the replayed fold**, if the
order is to survive.

### Two facts about latency, both real and both accepted

- The edge diff added **one `bd` read to every `store.Update`**. That is production latency, not just
  suite time.
- `FactoryHost.Activate` calls `Transition` then `Update`, so the dep diff runs twice per activation:
  two extra `bd list` reads and a second no-op diff. Cost only.

---

## Open decisions for the sync-gate plan

These are spec questions the prelude deliberately did not answer. Each has a stated cost of leaving
it open.

| Decision | Why it is open | Cost of leaving it |
|---|---|---|
| **Should D2's ledger tolerance be provider-agnostic?** `LedgerFaultTolerance` tolerates `IOException` and `UnauthorizedAccessException`, which is correct for `JsonlRunHistory` and honestly scoped to it. A plugin ledger over a database or HTTP reports transport faults as the very types deliberately excluded (`InvalidOperationException` for a closed connection, `ObjectDisposedException` for a recycled client). | Whether the tolerance belongs to the port or to the file-backed implementation is a spec question, not a code fix. Nothing ships a plugin ledger today. | A plugin ledger's transport fault halts a factory, with a misattributed provider name. |
| **A base revision for the dependency-edge diff.** | Needs a three-way merge the port cannot express. | A concurrent operator's mid-flight blocking edge is still lost, now with an audit line naming exactly what was removed. |
| **The tombstone.** Reconcile reports a bead that vanished from the authority and leaves the fold entry. A `WorkItemDeleted` event would change the `[JsonPolymorphic]` contract. | Deliberately kept out of a minors bundle: an ABI change inside one is the scope creep that stalls a review. | `factory ls` keeps showing a pruned item and `DependencySatisfiedLocked` keeps treating it as an unmet blocker. |
| **Beads telemetry.** | Carried from the prelude unanswered. | No visibility into `bd` invocation volume, which the two latency facts above make worth having. |
| **`RunHistoryConfig.Writer`.** | Carried from the prelude unanswered. | — |
| **The legacy priority band's explicit mapping at import.** New with this phase's C2. | The monotone shape (100 → 2, 150 → 3, 200 → 4) is a migration concern, and hardcoding one deployment's history into `Factory.Core` would be dead code everywhere else and deleted after cutover. | The migration files all 87 items at the lowest priority. |
| **`owner` is passed to both `BeadsCli` and `BeadsWorkItemStore` with nothing enforcing they agree.** | `FactoryHost` is the only production construction site and passes one value; unifying it churns ~20 test call sites. | A divergence would silently disarm one reclaim guard while the other masked it — and the masking is now proven, so this is a real hazard rather than a tidiness point. |
| **The repo-wide XML-doc convention.** The stated rule is `<summary>` on public APIs only; the code has settled on the opposite at ~18 sites in the changed files, most pre-existing, and the content is load-bearing everywhere. | Decide the rule, not the site. | Every review re-litigates it. |

---

## Still deferred, with the reason

Not worth doing now; recorded so the next reader does not rediscover them.

| Item | Reason |
|---|---|
| `RequeueOrphans`' two paths are told apart only by their wording | Fragile but honestly labelled, and the comment tells the next maintainer to reword both together |
| `Activate` runs the dep diff twice | Cost only; see above |
| `Release` is check-then-act where `--if-status <observed>` would be atomic | No caller reaches it; belongs with the `--if-assignee` work in the sync-gate plan |
| `Shell.cs` carries the same seven-line comment twice | Put the rationale once and have the second site point at it |
| `UnwritableLedger` duplicated verbatim between two test files | Second occurrence; extract on the third |
| `BeadRecord`'s summary says "the subset the factory reads", but `Revision`, `LeaseExpiresAt`, `HeartbeatAt` and `LeaseGrantedNode` have no production reader | The fields are the observation surface the tests assert post-conditions through — amend the summary, do not delete more fields. Deleting `StartedAt` on that ground already took away a column a lease test may want |
| `AForeignBeadTheFactoryHasClaimedAndUpdated`'s comment describes the pre-fix world (`ForeignType = "epic"` is now mapped) | Test still correct and still guards the fix |
| The genuinely-unknown-custom-*type* path (`KindFor`'s `_ => Feature`, which still destroys the type on the next update) has no test asserting that accepted loss | `StoreStatus` is the complete fix for the status side; the type side would need the same treatment and no one configures custom types |
| `Activating_…` / `Retrying_a_failed_item_…` are one shape twice | Two occurrences; a `[Theory]` is nicer, not required |
| Half the reclaim seam test guards a return value no caller reads | `FactoryHost` reads only `.Id`; the port's contract is a list of items and sync-gate Task 2 is its first real consumer |
| `LocalRunState` is `internal` yet documented; XML doc on private methods | Subsumed by the convention decision above |
| A capture cut below the bound surfaces as a raw `JsonException` blaming bd | Pre-existing and identical under the old length check |
| An unnecessary `DrainReadyQueue()`; a test helper's missing guard clause; `MetadataKeys` declared between two `[Fact]`s; `"test-machine"` repeated three times | Polish. Removing a drain can *introduce* order dependence, so that one is not free |
| `FactoryPaths.cs` declares two top-level types; `BeadsWorkItemStoreTests.cs` declares two | Pre-existing; splitting `FactoryPaths.cs` moves a public `Factory.Core` type for zero behaviour |
| The occupied-pair refusal blocks the whole run rather than one item | Blocking one item needs `BlockAsync`, upstream of the store |
| `factory cancel` pays the new dep read twice | Pre-existing redundancy this diff amplifies |

---

## The flake — must be dealt with before the suite gates the cutover

**`RuntimeTests.Two_independent_ready_items_are_both_claimed_and_completed`**, `Expected 2 / Actual 1`,
measured at roughly **1 run in 9**.

**Why it does not block this merge.** It is pre-existing, it drives the **ledger** provider — which
this branch does not touch — and the assertion that fails is about concurrency, not about anything
under review.

**The amplification mechanism, confirmed.** There is no `xunit.runner.json`, no `CollectionBehavior`
and no `MaxParallelThreads` anywhere in the test project, so classes run in parallel at the default
degree. This test runs `MaxConcurrency = 2` with real git worktrees and `test -f` gates, while the
beads classes it overlaps now spend far more subprocesses than at baseline: one refresh test sleeps
3.5 s while a 200 ms heartbeat timer fires ~12 `bd heartbeat` processes, and `WaitForARefresh` polls
`bd list` every 300 ms to a 20-second bound. **The branch did not create the defect and did make it
likelier.**

**Why it cannot merely be deferred in silence.** A gate that reddens 1 run in 9 trains people to
re-run it, which is precisely how phase 3's three criticals survived a green suite. It now asserts
`report.Failed == 0` and `report.Blocked == 0` before the count, so the next occurrence says *which*
failure mode it hit instead of only that a count was short.

**It must not gate the provider cutover until it is fixed.** A suite that reddens 1 run in 9 cannot
be the evidence for flipping the default backlog provider. Cheapest containment is an
`xunit.runner.json` with `parallelizeTestCollections: false`, or the trait split below.

---

## Two things about the gate itself

**65 beads tests pass vacuously without `bd`.** `if (Unavailable) return;` / `if (!Available) return;`
guard 65 tests, and xunit 2.9.2 has no dynamic skip, so each reports as **passed**. On a machine
without `bd` the same green total prints while none of the beads behaviour runs — and "the suite is
green" is the only evidence offered for any of it. `BeadsAvailabilityTests` is now the one red that
names the vacuum. A `[Trait("Category", "Beads")]` on the four beads classes would additionally give
a `bd`-less machine an explicit filter, and a ~1-minute inner loop: the beads surface alone accounts
for essentially the whole wall clock (~104 tests / ~3.5 minutes of a ~4-minute suite), so the other
~290 tests are free.

**`dotnet test` alone does not rebuild the plugin fixture.** `tests/fixtures/Factory.TestPlugin` is
not a `ProjectReference` of the test project — `PluginFixture` copies its built dll from its own
`bin/` — so a change to a fixture provider needs `dotnet build` at the root first, or the plugin
tests silently run against the previous dll. This cost real time during this phase.

---

## Where the untracked record lives

The full execution ledger (527 lines, every `Ruling:` line with its stated cost if wrong), the 21
task reports and reviews, both final whole-branch reviews and the fix-wave report are in
`.superpowers/sdd/2026-08-18-storage-ports-phase-4-remediation-prelude/`, which is excluded in
`.git/info/exclude`. Everything from it that outlives the phase is in this note or in the pre-flight.
If that directory is gone, nothing above depends on it.
