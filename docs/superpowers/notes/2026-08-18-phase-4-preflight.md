# Phase 4 Pre-Flight: Plan Defects and Rulings

Run before Task 1 of the remediation prelude, against the real code and a real `bd` 1.2.1, on the
pattern that caught the phase-1, phase-2 and phase-3 plan defects. Every finding below was
**probed, not reasoned about**. Rulings are binding on the tasks that follow.

Baseline in the phase-4 worktree: `dotnet build --no-incremental` → **1 warning** (the pre-existing
CA1416 in `DoctorCommandTests.cs`), `dotnet test` → **282 passing**. `bd version` → 1.2.1
(Homebrew), so the phase-3 pre-flight's probed facts stand and were not re-probed except where a
ruling below depends on them.

Probe database: throwaway `bd init --prefix wi` under the session scratchpad with
`BD_NON_INTERACTIVE=1` and the spec's custom vocabulary installed. No repository `.beads` or
`.factory` was touched.

## Probe evidence

| Probe | Result |
|---|---|
| claim as `machineA`, then `bd update <id> --status open -p 1 --metadata '{}'` (the exact `UpdateArgs` shape) | status `open`, **assignee retained** |
| `bd ready --claim --json --actor machineA` on that bead | `[]` — **nothing claimed**, by the assignee's own name |
| `bd ready --claim --json --actor machineA -a machineA` | `{"error":"--claim cannot be combined with --assignee"}` |
| `bd update <id> --status open --assignee "" --actor machineA`, then `ready --claim` | **claimed** — `in_progress`, assignee `machineA` |
| `bd update <id> --status open --assignee "" --actor machineA` on a bead `machineB` holds `in_progress` | **exit 1**, nothing written: `cannot reassign …: held by "machineB" (in_progress); … pass --force only if their claim is abandoned … or use bd reclaim` |
| `bd update <id> --status draft … --if-status open` on a bead claimed in the window | **exit 13**, nothing written, status still `in_progress` |
| `bd reclaim --older-than 0s --json --actor machineA` | `"scoped": false` |
| `bd reclaim --older-than 0s --json --actor machineA -a machineA` | `"scoped": true` |
| `bd config set node_id nodeA` | writes **`~/.config/bd/config.yaml`** — machine-global, not per-project (reverted after probing) |
| `BEADS_NODE_ID=nodeX bd reclaim …` | accepted; `scoped` still reflects filter scoping only, not the replica guard |
| `bd update --help` | has `--title`, `-t/--type`, `-d/--description`, `--acceptance`, `--if-status`, `--if-assignee`, `--force`, `--claim`; **no `--deps`** |
| `bd dep add <dependent> <blocker>` | exit 0; appears in the dependent's `dependencies[]` as `type: "blocks"` |
| `bd sync` with no remote configured | **exit 0** — "No remote is configured — skipping." |
| `bd sync` with an unreachable remote | **exit 1** — "sync failed: pull: fetch from origin/main: … Host key verification failed" |
| `bd sync --help` exit codes | `0` synced or nothing to do, `1` transport/auth/storage error, `2` merge conflict — halted, `3` retries exhausted (transient), `4` dirty working set stuck (operator only) |
| `bd dolt status` | `Dolt engine: embedded (in-process, no server)` — **there is no `dolt sql-server` process** in this deployment mode |
| `bd info --json` | `{config{…}, database_path, issue_count, mode, schema_version}` |
| `bd export` dependency shape | `[{issue_id, depends_on_id, type, created_at, created_by, metadata}]` |
| `bd import --help` | accepts `dependencies` as that same object array; a row rewrites a local issue **only when its `updated_at` is strictly newer**; `status: "tombstone"` rows are skipped |
| `git remote -v` in this repository | none — so `bd sync` here can only ever report the no-remote skip |

---

## Rulings on the remediation prelude

### PF1 — Critical 1 reproduces exactly, and the fix shape is confirmed

The strand and the recovery are both probed above. `--assignee ""` alongside `-s open` is what makes
a returned item claimable again.

