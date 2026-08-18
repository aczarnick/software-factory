# SDD ledger — plan: docs/superpowers/plans/2026-08-13-storage-ports-phase-3-beads.md

Spec: docs/superpowers/specs/2026-08-13-storage-adapters-design.md (read; binding authority).
Baseline at start: 203 tests passing; 1 pre-existing CA1416 (DoctorCommandTests.cs:33) with
`dotnet build --no-incremental`. bd 1.2.1 on PATH.

Pre-flight rulings are recorded in docs/superpowers/notes/2026-08-18-phase-3-preflight.md
(commit 76a18d4) and summarised as P1-P13 below. Every one was probe-proven.

## Cross-task conflict scan

| Rows | Produces / Consumes | Finding |
|---|---|---|
| T1 -> T4 | T1 produces `Shell.Run(file,args,cwd,env,timeout)`; T4's `BeadsCli.Exec` consumes it with `(string, string[], string, Dictionary)` | Agrees. But T1's body uses `stdout.Result` after `WaitForExit(ms)`, which ignores the timeout when any grandchild holds the pipe — probed at 8.0s for a 1s-exit command. Ruling P14 below. |
| T2 -> T3 | T2 produces `Ids.New` = `prefix-hash` and `Priority` default 2; T3's `CreateArgs` consumes `item.Id` via `--id` and `-p` | Agrees. bd rejects `wi_...` and rejects `-p` outside 0-4, so T2 is a hard prerequisite of T3/T4. |
| T2 self | narrows Priority to 0-4; Step 5 says find every other range assumption | Two sites break: PipelineStations.cs:281 (`+50`) and EvolutionLoop.cs:150 (`=200`). bd exits 1 on both. Ruling P9. |
| T3 self | test assigns `MetadataFor(item)` (string) to `BeadRecord.Metadata` (`JsonElement?`) | Does NOT compile — CS0029, probe-proven. Ruling P1. |
| T3 -> T4/T5 | T3 produces `ToWorkItem`; T4 `Get`/`All` and T5 reconcile consume it | `ToWorkItem` never maps `DependsOn`, so reconcile would erase dependency edges from the ledger fold. Ruling P4. |
| T3 self | `MetadataFor` excludes volatile state; its test asserts absence only | Test cannot redden by deleting logic — it guards against future additions, not present behaviour. Accepted as a guard; the mutation check for T3 is the round-trip test instead. |
| T4 self | `TryClaim(claimant)` never uses its parameter | Assignee would come from git user.name/$USER. Ruling P2. |
| T4 self | `Json<T>` throws on `!Ok`; `Get` must return `WorkItem?` | `bd show <missing>` exits 1 -> Get throws -> GuardedWorkItemStore halts the factory. Ruling P5. |
| T4 self | `Release` uses `bd unclaim` | Fails ("no matching row", exit 1) unless the item is `in_progress`. Ruling P6. |
| T4 self | `Reclaim` lists `--status open --assignee owner` after reclaim | reclaim CLEARS the assignee, so the filter is empty by construction; `reclaim --json` is an object. Ruling P7. |
| T4 self | `All()` uses `bd list --all` | `-n/--limit` defaults to 50 — silent truncation. Ruling P3. |
| T4 -> T5 | T4 produces `BeadsWorkItemStore(BeadsCli, string owner)`; T5 registers `new BeadsWorkItemStore(cli, config.Name)` | Agrees. |
| T5 self | heartbeat placed in the orchestrator poll loop | The loop exits at `started == MaxItems` and the item then runs inside the final `Task.WhenAll` drain — zero heartbeats for the whole run. Ruling P8. |
| T5 self | reconcile skips when `local.State == authoritative.State` | Every other authoritative field never heals. Ruling P13. |
| T5 self | `EnsureInitialised` guards on `cli.Exec("info").Ok` and runs from `FactoryHost.Open` | `bd init` writes AGENTS.md/CLAUDE.md/.cursor/ and appends .gitignore. Ruling P10. |
| T5 -> T6 | T5 registers "beads"; T6 exercises it end to end | Agrees; default provider stays `ledger`, so T6 Step 4 must edit config to select beads. |
| T6 self | `grep -c "ProjectReference\|PackageReference" src/Factory.Core/Factory.Core.csproj` expects 0 | Agrees today. Note: T4 adds a package to the TEST csproj only, which does not affect this. Ruling P12 removes that package anyway. |

