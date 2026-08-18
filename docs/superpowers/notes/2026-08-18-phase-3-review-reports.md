# Phase 3 Independent Review Reports (verbatim)

Two reviewers ran after phase 3 was reported complete, split between correctness and test quality.
Their reports are preserved here verbatim because the working directory they were written in is
gitignored scratch. The findings are summarised, with the reasoning, in
`2026-08-18-phase-3-handoff.md`; this file is the detail, including `file:line` references that
phase 4 needs in order to act on them.

The test-quality reviewer did not finish: its mutation table and its shared-`bd`-fixture ordering
experiment were still in progress when it stopped, so sections 1, 2 and 4 of its mandate are
unanswered and those questions remain open for phase 4.

---

# Phase 3 (beads) — Correctness Review

STATUS: in progress

Scope: correctness and the claim/lease protocol only. Style, naming, comment density and test
quality are a separate reviewer's. Baseline `a0e536e..09cc08e`, worktree
`/Users/aczarnick/personal/repos/software-factory-phase3`.

---

## Area 1 — `BeadMapper`

### Important — `UpdateArgs` sends neither `title` nor `issue_type`, so `Update` silently drops both, and reconcile then reverts them

`src/Factory.Runtime/Providers/Beads/BeadMapper.cs:175-181` writes only `--status`, `-p` and
`--metadata`. `CreateArgs` (`:121`) writes `title`, `-t`, `-d` and `--acceptance` in addition.

Because `ToWorkItem` reads `Title` from `bead.Title` (`:91`) and `Kind` from `bead.IssueType`
(`:93`), and reconcile compares the whole projection and lets beads win (D1/P13), the sequence is:

1. Any caller does `Items.Update(item with { Title = "corrected title" })`.
2. The bead keeps the old title; the local fold gets the new one.
3. Next `Open()` → `BacklogReconciler` sees the projections differ → emits
   `WorkItemUpdated(authoritative)` → **the local title silently reverts.**

The edit is lost with no error and no log line saying it was overwritten. Same for `Kind`.
`DependsOn` has the same shape (only `CreateArgs` emits `--deps`), so an edge added after filing
never reaches beads and is reverted at the next open.

This is not merely theoretical for `DependsOn`: `Blockers` is read back into `DependsOn`
(`:101`) and `FactoryState.Dispatchable()` gates on it, so a post-filing dependency edge is
dropped from the authority that other machines read.

`Intent` and `AcceptanceCriteria` do survive, but only because `ToWorkItem` prefers
`metadata.Intent` over `bead.Description` (`:92`) and criteria live wholly in metadata. The
consequence is that the bead's own `description` and `acceptance_criteria` cells go stale after
the first `Update`, so a human reading `bd show` sees the filing-time text, not current text.
That is a divergence from the spec's mapping table, which states `Intent → description`.

### Important — `Add` for a non-Ready item writes `--status draft` but the item is `open` in between, and the second write is unguarded against a claim that already happened

`BeadsWorkItemStore.cs:11-18`. P11 accepted the claimable window. What P11 did not consider is
that the second write is `Update`, which unconditionally forces `--status draft`. If a
concurrent `bd ready --claim` won the window, the bead is `in_progress` with an assignee, and
this write drags it back to `draft` while the other claimant is running it — the claim is
silently voided rather than the collision being detected. `revision` is read into `BeadRecord`
(`:20`) precisely for optimistic concurrency and is never used anywhere.

Note the ordering also means `Add` is the only path where the spec's "two writes" produce a
*wrong* intermediate: filing a `Draft` item makes it dispatchable for the duration of one local
`bd` invocation, which for the factory's own `Submit(item, activate: false)` proposals defeats
the `--include-proposed` gate the `frozen` status category exists to enforce.

### Minor — `BeadDependency.BlockerId` resolution and the self-id guard are correct, including the reversed edge

`BeadDependency.cs:27` is `DependsOnId ?? Id ?? ""`. For the `bd list` edge shape
(`issue_id` + `depends_on_id`) `DependsOnId` wins, which is the blocker. For the `bd show`
embedded shape only `id` is present, which is also the blocker. Preferring `DependsOnId` is the
right precedence: an edge row also carries `id` in some beads outputs, and taking `Id` first
would read the edge's own row id as a blocker.