**Ruling:** the assignee clear belongs in `BeadMapper.UpdateArgs`, conditional on the target status
being `open`. That is the single place both `Update(item with { State = Ready })` and
`Transition(item, Ready, …)` pass through, so one edit closes all three reachable paths, and
`ReleaseArgs` keeps its own shape rather than every Ready-bound write being rerouted through it
(`ReleaseArgs` carries no priority or metadata, so routing `Update` through it would drop them).

### PF2 — NEW CRITICAL: PF1's fix turns a stranded foreign item into a factory that will not start

`bd` refuses to clear the assignee of a bead another actor holds `in_progress` (probed: exit 1, and
nothing written). `RequeueOrphans` (`Orchestrator.cs:104`, `:226-229`) iterates
`_s.State.InFlight()` — every fold item in `InProgress` or `InReview`, with **no owner filter** —
and the fold contains foreign items, because `BacklogReconciler` writes every bead `store.All()`
returns into it. So on a shared backlog:

- **today:** machine A releases machine B's live claim; `bd update` refuses, `Release` throws
  `InvalidOperationException`, `GuardedWorkItemStore` converts it to `WorkItemStoreException`, and
  `factory up` dies at run start. This is live now, not introduced by PF1 — phase 3 shipped it.
- **after PF1:** the same refusal additionally reaches every Ready-bound `Transition` and `Update`.

**Ruling:** prelude Task 1 also scopes Ready-bound release to items **this checkout holds**. A
foreign in-flight item is left alone and reported, which is the spec's Limitations mitigation 2
("reported, not reaped") rather than mitigation 3 (deferred). Never `--force`. This needs the bead's
assignee to be visible on the item, hence PF3. The acceptance assertion is that a factory whose fold
holds a foreign in-progress item still starts **and** leaves that item claimable by its holder.

### PF3 — `WorkItem.Owner` moves forward out of the sync-gate plan into prelude Task 1

Sync-gate Task 2 Step 3 adds `WorkItem.Owner`, mapped from `bead.Assignee`. PF2 needs it earlier,
and so does prelude Task 2's evidence that a reclaim was scoped.

**Ruling:** add `Owner` in prelude Task 1 and strike it from sync-gate Task 2. It is an additive
`init` property on a `Factory.Core` record, not an interface change, so it is **not** a contract
break and does not bump `FactoryVersion.ContractVersion` on its own (see SG1, which does).

### PF4 — the "claim your own assigned bead" alternative does not exist

`bd ready --claim` refuses to combine with `-a/--assignee`. Clearing the assignee is the only fix.

### PF5 — `node_id` must not be written to config, and `--assignee` is the scope filter

`-a/--assignee` flips `"scoped": true`, confirming prelude Task 2's direction. But
`bd config set node_id` writes the **machine-global** `~/.config/bd/config.yaml`, shared by every
beads project on the machine; `bd reclaim --help` also warns that a node id committed to the
git-tracked `.beads/config.yaml` leaves the guard "armed-but-inert", and that the id is **one per
store**, not per host.

**Ruling:** `BeadsDeployment` does **not** call `bd config set node_id`. `BeadsCli` sets
`BEADS_NODE_ID` in the environment it already builds for `BD_NON_INTERACTIVE`, valued at the
checkout's `owner`. That is per-process, per-store, and clobbers nothing outside the factory. Record
the one-id-per-store constraint in the doc comment.

### PF6 — Task 4.1 is buildable for the native fields; edges are a separate mechanism

`bd update` accepts `--title`, `-t`, `-d` and `--acceptance`, so title, type, intent and criteria can
all be sent. There is no `--deps` on `update`: a post-filing edge needs `bd dep add <dependent>
<blocker>`, and removing one needs `bd dep remove`.

**Ruling:** `UpdateArgs` gains title, type, description and acceptance. Dependency edges are **not**
folded into `UpdateArgs`; they are a diff (add the new edges, remove the dropped ones) and get their
own step with their own test, or are deferred explicitly with the consequence recorded. Do not
pretend `--deps` exists.

### PF7 — Task 4.5's guard exists and behaves as needed