Ruling: P1-P13 as recorded in the pre-flight note; carried-forward RequeueOrphans routes
through `Release` with the redundant `Update` dropped.

Ruling: P14 — Task 1's `Shell.Run` must bound its output drain the way `ExecCoreAsync`
already documents (`DrainGrace`), because `stdout.Result` after `WaitForExit(ms)` waits on
pipe EOF, not on process exit: probed at 8.0s for a command that exited immediately while a
grandchild held the pipe. The spec records that beads auto-starts a `dolt sql-server` per
project, so this is the exact shape of hang that would freeze a station. Cost if wrong: a
slightly more complex Run and one extra ~2s test.

## Task log

Task 1: implementer dispatched (sonnet) with the P14 correction. Reproduced the defect first —
the brief's `stdout.Result` version failed the new grandchild test at 8.0s — then implemented the
bounded drain reusing the existing `DrainGrace` and `ReadAsync`. Commit b6182fd. 208 passed
(203 + 5), build 1 warning (the pre-existing CA1416). Report:
`task-1-report.md`. Task reviewer dispatched with review-76a18d4..b6182fd.diff.
Task 1: first reviewer (review-task1) ran the mutation cycles — observed applying the naive
`stdout.Result` version, then restoring the tree — but went idle without writing its report file
or delivering a verdict. This is the phase-2 reviewer failure mode repeating.
Ruling: dispatch a fresh reviewer (review-task1b) required to create its report file as its FIRST
action and append after every individual check, and to skip the already-evidenced grandchild
mutation (the implementer proved it reddens at 8.0s). Cost if wrong: one duplicated review cycle.
Task 1: review-task1b delivered mutations and constraints, then stalled before the quality
section. Evidence obtained: 5/5 mutations reddened (timeout flag, environment merge, exit-code
propagation, stdout capture — each RED with quoted assertion output; grandchild/bounded-drain
reddened by the implementer at 8.0s). Constraints PASS: Factory.Core has zero Project/Package
References, `dotnet build --no-incremental` = exactly 1 warning (the pre-existing CA1416),
global.json still pins 10.0.400.

Ruling: close Task 1 on that evidence and carry the unfinished quality reading to the final
whole-branch review rather than spending a third reviewer on one method. Two reviewers have now
stalled mid-review; Task 1's surface is a single method in a single file, and the final review
sees it again. Cost if wrong: a Task 1 quality defect surfaces at the final review instead of now.

I read `Shell.Run` myself against the four risks I had raised, since I raised them:
- Timeout path DOES preserve output captured before the kill (`stdout.ToString()`, not "") —
  better than the brief, which discarded it.
- The StringBuilder read is race-free on both paths: `cts.Cancel()` then
  `Task.WhenAll(...).Wait()` precedes every `ToString()`, and `ReadAsync` catches
  OperationCanceled/IOException so the readers complete rather than fault. This is stricter than
  the existing async path, which reads its builders without awaiting the readers at all.
- `.Wait()` cannot deadlock here: no SynchronizationContext in this host and `ReadAsync` uses
  ConfigureAwait(false).

Task 1: minor (deferred): the exception filter `ex is IOException or InvalidOperationException or
SystemException` reduces to `SystemException`, since the first two derive from it. Copied verbatim
from `Which()` in the same file, so it is consistent noise rather than new noise.

Task 1: minor (deferred): the post-cancel `Task.WhenAll(outTask, errTask).Wait()` has no timeout,
so it relies on a pending pipe read honouring cancellation. Empirically it does on this platform —
the grandchild test returns in ~3s against an 8s pipe holder, which is the direct evidence — but a
bounded `Wait(timeout)` would not depend on that, at the cost of reintroducing the builder race
unless handled carefully. Flag for the final review.

Task 1: Ruling: carried into Task 4 — `ReadAsync` bounds retained output at 64,000 chars, and
`Shell.Run` inherits that. Task 4's `All()` uses `bd list --limit 0 --json`, whose output scales
with the backlog: measured ~600 bytes per issue, so ~105 issues would silently truncate the JSON
mid-document and fail to deserialise. 23 items today is ~14KB, so this is not urgent, but `All()`
must not fail silently at the cap. Task 4 must detect a truncated read rather than treat it as a
parse error of unknown origin. Cost if wrong: a backlog that grows past ~100 items breaks `All()`
and reconcile with a confusing JSON error.