The self-id guard in `Blockers` (`BeadMapper.cs:119`) covers the reversed-edge case: when beads
reports the edge from the blocker's side (`issue_id` = the blocker = this bead), `DependsOnId`
points at the dependent and the guard does *not* fire, so a **reversed edge is mapped as a
dependency in the wrong direction** rather than being dropped. The guard only catches the case
where the resolved blocker equals this bead. This is a latent hazard rather than an observed
one: `bd list --json` was probed and reports `depends_on_id` from the dependent's row only.
Worth a probe before phase 4 relies on it.

`dependency_type` / `type` are deserialised (`:22-23`) and then **never read** — every edge is
treated as `depends-on` regardless of whether beads reported `blocks`, `related`,
`parent-child` or `discovered-from`. `CreateArgs` only ever writes `depends-on:` (`:141`), so
the factory's own edges are fine; edges another tool added in beads are mis-read as blocking.
Flagging as **Important** for D1 (beads is authoritative, including for edges another tool
added).

### Minor — nothing that must stay local is sent to beads

`MetadataFor` (`:68-80`) carries exactly `Intent`, `Requirements`, `Criteria`, `Assumptions`,
`Labels`, `ParentId`, `BudgetUsd`, `ProvenanceKind`, `ProvenanceSource`, `CreatedAt`.
`Station`, `Worktree`, `Attempts`, `LastError` and `SpentUsd` are absent, as the spec requires.
`BeadMetadata` has no setters for them, so a future field cannot leak in by accident.

Mapping totality: every `WorkItem` field except the five deliberately-local ones and `UpdatedAt`
round-trips. `UpdatedAt` comes from `bead.UpdatedAt` and is stripped from the reconcile
projection, which is right.

### Minor — `KindFor` swallows an unmapped `issue_type` as `Feature` while `StatusFor`/`StateFor` throw

`BeadMapper.cs:64` vs `:42`. If `bd config set types.custom` failed (see Area 5) a bead created
as `refactor` reads back as `Feature`, then reconcile rewrites the local fold to `Feature`, and
the item's kind is silently wrong forever. `BeadsDeployment.Install` only *logs* a failed
vocabulary write (`BeadsDeployment.cs:27`), so this path is reachable.

---

## Probe evidence gathered for this review

Throwaway `bd init --prefix wi` database in a scratch temp directory, real `bd` 1.2.1
(`/opt/homebrew/bin/bd`), `BD_NON_INTERACTIVE=1`, the spec's `status.custom` and `types.custom`
installed. No repository `.factory/` was touched.

| Probe | Result |
|---|---|
| `bd ready --json` on an `open` bead with a **non-empty assignee** | the bead **is listed** |
| `bd ready --claim --json --actor X` on that same bead | **skipped entirely** — a lower-priority unassigned bead is claimed instead, and the bead is never handed out, not even to the actor named in its own assignee |
| `bd update <id> --status blocked -p 0 --metadata '{}'` (the exact `UpdateArgs` shape) | status `blocked`, **assignee retained**, lease dropped |
| `bd update <id> --status open -p 0 --metadata '{}'` after that | status `open`, **assignee still retained** |
| `bd update <id> --status open --assignee "" --actor X` on a **closed** bead | **exit 0** — the closed bead is reopened |
| `bd update <id> --status draft ...` on a bead another actor holds `in_progress` | **exit 0** — status forced to `draft`, lease dropped, the other actor's assignee retained |
| two concurrent `bd ready --claim` against one ready bead | atomic: one gets the bead, the other gets `[]` |
| `bd ready --claim --json` response body | carries `metadata` **and** `dependencies[]` |
| `--deps depends-on:X` edge as reported by `bd list` | `{issue_id, depends_on_id, type: "blocks"}`, present on the **dependent's** row only; the blocker's row has no `dependencies` key |
| `bd dep add A B --type related` | appears in `A.dependencies[]` as `type: "related"`; beads correctly keeps `A` in `bd ready` |
| `bd reclaim --older-than 0s --json --actor machineA` with a live foreign lease | `{"count":0,"reclaimed":null,"schema_version":1,"scoped":false}` |
| `bd reclaim --help` | `--actor` is a **global audit-trail flag, not a scope filter**; the scope filters are `-a/--assignee`, `--label`, `--id`. The cross-replica guard is **opt-in** via `BEADS_NODE_ID` / `bd config set node_id`; unset means "the old, unguarded behavior" |

---

