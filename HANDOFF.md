# Handoff

State of this repository as of 2026-08-20. `README.md` explains what the factory *is*; this file
explains where the work stands, what is broken, and how to pick it up.

## The goal that matters now

**The factory cannot yet improve the factory unattended.** It produces good work when it completes,
but most runs end with items stuck in a state only a human can clear. Everything below is in service
of closing that gap.

## Where things are

    factory version     # must match `git rev-parse HEAD` — it will warn if not
    factory status      # version, harness staleness, disabled gates, backlog, spend
    factory doctor      # toolchain, harness, gates, claude CLI, blueprint
    factory doctor --recapture   # retake the toolchain baseline on an idle machine
    factory ls          # backlog; `passed` counts criteria that actually passed
    ./verify.sh         # the gate: build, test, format, then CLI acceptance criteria
    ./verify.sh --fast  # build, test, format only

`.factory/` is this deployment's own state — event ledger, prompt registry, response cache,
toolchain baseline. Gitignored, must not be deleted: it is the factory's memory. Backlog items now
live in **beads** (`bd`), so new ids look like `wi-abc123` while historical ones are `wi_abc123`.

## Environment

- **.NET 10** (`global.json` pins SDK `10.0.400`) and the `claude` CLI on `PATH`.
- `./install.sh` installs to `~/.local/bin` and needs no sudo. It warns if another `factory`
  earlier on `PATH` still wins.
- **This host's build tooling is unreliable.** `dotnet test` has needed a retry during an *idle*
  baseline capture. Retry rather than believing the first failure.
- **Other agents write to this repository concurrently** — a JetBrains Rider agent and Claude Code
  desktop sessions, some with permission checks disabled. This is expected and allowed. The factory
  must tolerate other writers rather than assume it is alone.

## Verified

- `./verify.sh` — **32 criteria, 0 failed**. `dotnet test` — **420+ passing**.
- The factory built and merged real fixes to its own source this session, gates passing honestly:
  `ToolchainGate` (serialised compiles), the concurrency-default reconciliation, and the first two
  pieces of the worktree-sync work.

## Why the factory stalls — measured, not guessed

Current blocked/failed items, by reason:

| count | reason |
|---|---|
| **11** | `Budget exhausted for daily` |
| 2 | `integrate: merge conflict` (`Commands.cs`, `Orchestrator.cs`) |
| 3 | `implement: no file changes were produced` — where the agent's text is a *reasoned blocker report* |
| 1 | `review: Missing implementation: … was not modified` |
| 2 | `decompose:` `stop_sequence`, `error_max_budget_usd` |

The three biggest autonomy defects, in order:

1. **Budget exhaustion blocks instead of pausing.** Hitting the daily ceiling moves items to
   `Blocked`, which needs a human `factory activate`. A daily ceiling is a *rate* limit; it should
   park work until the window rolls, exactly as the usage governor already does for rate limits.
2. **An agent that reports a real blocker is treated as a failed run.** The station returns "no file
   changes were produced" and retries, discarding the model's own explanation of why it could not
   proceed. There is no escalation path carrying a reason.
3. **Branches never sync with a moved mainline.** Each item's worktree is branched at claim time and
   merged with `--no-ff` at integrate; nothing rebases. Two items touching one file conflict
   deterministically. Work in progress — see the backlog.

## Known gaps

- **Two flaky tests**, both timing-sensitive, both fail only under load:
  `PipelineTests.Two_independent_ready_items_are_both_claimed_and_completed` and
  `HeartbeatTimerTests.StopHaltsFurtherInvocations`. This matters more than it looks: the check
  station gates on `dotnet test`, so a flake can record a false regression against good work, or
  poison the baseline and silently switch a gate off. That has already happened once.
- **`--max-items` counts dispatches, not deliverables.** A decomposing parent spends a slot,
  delivers nothing, and spawns children the cap now excludes.
- **`decompose` and `plan` disagree without anyone noticing.** Decompose called an item a "single
  unit" that plan immediately sized at 6 files and 11 steps; implement then blew its turn ceiling.
  That mismatch is a signal available before implement ever runs.
- **Traces are addressable but unread.** `RunRecord.SessionId` is now persisted, and the `claude`
  CLI writes a full transcript per session under
  `~/.claude/projects/<slugged-cwd>/<session-id>.jsonl`. Nothing feeds them to the optimiser yet.
  Asked to improve a station from scalars alone, the optimiser correctly refused: *"any edit would
  be a speculative rewrite rather than a response to evidence."*
- **The evolution loop has still never promoted a prompt.** It needs ~10 runs per station before it
  will propose anything, and it cannot see traces.
- `WorkItem.SpentUsd` is vestigial — nothing reads it for display; `FactoryState.SpentFor` is the
  truth. Removing it means refactoring `LocalRunState`, which carries it across the store boundary.
- A `ContractVersion` bump red-lights the suite once: `dotnet test` copies the fixture plugin before
  rebuilding it, so the plugin tests fail on the first run and pass on the second. Rebuild
  `tests/fixtures/Factory.TestPlugin` first.

## Operating notes

- **Run one factory at a time per repository**, and prefer `--concurrency 1` until the
  worktree-sync work lands.
- Baseline before dispatch on an idle machine: `factory doctor --recapture`. A baseline captured
  under load can record a check as already-failing, which switches that gate off silently.
- Budgets are enforced before dispatch (`--budget`, `--item-budget`). Spend is in the ledger.
- `factory activate <id>` requeues blocked, failed, or proposed work.

## Conventions established this session

- **Work in a git worktree** under `.claude/worktrees/` (gitignored). The repo has no remote, so
  create with `git worktree add … HEAD` rather than branching from `origin`.
- **`./verify.sh` is the gate.** A change is not done until it passes, and it must stay green.
- **Acceptance criteria name the specific proof**, not the whole gate. A criterion ending in
  `./verify.sh` drags a full build into the implement station and burns turns compiling.
- **Ledger-derived facts belong in the projection, not on `WorkItem`.** `WorkItemUpdated` replaces
  the record wholesale from a caller's snapshot and will destroy anything accumulated onto it.
- **Assert the next actor's postcondition.** Status assertions let three criticals through a green
  suite; a test that checks a lock exists is worth less than one that observes concurrency.