Task 1: complete (commits 76a18d4..b6182fd, spec PASS, 5/5 mutations reddened, 2 minors deferred)

Ruling: the subagent channel became unreliable after Task 1, so tasks 2-6 are implemented directly
in the controller session. Evidence: of five dispatches, only Task 1's implementer delivered.
review-task1 ran its mutation cycles (observed applying and reverting mutations in the worktree)
then went idle without writing its file or replying; review-task1b delivered mutations and
constraints incrementally then stalled before its quality section; two successive Task 2
implementers produced no file edits at all in 20 and 8 minutes respectively and did not answer
status pings. Stopping the stale agents to free capacity did not revive Task 2.

The user asked for subagent-driven development, but the deliverable is phase 3 implemented and
verified, and feeding 20-minute windows to agents that do not return would consume the session and
deliver nothing. Every other discipline the user demanded is kept unchanged: TDD, one commit per
task, mutation-checking every new test by deleting the logic it names, the full
`dotnet build --no-incremental && dotnet test` gate with real pasted output, and this ledger. I
will attempt one subagent for the final whole-branch review, where independent eyes are worth the
most, and will not block on it.

Cost if wrong: the per-task independent review seat is lost for tasks 2-6, so a defect that a
second reader would have caught survives to the final review. Mitigation: mutation-check every
test, and state plainly in the completion report which reviews were self-performed.

Task 2: implemented directly. `Ids.New` now emits `prefix-hash`; `WorkItem.Priority` defaults to 2.
The band was a magic number at three sites (default 100, `+ 50`, `= 200`), so it is named once in a
new `src/Factory.Core/Priorities.cs` (`Highest`/`Default`/`Lowest`, plus `Below(int)` which clamps).
`PipelineStations` review follow-ups use `Priorities.Below(ctx.Item.Priority)` and `EvolutionLoop`
uses `Priorities.Lowest`. The existing `Dispatchable_orders_by_priority_then_age` values 500/10
became `Lowest`/`Highest` without changing what it asserts.

Ruling: introduce `Priorities` rather than inline `Math.Min(4, x + 1)` at the two sites. Craft, not
architecture — it removes three magic numbers of the same band, gives the clamp an
intention-revealing name, and makes the rule unit-testable without a model call. One new file, one
new concept, no new coupling. Cost if wrong: one small type to delete and two call sites to inline.

Mutation checks — all five redden:
- `Ids.New` separator back to `_` -> `New_emits_a_beads_compatible_identifier` RED (Assert.StartsWith).
- `Priority` default back to 100 -> `Work_items_default_to_the_middle_priority_band` RED (Assert.Equal).
- `Below` clamp removed (`priority + 1`) -> `Below_never_leaves_the_band_for_already_lowest_work`
  and the `InRange` theory case `priority: 4` both RED.
- review follow-up stops stepping down (`Priority = ctx.Item.Priority`) ->
  `Work_agents_file_about_their_own_observations_lands_as_a_proposal` RED. This is the check that
  proves the production branch in `PipelineStations` is reachable from the suite, not just the
  helper.
- evolution items file at `Highest` -> initially **GREEN across all 217 tests**: the whole suite
  never reached `EvolutionLoop`'s improvement path, exactly the unreachable-production-branch trap
  from phase 2. Closed by adding `EvolutionImprovementTests`, which drives `RunStationAsync` with a
  scripted `evolve` response and asserts `ImprovementItems[0].Priority`. The same mutation is now
  RED (Assert.Equal).

Process note for the remaining tasks: `git checkout -- <file>` to undo a mutation also discards
uncommitted implementation work, which it did here (silently reverting the `Ids.cs` and
`WorkItem.cs` edits mid-mutation and producing a confusing extra red). Commit the task first, then
mutate, then checkout.

Task 2: complete (commit 5957ea9, 218 passed, 1 pre-existing warning, 5/5 mutations reddened)

Task 3: implemented directly. Four new types under `src/Factory.Runtime/Providers/Beads/`:
`BeadRecord`, `BeadDependency`, `BeadMetadata`, `BeadMapper` (one top-level type per file).

