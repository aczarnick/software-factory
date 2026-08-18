# Storage Ports: Phase 1 Handoff

Phase 1 shipped 2026-08-14, merged to `master` as a fast-forward (`f1a3eb2..ecec44c`,
12 commits). Suite **142 passing**. This is what phase 2 needs to know and could not learn
from the diff.

## What phase 1 actually delivered

`IWorkItemStore`, `IRunHistory`, `IRunHistorySink`, `SpendTotals`, `BudgetRestoreView` in
`Factory.Core` (still dependency-free — it is the plugin ABI). `Ledger` became
`Factory.Runtime/Providers/JsonlRunHistory`. `LedgerWorkItemStore` implements the backlog
port. `FactoryServices` carries `History` and `Items`; the orchestrator claims through
`IWorkItemStore.TryClaim` instead of `State.Dispatchable().Take(n)`.

## The one behaviour that did change

The ledger's claim reason text: `"dispatched"` → `"claimed by <factory name>"`.
`WorkItemStateChanged.Reason` is read by no code and no test, and old ledger lines still
parse — but the ledger is a durable artifact, so the "no observable behaviour change" claim
carries this one exception.

## The property phase 3 depends on

**No work-item event is constructed outside `LedgerWorkItemStore`.** Verified by enumerating
every `Record(new ...)` call site in `src/` — 15 total, 12 of them run-history events, 3
inside the store. This is the single fact that makes swapping in a beads-backed store a
drop-in. If a future change writes `WorkItemFiled`/`WorkItemUpdated`/`WorkItemStateChanged`
anywhere else, that property is lost and phase 3 gets harder.

One deliberate exception: `RunCompleted` mutates a work item's `SpentUsd` inside the fold
(`FactoryState.ApplyLocked`). That is correct — the spec's beads mapping lists `SpentUsd`,
`Station`, `Worktree`, `Attempts`, `LastError` as staying local.

## Traps that cost real time in phase 1

- **A `git mv` plus a heavy rewrite drops below git's 50% rename-similarity default.**
  `JsonlRunHistory.cs` traces to its `Ledger.cs` origin only at `-M20%`. `git mv` was used;
  history is intact. Do not re-investigate this.
- **`dotnet build` without `--no-incremental` reports 0 warnings by recompiling nothing.**
  The true baseline is **1** pre-existing `CA1416` at `tests/Factory.Tests/DoctorCommandTests.cs:33`.
  Always use `--no-incremental` when reporting a warning count.
- **`dotnet test tests/Factory.Tests --filter ...` builds only that project and its
  ProjectReferences.** Phase 2's fixture plugin is deliberately not referenced by the test
  project, so a focused filter run will not build it. Run `dotnet build` at the solution root
  first, or the fixture DLL will be missing and the failure will be confusing.
- **Plans in this directory carry stale line numbers.** Phase 1's Task 4 described edits that
  an earlier task had already made. Verify current file state before editing; do not trust a
  plan's line references.

## Latent issues carried forward, with rulings

- **`_gate` in `LedgerWorkItemStore` guards compound read-modify-write only** (`TryClaim`,
  `Release`). `Add`/`Update`/`Transition` are single `Record` calls, atomic under
  `JsonlRunHistory`'s and `FactoryState`'s own locks. Documented on the field. Safe today
  because the only path *into* `Ready` is `Activate`, which runs from the CLI, not from a
  station task — and if the race ever became reachable, `CanTransition` throws rather than
  silently double-claiming. The failure is loud.
- **Two clocks decide "today" in budget restore.** `BudgetGuard._clock` stamps `_day`;
  `JsonlRunHistory._clock` buckets the daily totals. Both default to `TimeProvider.System`
  and `FactoryHost.Open` injects neither, so the divergent case is reachable only from a test
  that fakes one side. Noted on `BudgetRestoreView`.
- **`IRunHistory.Champions()` has no caller.** `EvolutionService` still reads
  `_s.State.Champions`. Leaving it is correct — routing it through the port would trade a
  dictionary lookup for a full ledger re-read — but D4's "every member traces to a call site"
  is not literally true of `Champions()` yet.
- **`IWorkItemStore.Release` has no production caller.** See the phase 3 plan's carried-forward
  section: `Orchestrator.RequeueOrphans` is its intended caller, deferred because swapping it
  in would drop a `WorkItemUpdated` event and break phase 1's contract.
- **`FactoryHost.Open` folds the ledger three times** (constructor `_seq`, `Replay()`,
  `ForBudget()`), and `EvolutionService` reads once per evolvable station. Measured, not
  assumed: the real ledger is 1.5 MB / 927 lines, so this is single-digit milliseconds against
  a tool that shells out to Claude. Revisit when a provider-level fold cache is cheap.
- **`FactoryHost._history` is a redundant second reference** to `Services.History`, used only
  by `Dispose()`.

## Process notes for the next run

- Subagent-driven development worked. Six tasks, fresh implementer each, review between.
  Tasks 2 and 3 each needed one fix round; tasks 1, 4, 5 passed clean.
- **Reviewers reliably failed to deliver verdicts as chat replies** — three of them idled
  without reporting. Giving the reviewer a report *file* to write, and a one-line reply, fixed
  it immediately. Use the file contract for every review.
- **Mutation-check every new test.** Two tasks shipped tests that passed whether or not the
  behaviour existed, caught only by asking a reviewer to delete the logic and watch. In
  phase 1's task 6 the mutation check also caught a test that passed for the wrong reason
  (two items writing the same filename, so a merge-order artifact masked the real assertion).
- Do not run `factory up` or `factory build` against this checkout while implementing —
  phase 2 task 5 modifies `FactoryHost.Open` and the queued items are live.
