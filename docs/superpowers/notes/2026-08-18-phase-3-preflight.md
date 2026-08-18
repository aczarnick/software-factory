# Phase 3 Pre-Flight: Plan Defects and Rulings

Run before Task 1 against the real code and a real `bd` 1.2.1, on the pattern that caught the
phase-1 and phase-2 plan defects. Every finding below was **probed, not reasoned about**. Rulings
are binding on the tasks that follow.

Baseline at pre-flight: 203 tests passing, one pre-existing CA1416 in `DoctorCommandTests.cs`
(`dotnet build --no-incremental`).

## Probe evidence

Throwaway `bd init --prefix wi` database in a temp directory, `BD_NON_INTERACTIVE=1`, custom
statuses and types installed as the spec specifies.

| Probe | Result |
|---|---|
| `bd create --json` | returns a JSON **object**; `show`, `list`, `ready --claim` return **arrays** |
| `bd ready --claim --json --actor X` | sets `assignee: X` — `--actor` is the only lever; `-a/--assignee` on `ready` is a *filter* |
| `bd list --json` | carries `metadata` **and** a `dependencies[]` array of full blocker objects with `dependency_type` |
| `bd list` | `-n/--limit` defaults to **50** |
| `bd update -s <status>` | exists; `bd create` has **no** status flag |
| `bd update <id> -s open --assignee ""` | clears assignee, status open, lease dropped |
| `bd update <id> -s in_review` | **drops the lease** (`lease_expires_at`/`heartbeat_at` → null), keeps assignee |
| `bd heartbeat` on an `in_review` item | **exit 1** — "issue not claimable: status in_review" |
| `bd heartbeat` as a non-owner | exit 1 — "issue already claimed by <owner>" |
| `bd unclaim` on an item not `in_progress` | **exit 1** — "no matching row" |
| `bd show <missing> --json` | **exit 1** |
| `bd create -p 5` / `-p 250` | **exit 1** — "invalid priority (expected 0-4 or P0-P4)" |
| `bd reclaim --json` | object: `{count, reclaimed:[{id, previous_owner}], schema_version, scoped}` |
| lease TTL | 5 min from the last heartbeat, measured; no config key in `bd config list` |
| after reclaim | status `open`, **assignee cleared**, lease dropped |
| `bd init` | writes `AGENTS.md`, `CLAUDE.md`, `.claude/`, `.cursor/`, `.codex/`, `.agents/` and appends to `.gitignore`; `--init-if-missing` makes it idempotent |
| lingering processes | none — no `dolt sql-server` left holding pipes after a `bd` call |

## Rulings

### P1 — Task 3's test cannot compile

`Metadata = BeadMapper.MetadataFor(item)` assigns a `string` to `JsonElement?`. Proven with a
throwaway probe project: `error CS0029: Cannot implicitly convert type 'string' to
'System.Text.Json.JsonElement?'`.

**Ruling:** `MetadataFor` keeps returning `string` — `--metadata` needs a string. The test builds the
element with `JsonDocument.Parse(json).RootElement.Clone()`. The `Clone()` matters: the
`JsonDocument` is disposed while the element is still read.

### P2 — `TryClaim(owner)` ignores `owner`