Corrections applied against the plan, as ruled in pre-flight:
- P1: the plan's test assigned `MetadataFor(item)` (string) to `BeadRecord.Metadata`
  (`JsonElement?`) and could not compile. `MetadataFor` still returns the string `bd --metadata`
  needs; the test parses it via `JsonDocument.Parse(...).RootElement.Clone()` in one private helper.
- P4: `BeadRecord.Dependencies` added and `ToWorkItem` maps their ids into `DependsOn`. Without it
  reconcile-on-open would have erased dependency edges from the ledger fold on every open.

Ruling: `TypeFor` throws on an unmapped kind instead of the plan's `_ => "task"` fallback. A silent
fallback would file a new `WorkItemKind` as a plain task and lose the distinction in the shared
backlog, and the round-trip test over all six kinds could not detect it. `KindFor` keeps a
permissive default because beads can legitimately hold types the factory did not create (`epic`,
`decision`, gates), and refusing to read those would make `All()` throw on a foreign bead. Cost if
wrong: adding a kind without extending the mapper fails loudly at the write instead of silently
mis-filing.

Beyond the plan's five tests, added: kind round-trip over all six kinds; the structured remainder
(intent, requirements, assumptions, labels, parentId) round-trip; native fields read from the bead
rather than metadata; a bead with no metadata at all; `UpdateArgs` carries the mapped status; an
unmapped status is refused rather than guessed; `CreateArgs` declares every dependency; and two
fixture tests over JSON captured verbatim from real `bd show --json`.

Ruling: add those two real-output fixture tests. Every other test builds `BeadRecord` by hand, which
agrees with any misspelling of a `[JsonPropertyName]` — and `FactoryJson` uses a camelCase policy,
so `issue_type`, `acceptance_criteria`, `lease_expires_at` and `dependency_type` only bind because
of those attributes. Mutation M3 proves the gap was real. Cost if wrong: two literals to refresh if
beads changes its output shape, which is exactly when they should fail.

Mutation checks — 5/5 redden:
- `DependsOn` mapping removed -> both dependency tests RED.
- `Criteria` dropped from the metadata written -> round-trip test RED.
- `[JsonPropertyName("issue_type")]` misspelled as `issueType` -> only the real-output fixture test
  RED; every hand-built test stayed green, which is the whole reason that fixture exists.
- `Draft` mapped to `open` (would make proposals claimable) -> state round-trip RED.
- `CreateArgs` stops emitting `--deps` -> dependency-declaration test RED.

Task 3: complete (commit 2cfbc94, 233 passed, 1 pre-existing warning, 5/5 mutations reddened)

Task 4: implemented directly. New: `BeadsCli`, `BeadsWorkItemStore`, `BeadsReclaimResponse`,
`ReclaimedLease`; `BeadMapper` gained the query-argument builders; `Shell` gained
`MaxCapturedOutputChars`.

Ruling: put the bd query flags in `BeadMapper` as pure arg builders (`AllArgs`, `GetArgs`,
`ClaimArgs`, `ReleaseArgs`, `ReclaimArgs`) rather than inline in the store. Every one of them
encodes a probe-derived flag whose regression is silent, not loud — a missing `--limit 0` truncates
a backlog at 50 with no error, a missing `--actor` assigns work to the wrong identity — and as pure
functions they are pinned by fast unit tests instead of needing a 5-minute lease or a 51-item
fixture. `BeadMapper` already owned "how a WorkItem becomes bd arguments", so this is the same
responsibility, not a new one. Trivial one-liners (`sync`, `note`, `heartbeat`) stayed inline.
Cost if wrong: five small functions to inline back into the store.

Ruling: `Get` uses `bd list --id <id> --all --limit 0` rather than `bd show`. Probed: `bd show` on
an unknown id exits 1, which `BeadsCli.Json` turns into a throw and `GuardedWorkItemStore` turns
into a factory-halting `WorkItemStoreException`, while `list --id` exits 0 with `[]` — a
"not found" that is distinguishable from a broken database without matching on stderr text.
`--all` is required or a closed or draft bead reads as missing (probed). Cost if wrong: `Get`
returns null for a database error it should have raised — mitigated because a genuinely failing
`bd` still exits non-zero and still throws.

Ruling: `BeadsCli` refuses a capture that reached `Shell.MaxCapturedOutputChars` instead of letting
it fail as malformed JSON. This is the Task 1 carried ruling about the 64,000-character retention
bound; at roughly 600 bytes per bead, `All()` would hit it near 105 items. Cost if wrong: an extra
guard that can only fire on an oversized backlog.