`--if-status` writes nothing and exits 13 on a mismatch. `Add`'s second write becomes
`--if-status open`, so a bead a concurrent machine claimed in the two-write window is left alone
instead of being dragged back to `draft`.

---

## Rulings on the sync-gate plan

### SG1 — the `Sync()` → `SyncStatus` ABI change touches more than the plan lists, including a plugin fixture

Implementations to update: `LedgerWorkItemStore`, `GuardedWorkItemStore`, `BeadsWorkItemStore`
(the plan knows these three), **`LedgerMirroringWorkItemStore`** (`:63`, forwards `Sync`), and three
test doubles: `GuardedProviderTests.ThrowingStore`, `BacklogReconcilerTests.StubStore`, and
**`tests/fixtures/Factory.TestPlugin/ExplodingStore.cs`** — a real plugin assembly carrying
`[FactoryProvider("exploding-store", Contract = 1)]`. That fixture is the third-party plugin the
contract version exists to protect: it will fail to compile against the new interface, which is the
proof that the break is real.

**Ruling:** bump `FactoryVersion.ContractVersion` to 2 and the fixtures' `Contract` to 2 in the same
task. `PluginCatalog.cs:47` refuses a mismatch with a log line and skips the provider, so leaving the
fixtures at 1 would silently un-register them and fail `PluginCatalogTests` for a misleading reason.

### SG2 — `bd sync` has five outcomes, not two

The plan's `result.Ok ? Success : Unavailable(...)` collapses "merge conflict, halted, resolve by
hand" (2) and "dirty working set is stuck, no later run will publish" (4) into the same degraded
state as an ordinary transport failure. Those two need an operator; 1 and 3 resolve themselves.

**Ruling:** `SyncStatus` carries the exit code alongside `Ok` and `Detail`, and `factory status`
distinguishes "degraded, retrying" from "halted, needs you". Also record explicitly: a deployment
with **no remote configured exits 0**, so `LastSync.Ok` is true and D7's gate passes — correct for
solo use, since there is no second machine to lose a claim race to, but it means a green sync is not
evidence that anything replicated. This repository has no git remote at all, so that is the path the
verification gate will actually exercise.

### SG3 — `StationResult.Blocked` and `GateResult` do not exist

`StationResult` (`Station.cs:66-91`) offers `Ok`, `GateFailed` and no `Blocked`; `grep` finds no
`GateResult` anywhere. The integrate `StationDef` (`Blueprint.cs:202-210`) already sets
`EscalateToHuman = true` with no `OnFail`, and `Orchestrator.cs:329-332` routes an escalating
station's gate failure to `BlockAsync` → `Transition(Blocked)` with the worktree kept.

**Ruling:** the gate returns `StationResult.GateFailed("sync-required: …")` and D7's Blocked state
comes from the existing escalation path. No new result type, no new API. The plan flagged these as
placeholders; this is the resolution.

### SG4 — Task 5's migration emits the wrong dependency shape and no `updated_at`