## Area 2 — `BeadsWorkItemStore`

### CRITICAL — every path that returns an item to Ready through `Transition` strands it forever: `bd ready --claim` will not hand out an `open` bead that still has an assignee

`BeadMapper.UpdateArgs` (`BeadMapper.cs:175-181`) never emits `--assignee`. Only
`ReleaseArgs` (`:167-168`) clears it. So:

- `BeadsWorkItemStore.Transition(item, Ready, ...)` → `bd update -s open` → assignee stays set.
- `bd ready --claim` **skips** an `open` bead with a non-empty assignee (probed above), including
  when the requesting `--actor` *is* that assignee.

Result: the bead is `open`, shows in `bd ready`, shows in `factory ls` as Ready, and is
**permanently unclaimable by any machine including the one that owns it**. Nothing in the phase-3
code path ever clears the assignee for it, because `RequeueOrphans` — the only `Release` caller —
filters to `FactoryState.InFlight()` (`FactoryState.cs:112-118`, InProgress/InReview only).

Three reachable sequences, all proven against real `bd` above:

1. **`factory activate` on a blocked item.** `Orchestrator.BlockAsync` (`Orchestrator.cs:392`)
   → `Transition(Blocked)`; the assignee survives. Then `FactoryHost.Activate`
   (`FactoryHost.cs:234`) → `Transition(item, Ready, "activated")` → `bd update -s open`,
   assignee still `machineA`. The item never runs again. This is the exact path spec decision **D7
   depends on** ("`Blocked` with reason `sync-required`, which `factory activate` already
   requeues — no new state needed"), so D7 cannot be built on phase 3 as it stands.
2. **Ctrl-C during a run.** `ProcessItemAsync`'s cancellation handler (`Orchestrator.cs:340`)
   → `host.Transition(run.Item, Ready, "cancelled")`. Every item in flight at cancellation is
   stranded; the next `factory up` sees them as Ready and never claims them.
3. **A retried failure.** `FailAsync` → `Transition(Failed)`; `factory activate` → `Ready`;
   stranded.

Only `RequeueOrphans` (which uses `Release`) is safe, and it only covers items still
InProgress/InReview at restart.

The existing suite cannot see this: `BeadsWorkItemStoreTests` asserts `status` after a transition,
never that the bead is still claimable afterwards.

### CRITICAL — `Reclaim` is not scoped to this checkout, so it reaps leases another machine granted — the mitigation the spec explicitly **deferred**

`BeadMapper.ReclaimArgs` (`:172-173`) emits `["reclaim", "--older-than", "<n>s", "--json",
"--actor", owner]`. Per `bd reclaim --help`, `--actor` is a **global audit-trail flag**; the scope
filter is `-a/--assignee`. The probe response confirms it: `"scoped": false`.

The cross-replica guard that would otherwise save this is **opt-in** — `bd reclaim --help`:
"The guard is opt-in: set `BEADS_NODE_ID`, or run `bd config set node_id <name>` … an unnamed
deployment keeps the old, unguarded behavior". `BeadsDeployment.EnsureInitialised`
(`BeadsDeployment.cs:8-22`) sets `status.custom` and `types.custom` and nothing else, and
`BeadsCli`'s environment is only `BD_NON_INTERACTIVE=1` (`BeadsCli.cs:8-9`). The guard is inert.

So `FactoryHost.Open` (`FactoryHost.cs:124`) unconditionally reaps **every** stale lease the shared
store holds, not this checkout's. Sequence: machine A claims `wi-x` and works it for 40 minutes;
machine A's heartbeats are node-local and do not replicate (spec, Limitations); machine B opens its
factory, sees `wi-x`'s replicated `lease_expires_at` long past, reclaims it, and claims it. Both
machines now run `wi-x`. That is spec mitigation #3, recorded as **Deferred** with the reason
"auto-requeueing work another machine is actively doing is worse than leaving it stuck".

Two further things `bd reclaim --help` states that phase 3 violates by construction:
"grace window > sync interval, and lease TTL > sync interval". The lease TTL is a fixed 5 minutes
and `Sync()` is called **once, at `Open()`** (the only call site — `FactoryHost.cs:121`), so the
effective sync interval is the length of a whole factory run. The spec's second sync point
("item completes → `Sync()`") is not implemented.

Fix direction (not applied): `--assignee <owner>` in `ReclaimArgs`, plus setting a per-machine
`node_id` in `BeadsDeployment`.

### CRITICAL — claiming an item wipes its local run history from the fold

Claim loop, `Orchestrator.cs:164-168`:

```csharp
if (_s.Items.TryClaim(_s.Config.Name) is not { } claimed) break;
claimed = _s.Items.Update(claimed with { Station = claimed.Station ?? ... });
```

`claimed` is `BeadMapper.ToWorkItem(bead)`, and the mapper never sets `Station`, `Worktree`,
`Attempts`, `LastError` or `SpentUsd` — correctly, since beads does not store them. But the
following `Update` mirrors `WorkItemUpdated(claimed)`, and `FactoryState.ApplyLocked`
(`FactoryState.cs:64-66`) replaces the fold entry **wholesale**. So on every claim the fold's
`Attempts`, `LastError`, `SpentUsd` and `Worktree` are reset to defaults.

`BacklogReconciler.WithLocalRunState` (`BacklogReconciler.cs:53-61`) exists precisely to stop this
happening on the reconcile path; the claim path has no equivalent. `LedgerWorkItemStore.TryClaim`
is unaffected because it claims the fold item itself, so this is a beads-provider-only regression.

Blast radius, checked rather than assumed: `BudgetGuard` keeps its own `_perItem` accumulator
restored from run history (`Budget.cs:31,114-124`), so **spend ceilings are not bypassed**, and
worktree resume keys off `Directory.Exists(WorktreesDir/<id>)` (`FactoryHost.cs:232`) rather than
`item.Worktree`. What is actually lost is: `factory ls` / `factory show` reporting `$0.000` spend
and `0` attempts for every item currently or previously claimed under beads
(`Commands.cs:379,400`), and the `Attempt` number handed to station prompts
(`Station.cs:174`) resetting to 0 across runs, so a station retrying an item that has already
failed three times is told it is on attempt 1. Rated Critical for the silent-fold-corruption
mechanism rather than for today's symptom; a later reader of `item.Attempts` inherits a
permanently broken value.

### Important — `Release` does not enforce the port's state-machine precondition, and reopens finished work

`IWorkItemStore.Release`'s contract: "rejects an item whose current state cannot reach Ready.
Providers raise that as `InvalidOperationException`". `LedgerWorkItemStore.Release`
(`LedgerWorkItemStore.cs:59-66`) honours it by routing through `Transition`.
`BeadsWorkItemStore.Release` (`BeadsWorkItemStore.cs:55-61`) checks only existence and then writes.

Probed: `bd update <closed-id> --status open --assignee "" --actor X` exits **0** and the bead is
`open` again. And `FactoryState.ApplyLocked` does **not** validate transitions
(`FactoryState.cs:68-70` — it assigns `State = c.To` unconditionally), so the mirror's
`WorkItemStateChanged(Done → Ready)` is accepted by the fold too. `Release("<a Done item>")` on the
beads provider therefore **resurrects integrated work onto the ready queue on both sides**, where
the ledger provider would have thrown. No current caller does this (`RequeueOrphans` filters to
in-flight), so it is a latent contract break rather than a live bug — but the guard exists on one
provider and not the other, which is the definition of a leaky port.

### Important — `Add`'s second write silently voids a claim another machine just took

`BeadsWorkItemStore.cs:11-18`. P11 accepted the two-write window as "one local write apart".
Probed consequence: if a concurrent `bd ready --claim` wins the window,
`bd update <id> --status draft -p 0 --metadata '{}'` **exits 0**, forces the bead to `draft` and
drops the lease while the other claimant is still running it — and leaves that claimant's assignee
in place, which by the first Critical above makes the bead permanently unclaimable once the factory
later moves it back to `open`. `BeadRecord.Revision` (`BeadRecord.cs:20`) is deserialised and
documented as the "optimistic-concurrency check on write" and is **never read anywhere**, so the
collision is undetectable. The accepted risk in P11 was a lost claim; the actual risk is a
corrupted bead.

### Minor — `Transition`'s note write is fire-and-forget, so a failed audit note is invisible

`Transition` (`:26-37`) does the state-machine check **before** the write (correct), then
`Update(moved)` then `Note(item.Id, reason)`. `Note` (`:86-89`) calls `cli.Exec` and discards the
`ShellResult`, so a non-zero `bd note` after a successful status write is silently dropped — the
transition reason exists in the ledger but not in beads, and nothing says so. Not a state
correctness problem (the reason is not authoritative), but it is the one place in the class where a
`bd` failure is neither thrown nor logged.

Also: `Transition` returns `item with { State = to, UpdatedAt = now }`, discarding the `UpdatedAt`
`Update` computed. Harmless, but it means the returned `UpdatedAt` is not the one beads recorded.

### Minor — `Heartbeat` is correctly best-effort, but is called for beads this checkout does not hold

`Heartbeat` (`:53`) discards the `ShellResult`, which is the documented intent and matches the
probe evidence (`bd heartbeat` exits 1 on an `in_review` bead and on a bead another actor holds).
`GuardedWorkItemStore` wraps it (`GuardedWorkItemStore.cs:18`) and `HeartbeatTimer.RunTickAsync`
catches everything (`HeartbeatTimer.cs:64-67`), so there is no path where a refused heartbeat halts
the factory. See Area 5 for the wasted-subprocess consequence.

### Minor — `BeadsCli.Captured` false-positives at exactly the bound

`BeadsCli.cs:50-57` throws when `stdout.Length >= Shell.MaxCapturedOutputChars`. `Shell.ReadAsync`
appends a whole 4096-char buffer whenever `sink.Length < MaxCapturedOutputChars`
(`Shell.cs:241`), so a real truncation lands anywhere in `[64000, 68095]` and is caught — good.
But a *complete* JSON document of exactly 64,000 characters is rejected as truncated. The
capture bound is the right thing to name loudly; the comparison should be `>` with the overshoot
accounted for, or the bound should be checked against the parse rather than the length.

---

## Area 3 — `LedgerMirroringWorkItemStore` (D2)

**Order is right.** Every mutating member calls `inner` first and mirrors afterwards
(`LedgerMirroringWorkItemStore.cs:17-58`); a thrown backlog write propagates before any ledger
event exists, and the ledger append is the only thing wrapped in `try`. That is D2 exactly. The
decorator-rather-than-baked-in choice recorded in `progress.md` is sound and I am not
re-litigating it.

`state.Apply` cannot throw — `FactoryState.ApplyLocked` (`FactoryState.cs:54-90`) is a switch of
dictionary assignments with no throwing path — so the `catch` at `:82` only ever covers
`history.Append`. That is worth knowing, because it means the fold and the ledger can never
disagree *with each other*: they fail together.

### Important — the fold and beads CAN diverge unrepairably, in exactly the fields reconcile refuses to touch

Answering the question directly: yes. `Mirror` swallowing a failed append is self-healing **only
for fields beads owns**. `BacklogReconciler.WithLocalRunState` (`BacklogReconciler.cs:53-61`)
deliberately re-imposes the local `Station`, `Worktree`, `Attempts`, `LastError` and `SpentUsd` on
every correction, so reconcile can never restore them — beads does not have them.

Concrete sequence:

1. `Orchestrator.cs:262` — `run.Item = host.Update(run.Item with { Station = "check" })`.
2. Beads write succeeds (`--status`, `-p`, `--metadata` only; `Station` was never going to beads).
3. `history.Append` fails — disk full → `IOException` → caught, logged
   "…will be corrected at the next open".
4. The fold's `Station` is still `"implement"`. It will **never** be corrected: reconcile compares
   only the stripped projection, and `WithLocalRunState` copies the stale local value forward.
5. The factory is killed. Next open: `RequeueOrphans` → `Release` → `factory activate` →
   `FactoryHost.Activate` (`FactoryHost.cs:229-236`) resumes at `"implement"`, redoing the
   implement station on work that had already passed it.

The log line's promise ("will be corrected at the next open") is therefore true for state, title,
priority, criteria and dependencies, and false for the five local fields. Worth saying so in the
message, and worth deciding whether a failed audit append should be tolerated for
`WorkItemUpdated` at all, given `WorkItemUpdated` is the only event that carries local run state.

### Important — the caught exception set is too narrow: a permission-denied ledger halts the factory, violating D2's asymmetry

`:82` catches `IOException or ObjectDisposedException or InvalidOperationException`.
`JsonlRunHistory.Append` (`JsonlRunHistory.cs:36`) opens
`new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)`, which throws
**`UnauthorizedAccessException`** (a `SystemException`, *not* an `IOException`) when
`.factory/ledger.jsonl` is read-only, owned by another user, or on a mount that denies writes.
`FactoryJson.Write<FactoryEvent>` at `:35` can throw `JsonException`, also uncaught.

Either escapes `Mirror`, escapes `LedgerMirroringWorkItemStore`, and is converted by
`GuardedWorkItemStore.Guard` (`GuardedWorkItemStore.cs:31-34`) into a factory-halting
`WorkItemStoreException` — **after the beads write already committed**. That is precisely the case
D2 says must be tolerated: "a failed ledger write is tolerable and self-heals at reconcile". The
factory stops with a `WorkItemStoreException` naming the *backlog* provider for a fault that is
entirely local ledger I/O.

`ObjectDisposedException` is arguably too *wide* in the other direction — it means a mirror after
`FactoryHost.Dispose()` is silently dropped rather than surfacing a lifecycle bug — but that is a
much smaller concern than the missing `UnauthorizedAccessException`.

### Important — a failed mirror of `TryClaim` leaks the claim: the fold never goes InProgress, so nothing ever heartbeats it

`Orchestrator.RefreshClaimsAsync` (`Orchestrator.cs:213-219`) selects heartbeat targets from
`_s.State.Items` filtered to `State == InProgress`. If `TryClaim`'s mirror append fails (any of the
uncaught-or-caught paths above), the bead is `in_progress` with a 5-minute lease and the fold does
not know it, so **the item runs to completion with zero heartbeats**. The lease expires 5 minutes
in; any `Reclaim` — including the unscoped one at `FactoryHost.cs:124` on any checkout — then
reverts it to `open` and hands it to a second claimant while the first is still working. This is
the P8 failure the phase set out to fix, re-entering through the audit-copy path.