Deviation from the plan worth recording: the plan's `BeadsCli.Json<T>` was the only JSON entry
point, but `bd reclaim --json` returns a summary object rather than a list, so `JsonObject<T>` was
added alongside it.

Mutation checks — 5/5 redden, and each corresponds to a pre-flight defect:
- `AllArgs` loses `--limit 0` -> `Reading_the_whole_backlog_defeats_the_default_page_size` RED (P3).
- `ClaimArgs` loses `--actor` -> the arg test AND the real-bd
  `TryClaim_marks_the_item_in_progress_and_assigns_it_to_the_named_owner` both RED, which proves
  against a live database that the assignee really does fall back without it (P2).
- `ReleaseArgs` reverts to `bd unclaim` -> two arg tests plus the real-bd
  `Release_requeues_an_item_that_a_station_has_already_moved_past_in_progress` RED (P6).
- `GetArgs` reverts to `bd show` -> the arg test plus the real-bd
  `Get_returns_null_for_an_id_the_backlog_does_not_know` RED (P5).
- the retention bound removed -> `Run_bounds_how_much_output_it_retains` RED.

Defect the plan did not know, found by a failing test rather than by reading: **beads reports
dependencies in two different shapes.** `bd show --json` embeds the blocking issue
(`{"id": ..., "dependency_type": "blocks"}`), while `bd list --json` reports the edge
(`{"issue_id": ..., "depends_on_id": ..., "type": "blocks"}`). My pre-flight had only ever captured
the `show` shape on a bead that had dependencies, and inferred `list` matched it — exactly the
inference the probe discipline exists to prevent. `Add_then_Get_round_trips_dependencies` failed and
exposed it. `BeadDependency` now accepts both and exposes one `BlockerId`, with a self-id guard for
the reversed edge, and both real shapes are pinned as captured fixtures in a theory.

Task 4: known gap, recorded rather than papered over: `Reclaim`'s resolution of reclaimed ids back
into `WorkItem`s is not exercised by the suite, because producing a genuinely stale lease requires
waiting out the fixed 5-minute TTL and no config key shortens it. The response parsing is unit
tested from captured real JSON, `Get` is heavily tested, and the live behaviour was probe-verified
by hand (`{"count":1,"reclaimed":[{"id":"wi-aaaa11112222","previous_owner":"node-a"}]}`, after which
the bead read back as open with no assignee and no lease). Phase 4 exercises reclaim for real.

Task 4: minor (deferred): `BeadsWorkItemStore.Release` reads the item first only to decide whether
to no-op, costing an extra bd call on every release.

Task 4: complete (commit a8e65ad, 262 passed, 1 pre-existing warning, 5/5 mutations reddened)

Task 5: implemented directly. New: `BacklogReconciler`, `BeadsDeployment`, `Leases`,
`LedgerMirroringWorkItemStore`. `FactoryHost.Open` registers "beads", wraps the resolved store in
the audit mirror, then syncs, reconciles and reclaims. `Orchestrator` refreshes claims from a
`HeartbeatTimer` and requeues orphans through `Release`.