`bd import` wants `dependencies` as `[{issue_id, depends_on_id, type}]` (confirmed against
`bd export`'s own output), not the plan's array of bare id strings. And import only overwrites a
local row when the incoming `updated_at` is strictly newer, so a migration that omits it cannot be
re-run to correct itself.

**Ruling:** emit the object shape with `type: "blocks"`, and emit `created_at`/`updated_at` from the
item. Keep the `wi_`→`wi-` rewrite on both the id and every dependency reference.

### SG5 — Task 3's testable predicate cannot be `internal`

There is no `InternalsVisibleTo` anywhere in the repository, though `Factory.Tests` does reference
`Factory.Cli` and `Commands` is already `public static`.

**Ruling:** the predicate is `public static bool IsHeldElsewhere(WorkItem, string)` on `Commands`.
Adding an `InternalsVisibleTo` to open up a whole assembly for one predicate is the larger change.

### SG6 — Task 4's dolt-server premise is stale

`bd dolt status` in 1.2.1 reports `Dolt engine: embedded (in-process, no server)`. The spec's
"auto-starts a local `dolt sql-server` per project" is not what this version does, and the phase-3
pre-flight already recorded "no `dolt sql-server` left holding pipes after a `bd` call".

**Ruling:** doctor reports the engine mode from `bd dolt status`, the database path and issue count
from `bd info --json`, and whether a remote is configured — not whether a server process is running.
A missing `bd` while `beads` is configured stays a failure; a missing remote stays a warning.

### SG7 — not a defect: `history.Replay()` exists

Task 1's test writes `new LedgerWorkItemStore(history, history.Replay())`.
`JsonlRunHistory.Replay()` exists (`:112`) and returns `FactoryState`, so this compiles as written.
Recorded because the phase-3 plan's equivalent line did not.

---

## Carried forward from the prelude, unchanged

Prelude Task 4's remaining items (the narrow mirror catch, the leaked claim on a failed `TryClaim`
mirror, `Release`'s missing state-machine precondition, unread `dependency_type`, the minors) and
Task 5's test-quality questions are unaffected by the probes above and stand as written.

---

## For the sync-gate plan: minors deferred out of the final review

Raised by the final whole-branch review at `db7234e` and deliberately left unfixed on this branch.
These three are the ones that change what the **next** plan has to do, so they are recorded here
rather than only in the review, because this note is the artefact the next session reads.

### SG8 — the contract bump has to cover this branch's type widening, and record why it waited

`FactoryVersion.cs`'s own rule is "bump on a breaking change to … any type they expose". This branch
widened three things `IWorkItemStore` and `IRunHistorySink` expose and did **not** bump: five new
`WorkItemKind` members, `WorkItem.Owner`, and `WorkItem.StoreStatus`. That was safe, for a reason
that is not currently written beside any of them:

- an enum member addition is binary-compatible, so it cannot produce the `MissingMethodException`
  the contract major exists to prevent;
- `IWorkItemStore` is one-of, so a third-party *store* never coexists with beads and never meets the
  new kinds;
- the only real exposure is a contract-1 **sink** handed `Kind = Task` with no case for it, and none
  ships.

**For SG1:** the bump to 2 already planned for `Sync()` → `SyncStatus` is the bump that covers all of
this. When it lands, record the reasoning above next to the enum and next to the two new properties,
so the rule's exception is documented where the next reader will look rather than only in a review.

### SG9 — a ledger line from this branch is unreadable by an earlier build

`FactoryJson` uses `JsonStringEnumConverter`, so a ledger line carrying `"kind":"Task"` (or any of
the other four new kinds) fails `FactoryState.Replay` outright on any factory built before this
branch — it does not degrade, and `JsonlRunHistory.ReadFrom` only skips lines that fail to *parse*,
not lines whose enum value is unknown.

Downgrading after cutover is therefore not a supported operation. Say so in the cutover handoff
rather than discovering it during a rollback.

### SG10 — `bd import` overwrites omitted fields with defaults

Probed: a row omitting `priority` and `issue_type` logged `priority 2 → 0, type feature → task`. So
SG4's requirement to emit `updated_at` is not sufficient — the migration must emit **every** field
the item has, or import will silently default the ones it leaves out.

The same probe reproduced SG4's strictly-newer rule: an equal `updated_at` was refused with
`Kept local state … use --allow-stale`.

### SG11 — the legacy priority band must be mapped from the raw ledger lines

New with the final fix wave, and it constrains where the migration reads from. `WorkItem.Priority`
now clamps into 0-4 at every construction path, deserialisation included, so this repository's 87
legacy items (priorities 100 ×80, 150 ×5, 200 ×2 — folded read-only) all arrive as **4** once
`FactoryState.Replay` has read them. Their relative order is gone by the time anything sees the fold.

**So the migration must read the raw ledger lines, not the replayed fold,** if the legacy band is to
be mapped monotonically (100 → 2, 150 → 3, 200 → 4 is the obvious shape). Reading the fold and
emitting what it holds is not wrong — every item lands in band and bd accepts it — but it files all
87 at the lowest priority.