Making the heartbeat set the store's own claim bookkeeping rather than a fold query would close
both this and the wasted-subprocess issue in Area 5.

### Minor — `Release` is the only member that mirrors nothing for an item the fold does not know

`:45-49` guards on `if (StateOf(id) is { } from)`. `TryClaim` (`:41`) and `Reclaim` (`:55`) both
route through `MirrorChange`, which handles the unknown-item case by recording the item whole
(`:70-73`). `Release` has no such fallback, so releasing an item this checkout has not seen — work
another machine filed after this factory opened — leaves no ledger record at all until the next
reconcile. Reconcile does repair it, so this is cosmetic, but the asymmetry between three members
that all face the same situation reads as an oversight rather than a decision.

### Minor — the `from` on every mirrored `WorkItemStateChanged` can be stale, and nothing reads it

`MirrorChange` takes `from` from `StateOf(...)`, i.e. the fold. After any swallowed append the fold
is behind, so `from` records a state the item had already left. `grep` finds no reader of
`WorkItemStateChanged.From` anywhere in `src/` or `tests/` — `FactoryState.ApplyLocked` uses only
`c.To` and `c.At` (`FactoryState.cs:68-70`) — so today this only corrupts the audit trail a human
reads. Recording it because the field's whole purpose is forensics.