**Defect the plan never addressed, found by a failing test: there was no audit copy at all.**
`LedgerWorkItemStore` records every write as a ledger event, but `BeadsWorkItemStore` only writes to
beads — so with the beads provider selected, nothing reached the ledger or the fold during a
session. `State.Items` stayed empty, which silently breaks everything that reads the fold:
`factory ls`, dependency queries, `InFlight()` (so orphan requeue), the heartbeat's in-progress
scan, and the budget. Reconcile-on-open would have papered over it at the *next* open, which is why
this is invisible to reasoning about a single write. This is spec decisions D1 ("the ledger keeps an
audit copy") and D2 ("write order is beads first, ledger second") — neither was implemented by the
plan. All three of my integration tests failed on the first run because of it.

Ruling: implement it as `LedgerMirroringWorkItemStore`, a decorator applied in `FactoryHost` to any
store that is not the built-in `LedgerWorkItemStore`. The backlog write goes first and its failure
propagates; the ledger append that follows is caught and logged, which is exactly D2's asymmetry
("a failed beads write aborts the transition; a failed ledger write is tolerable and self-heals at
reconcile"). A decorator rather than teaching `BeadsWorkItemStore` about the ledger: the audit copy
is a property of the composition, not of beads, so any third-party provider gets it for free and the
adapter stays a pure beads adapter. The ledger provider is excluded by type test, not by name, since
its own writes already are the audit copy. Cost if wrong: one decorator to remove, and the exclusion
is the one place a future event-free ledger store would simplify.

Ruling: reconcile compares a serialised projection of the item with volatile fields stripped, rather
than a hand-listed field set. Records compare `IReadOnlyList` members by reference, so a
field-by-field comparison needs `SequenceEqual` on five collections and silently under-compares the
moment a field is added to the mapping; serialising compares everything beads owns and extends
itself. Cost if wrong: one serialisation per item per open — 23 items today.

Ruling: `Leases` names the measured 5-minute lease and the TTL/3 refresh cadence in one place, and
`OrchestratorOptions.LeaseRefreshInterval` makes it injectable. Without the knob the cadence is
minutes and no test could observe a refresh inside a run that lasts milliseconds — the P8 defect
would have stayed unprovable, which is how it survived the plan. It sits beside the existing
`PollInterval` knob. Cost if wrong: one option nobody but tests sets.

Second defect found by a failing test: `ToWorkItem` never read `created_at`, so every read produced
a fresh `CreatedAt`. Two consequences — `Dispatchable()` breaks priority ties on `CreatedAt`, so the
queue would reshuffle on every read, and reconcile's comparison would see every item as changed and
append a correction for the entire backlog on every open. `BeadRecord` now carries `created_at`,
`updated_at` and `heartbeat_at`, and `Reconciling_a_real_backlog_twice_writes_nothing_the_second_time`
fails against the old behaviour.

Mutation checks — 6/6 redden, every one against a real beads database where relevant:
- lease refresh moved back into the poll loop, exactly as the plan specified ->
  `A_claim_is_refreshed_while_its_station_works` RED with `heartbeat_at` equal to `started_at` to the
  tick, i.e. the claim was never refreshed once during the entire run (P8 proven, not argued).
- `RequeueOrphans` reverted to `Transition` + `Update` -> `An_orphan_is_requeued_...` RED with
  "keeps its assignee ... was 'test-machine'" — the orphan kept the claim no other machine could take.
- reconcile compares only `State` -> all four `The_store_wins_for_every_field_it_owns` cases RED.
- reconcile writes `WorkItemUpdated(authoritative)` -> `A_correction_keeps_the_local_run_state` RED.
- the audit mirror removed -> all three beads-backed integration tests RED.
- `--init-if-missing` dropped -> `Deploying_twice_is_not_an_error` and
  `Deploying_keeps_work_that_is_already_filed` RED.

Task 5: minor (deferred): `RefreshClaimsAsync` returns `Task.CompletedTask` because `HeartbeatTimer`
takes a `Func<Task>` while `IWorkItemStore.Heartbeat` is synchronous.

Task 5: complete (commit 13c32fe, 280 passed, 1 pre-existing warning, 6/6 mutations reddened)

Task 6: the verification gate, plus two defects it exposed that no unit test could have.

- Step 1: `grep -c "ProjectReference\|PackageReference" src/Factory.Core/Factory.Core.csproj` -> 0.
- Step 2: worktree clean, no `.beads` and no `.factory` in either checkout, `AGENTS.md`/`CLAUDE.md`
  unmodified. The main checkout is still clean and still on a0e536e.
- Step 3: `dotnet build --no-incremental` -> Build succeeded, 1 Warning (the pre-existing CA1416),
  0 Errors. `dotnet test` -> 282 passed, 0 failed.
- Step 4: end to end in a scratch repo, `factory add` then `factory ls` then `bd list`.

**Defect 1, found only by running it: `ProviderRef.Options` deserialised to null.** `factory add`
failed with `Work item store 'beads' failed during Create: Value cannot be null. (Parameter
'dictionary')`. `[method: JsonConstructor]` binds the primary constructor, so a config entry of
`{"provider":"beads"}` — exactly what the spec's example and `factory init` write — left `Options`
null, and the first provider to read its own options threw. A latent phase-2 ABI defect that only a
provider *with* options could surface. Fixed in `Factory.Core` by normalising the member to an empty
dictionary, so no provider has to guard. Mutation: reverting the normalisation reddens
`A_provider_named_with_no_options_still_has_an_empty_option_set`.

**Defect 2, found by reading the end-to-end output rather than the assertions: reconcile churned on
every open.** `factory ls` printed "reconciled 1 item(s)" against a backlog nothing had changed. The
cause was not precision, as I first assumed and half-fixed: beads stamps its own `created_at` at
write time, so it can never equal the moment the factory constructed the item. `CreatedAt` now
travels in the bead's metadata and is preferred on read, falling back to the bead's own stamp for
work another tool filed, so it round-trips exactly. This matters beyond noise — `Dispatchable()`
breaks priority ties on `CreatedAt`, so a value reassigned on every read reorders the queue.

Worth recording as a lesson: my own integration test
`Reconciling_a_real_backlog_twice_writes_nothing_the_second_time` **passed throughout**, because it
compared a beads read against a beads read. The divergence only exists between a locally filed item
and its beads copy. `Reopening_an_unchanged_backlog_reports_no_corrections` is the test that
reproduces it, and it was written red first.

Task 6: complete (commit 1a2ec53, 282 passed, 1 pre-existing warning)

Final whole-branch review: **not obtained.** Dispatched on the most capable model with the
incremental-write contract; it wrote only its orientation section in 20 minutes, did not answer a
status ping, and was stopped. That is the fourth stalled subagent this session out of six dispatches.

Ruling: stop spending the session on the delegation channel and close out with the checks I can run
myself, stating plainly which reviews were self-performed. Cost if wrong: no independent reader ever
saw this branch, so a defect that only fresh eyes would catch is still in it. The branch is
deliberately left unmerged and unpushed so that review can happen before it lands.

Residual checks I ran directly instead:
- `Factory.Core` contains no reference to beads or bd (grep) and still has zero Project/Package
  references — the dependency rule holds and the plugin ABI is unchanged.
- Every one of the 13 new production files declares exactly one top-level type.
- The suite is stable across repeated runs: 282, 282, and 38 for the beads subset. That is the
  evidence for the shared-`bd`-fixture isolation, which was the sharpest question about these tests —
  a `DrainReadyQueue()` that did not actually isolate would show up as an order-dependent failure.
- `RefreshClaimsAsync` reads `FactoryState.Items`, which returns a snapshot taken under the state's
  own lock, so the timer thread cannot observe a torn view while stations write. Verified by reading
  `FactoryState`, not assumed.

Not verified, and recorded as such: the `bd`-absent early-return guard in the beads tests. I could
not hide `bd` from `Shell.Which`, which resolves through `/bin/sh -c "command -v bd"` and picks up
the login shell's PATH. The 38 beads tests demonstrably executed for real here — the Task 4 and
Task 5 mutations reddened them against a live database — but the claim that a machine without `bd`
stays green is untested.

Phase 3: complete. 8 commits a0e536e..09cc08e, 282 passing, 1 pre-existing warning. Not merged,
not pushed.

Correction to the record: both Task 1 reviewers **did** deliver. Their completion messages arrived
long after they went idle and after I had stopped polling, and `task-1-review.md` (10KB, full
mutation table plus the deeper-verification sections) is on disk. So the earlier ledger entry saying
review-task1 "went idle without writing its report file" is wrong — it was late, not lost, and my
ruling to dispatch review-task1b duplicated a review that was already coming. The delegation channel
here is slow and its replies arrive out of band, not silently dead; that is a better model of the
failure than the one I recorded.

Task 1 therefore has two independent verdicts, both SPEC: PASS, QUALITY: APPROVED, 0 Critical,
0 Important, 1 Minor, with 5/5 and 4/4 mutations reddened. review-task1 independently reproduced the
8.0s grandchild hang against the brief's original code and confirmed the fix greens it — the P14
ruling is now confirmed by a reader who was not its author.

Their single Minor is the redundant exception filter at `Shell.cs:85`, which this ledger had already
recorded as a deferred minor at the same location and for the same reason (copied verbatim from
`Which()` at `Shell.cs:135`, consistent noise rather than new noise). Independent agreement, no new
work. Both reviewers left the tree clean; confirmed.

Standing: tasks 2-6 and the whole-branch review remain self-reviewed. Task 1 is independently
reviewed and approved.