The plan's body never uses its parameter, so the assignee would come from `$BEADS_ACTOR` / `git
user.name` / `$USER` — the human, not the checkout, which is the opposite of what the spec asks for.
Every identity-keyed operation downstream (`reclaim -a`, `unclaim`, foreign-orphan reporting) then
keys off the wrong value.

**Ruling:** every mutating `bd` call passes `--actor <owner>`, threaded from
`BeadsWorkItemStore(cli, owner)`. Test it by asserting the claimed bead's assignee.

### P3 — `All()` silently truncates at 50 items

**Ruling:** `bd list --all --limit 0 --json`. A backlog larger than 50 would otherwise reconcile
only its first 50 and `factory ls` would under-report.

### P4 — the mapper drops `DependsOn`

`ToWorkItem` never sets it, and reconcile-on-open writes `WorkItemUpdated(authoritative)` into the
ledger — so every open would erase dependency edges from the local fold. `list`/`show --json` do
carry `dependencies[]`.

**Ruling:** `BeadRecord` gains `dependencies`; `ToWorkItem` maps their ids into `DependsOn`.

### P5 — `Get(missing)` throws instead of returning null

`bd show` exits 1 for an unknown id and `BeadsCli.Json` throws on `!Ok`; `GuardedWorkItemStore`
then converts that into a factory-halting `WorkItemStoreException`. The port declares `WorkItem?`
and `LedgerWorkItemStore` returns null.

**Ruling:** `Get` returns null when the item is absent, and only genuine failures throw.

### P6 — `Release` via `bd unclaim` fails unless the item is `in_progress`

Orphans are requeued from `InReview` too, and the port contract says `Release` must reach Ready.

**Ruling:** `Release` is `bd update <id> -s open --assignee "" --actor <owner>` plus the note —
probed to clear status, assignee and lease together.

### P7 — `Reclaim` can never return anything

`bd reclaim` clears the assignee, so the plan's follow-up `list --status open --assignee <owner>`
is empty by construction. `reclaim --json` is also an object, not an array of beads.

**Ruling:** parse `{count, reclaimed:[{id, previous_owner}]}` and `Get` each id. Emit the grace
window in seconds (`$"{(long)olderThan.TotalSeconds}s"`), not truncated minutes. Reclaiming a
genuinely expired lease costs a 5-minute wait, so the suite tests the response *parsing*; the live
behaviour is evidenced by the probe above.

### P8 — heartbeating in the poll loop never covers the run

The loop exits the moment `started == MaxItems`, and the item then runs to completion inside the
final `Task.WhenAll(running)` drain — so with `MaxItems = 1` an entire implement run gets **zero**
heartbeats, which is exactly the 5-minute-lease failure the spec calls mandatory to prevent.
Separately, an item moved to `in_review` has no lease and heartbeat fails.

**Ruling:** drive heartbeats from a cadence that spans the whole in-flight window, reusing
`HeartbeatTimer` (it exists, is tested, and has no production caller), and heartbeat only
`InProgress` items. A failed heartbeat stays best-effort.

### P9 — two sites file out-of-band priorities, which beads rejects outright

`PipelineStations.cs:281` (`ctx.Item.Priority + 50`) and `EvolutionLoop.cs:150` (`Priority = 200`).
Both mean "lower than the item that spawned this".

**Ruling:** `Math.Min(4, ctx.Item.Priority + 1)` and `4`. No prompt change is needed — probed:
nothing in `KitPrompts` or the station contracts mentions priority, and no CLI flag accepts one.

### P10 — `bd init` mutates the target repository's agent files

Calling `EnsureInitialised` from `FactoryHost.Open` means merely opening a beads-backed factory
writes `AGENTS.md`, `CLAUDE.md` and per-tool directories, and appends to `.gitignore`. It edits
inside a marked region rather than clobbering, and nothing runs it unless the operator selects the
beads provider.

**Ruling:** use `bd init --init-if-missing --prefix <p>` — the platform's own idempotent form,
which also avoids the destroy-token re-init hazard. The file-writing side effect is recorded here
and in the handoff as a deliberate acceptance, not a discovery for phase 4.

### P11 — filing a non-Ready item is two writes with a claimable window

`bd create` has no status flag, so a Draft item is briefly `open` between create and update and a
concurrent machine could claim it. `bd batch` could make it one transaction; that is scope creep.

**Ruling:** accept and record. The window is one local write apart.

### P12 — `SkippableFact` needs a new package

**Ruling:** take the plan's stated alternative — a plain `[Fact]` that returns early when `bd` is
absent — and state in the completion report that `bd` was present, so the tests genuinely executed
rather than silently passing.

### P13 — reconcile compares only `State`

`local.State == authoritative.State` means a title, priority, intent or dependency change made on
another machine is never folded in, although beads is authoritative for all of them.

**Ruling:** compare the mapped projection rather than `State` alone, so "beads wins" is true of
every field beads owns.

### Carried-forward decision: `RequeueOrphans` routes through `Release`

`Release` is where a beads store drops the lease and clears the assignee; left unrouted, a requeued
orphan keeps its lease and no other machine can take it.

**Ruling:** `_s.Items.Release(item.Id, "requeued after restart")`, and **drop the redundant
`Update`.** The `Update` re-emitted the same item with no field change — the
`WorkItemStateChanged` fold already sets `State` and `UpdatedAt` — and under beads it would cost a
second write that re-sends the whole metadata blob. `RuntimeTests.Orphaned_work_is_requeued_after_a_restart`
asserts the final state only, so it still holds.

### Not re-litigated

`ProviderRegistry` already enforces built-ins-win structurally, so registering `"beads"` is a
config change. The default provider stays `ledger` in phase 3: flipping it is a migration, which is
phase 4's scope, so **D1 is delivered as an available authority, not as the default**.