---

## Area 4 — `BacklogReconciler` (D1)

**"Beads wins, unconditionally" holds, and a correction cannot flow the wrong way.** `Reconcile`
(`BacklogReconciler.cs:11-32`) only ever calls `history.Append` and `state.Apply`; it never calls a
mutating member of `store`. There is no code path from reconcile back into beads.

**It cannot loop.** The comparison is `SharedState(known) == SharedState(authoritative)`
(`:20`), and the correction it emits is `WorkItemUpdated(WithLocalRunState(authoritative, known))`
(`:24`), which `FactoryState.ApplyLocked` applies by wholesale replacement. After one correction
`SharedState(known)` is by construction equal to `SharedState(authoritative)` — the projection
strips exactly the five fields `WithLocalRunState` re-imposes, plus `UpdatedAt`. I looked for the
usual culprits and found none: `CreatedAt` round-trips through metadata rather than being re-stamped
(`BeadMapper.cs:109`, `BeadMetadata.cs:19-22` — this was the fix in `1a2ec53` and it is the right
one), and `IsFullyDeterministic` is serialised but derived purely from `AcceptanceCriteria`, so it
cannot disagree independently.

**It cannot lose local run state**, for the same reason: `WithLocalRunState` (`:53-61`) copies all
five volatile fields from `known` before the correction is emitted. The projection membership is
right — `Station`, `Worktree`, `Attempts`, `LastError`, `SpentUsd`, `UpdatedAt` out; everything the
mapping carries in.

