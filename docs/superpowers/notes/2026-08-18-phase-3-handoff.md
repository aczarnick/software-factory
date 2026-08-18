# Storage Ports: Phase 3 Handoff

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

## Repository state

`master` is unchanged at `a0e536e` and the main checkout is still on it and clean — the factory was
stopped for this phase and no test ever wrote to the repository's `.beads` or `.factory`. The
worktree at `../software-factory-phase3` holds `storage-ports-phase-3`, which is `master` plus seven
commits and nothing else; there is no divergence to untangle this time, unlike phase 2. Nothing has
been pushed and `master` remains local-only.