### Minor — an item deleted from beads is never removed from the fold

The loop is over `store.All()`, so a bead that no longer exists leaves its ledger-fold copy in
place indefinitely, where `factory ls` keeps showing it and `DependencySatisfiedLocked`
(`FactoryState.cs:109-110`) keeps treating its state as a real blocker. Given D1 makes beads the
authority for existence too, a deletion is a correction that never flows. `bd list --all
--limit 0` does include closed beads, so this only bites on genuine deletion — plausible when a
human prunes the shared backlog. Reporting rather than arguing for a tombstone mechanism.

### Minor — reconcile's own appends are not failure-tolerant, unlike every other ledger write in the phase

`history.Append(correction)` at `:26` is bare. A failure aborts `FactoryHost.Open`, so a
read-only ledger means the factory cannot start at all rather than starting degraded. Arguably
correct (fail loud at open), but it is the opposite of the tolerance `LedgerMirroringWorkItemStore`
applies to the identical call, and the two should agree deliberately rather than by accident.


---

# Phase 3 review — test quality and coding standards

STATUS: in progress

Reviewer: second-opinion reviewer. Scope: **test quality + coding standards**.
Functional correctness of the beads protocol is owned by `review-correctness.md`.

Range: `a0e536e..09cc08e` (8 commits) in `/Users/aczarnick/personal/repos/software-factory-phase3`.

---

## Gate re-run (my own, not the author's)

```
$ dotnet build --no-incremental
  ...DoctorCommandTests.cs(33,9): warning CA1416: ... File.SetUnixFileMode ... unsupported on: 'windows'
Build succeeded.
    1 Warning(s)
    0 Error(s)
Time Elapsed 00:00:03.11

$ dotnet test --no-build
Passed!  - Failed: 0, Passed: 282, Skipped: 0, Total: 282, Duration: 56 s
```

Warning count and test count both match the ledger exactly. **PASS.**

---

## 3. Standards

### Dependency rule / plugin ABI — PASS

`src/Factory.Core/Factory.Core.csproj` has no `PropertyGroup` beyond TFM/nullable/usings and
**zero** `ProjectReference`/`PackageReference` (`grep -cE 'ProjectReference|PackageReference'` -> 0).
`grep -riE 'beads|\bbd\b|dolt' src/Factory.Core/` -> no hits. The only Core changes in this phase
are `Ids.cs`, `Priorities.cs`, `ProviderRef.cs`, `WorkItem.cs` — all policy, no detail. The beads
adapter lives entirely in `Factory.Runtime/Providers/Beads/`. Dependencies point inward. Confirmed
directly, not taken from the ledger.

### One top-level type per file — PASS

All 13 new production files declare exactly one top-level type (mechanically counted). The four
multi-type files touched in the diff (`WorkItem.cs`, `Orchestrator.cs`, `PipelineStations.cs`,
`EvolutionLoop.cs`) were already multi-type before this phase; the diff does not add types to them.

Test files bundle several `public class` fixtures per file (`CoreTests.cs` now holds `IdFormatTests`
and `PriorityBandTests`; `EvolutionTests.cs` holds `EvolutionImprovementTests`) — consistent with
the pre-existing convention in this suite, and the rule is about production types.

### XML docs / comments — PASS with one note

`<summary>` appears on public surface only, and the doc text is unusually good: it records *why* a
flag was chosen and what breaks silently without it (`BeadMapper.AllArgs`, `ClaimArgs`,
`ReleaseArgs`, `BeadRecord.Revision`, `BeadMetadata.CreatedAt`). Private helpers use bare `//`
comments that carry a reason rather than narration (`BeadMapper.Blockers`,
`BacklogReconciler.StripLocalRunState`, `BeadsCli.Captured`). No step-by-step narration found.

Note: `BeadMapper`, `BeadsCli`, `BeadsWorkItemStore` have several undocumented public members
(`StatusFor`, `StateFor`, `TypeFor`, `KindFor`, `CustomTypes`, `Json`, `Update`, `Transition`,
`Get`, `All`, `TryClaim`). Names carry them and `GenerateDocumentationFile` is off, so this is
consistent rather than a gap. Not a finding.

Findings for the rest of section 3 are in the numbered list below.
