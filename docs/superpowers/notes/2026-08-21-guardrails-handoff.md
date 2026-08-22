# Guardrails programme — session handoff

**Date:** 2026-08-21
**Branch:** `worktree-guardrails` (28 commits, **unmerged**)
**Worktree:** `.claude/worktrees/guardrails` (git-ignored, still on disk)
**State:** Phase 1 complete and reviewed. Phases 2–6 planned, not started.

---

## 1. What exists

| Artifact | Path |
|---|---|
| Design spec (all six phases) | `docs/superpowers/specs/2026-08-19-pipeline-gates-design.md` |
| Phase 1 plan | `docs/superpowers/plans/2026-08-19-phase-1-analyzers.md` |
| Phase 1 outcome + lessons | `docs/superpowers/notes/2026-08-19-phase-1-analyzers.md` |
| Issue tracker | beads epic `software-factory-v3z` |

Read the spec before touching phases 2–6. It records not just the decisions but the
**measurements and rejected alternatives** behind them, which is what stops the next session
relitigating settled ground.

## 2. THE MERGE IS NOT DONE — and it is a real job, not a formality

**Measured 2026-08-21, after the work was finished:**

```
master...worktree-guardrails      19 behind, 29 ahead     (diverged, not fast-forwardable)
files changed by BOTH sides       12
snake_case test methods on master 378
```

`master` advanced **19 commits** while this branch was being built — including a
`factory: integrate wi-…` commit, i.e. the autonomous daemon committing to `master` directly.
This is exactly the hazard recorded in the phase-2 storage-ports handoff: a live `factory up`
commits to whatever the main checkout has at HEAD.

**Why this is not a small merge:**

- The 12 overlapping files include `tests/Factory.Tests/RuntimeTests.cs`, `AgentTests.cs`,
  `CoreTests.cs` and `DoctorCommandTests.cs` — precisely the files this branch rewrote wholesale
  when it renamed 362 test methods. Large textual conflicts are near-certain.
- `master` now has **378 snake_case test methods**. This branch renamed 362 at the branch point,
  so `master` has added new tests in the old convention. Every one of them is a **CA1707 error**
  after the merge, because `TreatWarningsAsErrors` is now on.
- Overlapping source files include `Toolchain.cs`, `Workspace.cs`, `CheckStation.cs` and
  `CliAgentTransport.cs` — all four carry Phase 1 defect fixes, so conflicts there need
  resolving with the fix intact, not just textually.

**Recommended approach — do not merge into `master` first.** Merge the other direction, inside
the worktree, where there is a green build to check against and `master` is never left broken:

```bash
cd /Users/aczarnick/personal/repos/software-factory/.claude/worktrees/guardrails
git merge master                # resolve 12 conflicts here
# rename every new snake_case test method master brought in -- CA1707 is an error now
dotnet build SoftwareFactory.sln --no-incremental   # must reach 0 Warning(s) 0 Error(s)
dotnet test SoftwareFactory.sln
```

Only once that is green does the main-checkout merge become a fast-forward and therefore safe.
Tracked as its own bead — it deserves a fresh session, not the tail end of one.

**The other hazard.** The main checkout also has uncommitted changes to three files:

```
 M src/Factory.Cli/Commands.cs
 M tests/Factory.Tests/LedgerProjectionTests.cs
?? tests/Factory.Tests/ListVerdictColumnTests.cs
```

This branch **also modifies the first two**. A merge will collide. Worse, `LedgerProjectionTests.cs`
and `ListVerdictColumnTests.cs` are test files written before this branch existed, so their test
methods are almost certainly still snake_case — and `CA1707` is now an **error**, so they will fail
the build the moment they are committed.

Also uncommitted in the main checkout, and required for the harness worktree flow to work at all:

```
 M .gitignore                     (+ .claude/worktrees/)
 M .claude/settings.local.json    (+ worktree.baseRef: head)
```

### Before touching the merge, land the loose ends in the main checkout

Do not use bare `git stash` — the stash stack is shared with other worktrees and with concurrent
sessions, so a pop can take someone else's work.

```bash
cd /Users/aczarnick/personal/repos/software-factory
git status                                    # confirm what is dirty

# The two settings changes are prerequisites for the harness worktree flow, not feature work.
git add .gitignore .claude/settings.local.json
git commit -m "Ignore harness worktrees and branch them from local HEAD"

# Set the in-flight verdict-column work aside as a WIP commit; it survives concurrent sessions.
git add -A
git commit -m "WIP: verdict column work in progress"
```

Then do the `git merge master` inside the worktree as above. No git remote is configured, so
there is nothing to push at any point.

## 3. Phase 1 outcome

```
Diagnostics    438 -> 413 -> 51 -> 46 -> 0
Build          0 Warning(s) 0 Error(s)
Suite          417 passed, 0 failed
Gate proof     injected violation -> error CA1707 -> Build FAILED
.editorconfig  zero severity = none
```

`AnalysisLevel=latest-recommended` plus four opt-in rules, `TreatWarningsAsErrors=true`.
`CheckStation` already runs `dotnet build`, so this gates every future work item with no gate code.

**Three of the four opt-in rules found a real defect** — not four. CA5392 is hygiene only; the
attribute has no effect on non-Windows and that P/Invoke only ever runs on Unix. Corrected in the
phase note; do not let the four-for-four version resurface.

## 4. Open beads

| Bead | What |
|---|---|
| `v3z.2` | **Phase 2 — CSharpier.** Next ready work. |
| `v3z.3` | Phase 3 — split `Factory.Tests` into unit/integration/e2e. **Blocks the coverage gate.** |
| `v3z.4` | Phase 4 — `IGate`, pipeline builder, blueprint schema (the vertical slice). |
| `v3z.5` | Phase 5 — coverage / complexity / security gates. Carries the two open numbers. |
| `v3z.6` | Phase 6 — LLM review gates, cron scheduling, worktree enforcement. |
| `v3z.7` | **Progress hooks are process-wide statics.** Take this before stall detection is wired. |
| `v3z.8` | `Workspace.Dispose()` never called by its owner. |
| `v3z.9` | `EnforceCodeStyleInBuild` enforces nothing. Blocked on Phase 2. |

## 5. Decisions still owed by the user

- **What "100% unit coverage" means**: line only, or line and branch. And whether `Factory.Cli` is
  in scope — it was 15.5% line / 8.6% branch and dominates the cost. Decide against the per-tier
  baseline Phase 3 produces, not before.
- **The integration coverage figure** (`N%` in the spec). Not defensible until the tier split makes
  it measurable.
- **Complexity tool and thresholds.** `Microsoft.CodeAnalysis.Metrics` for .NET; something
  language-agnostic for generated output, chosen per toolchain.

## 6. Things that will bite the next session

**The test suite is not concurrency-safe.** Two `dotnet test` runs against the same worktree
corrupt each other and produce failures that look real. Run one at a time. The suite takes 4–6
minutes.

**`PipelineTests.TwoIndependentReadyItemsAreBothClaimedAndCompleted` is flaky.** It passes alone in
~900ms and fails under xUnit parallel-collection contention because it drives real git and a real
`dotnet` toolchain probe. `HeartbeatTimerTests.StopHaltsFurtherInvocations` is similar. If either is
the only failure, it is not your bug — confirm in isolation and move on. Phase 3's tier split is the
actual fix, and this is the concrete argument for it.

**This host slept mid-command repeatedly**, killing four subagents. Commit before running anything
long. A detached background process writing to a file survives a sleep; an in-flight subagent
response does not.

**Reviewer subagents failed to deliver eight times across five agents.** Every one that did deliver
did so only after being explicitly chased for its report. Both attempts on the full 419KB diff died;
the same review succeeded on a 57KB scoped diff. If a reviewer goes idle without reporting: chase it
once with a fill-in-the-blank skeleton, and if that fails, scope the diff down rather than
re-dispatching the same thing.

## 7. The two lessons worth carrying

**A gate that has never failed has not been shown to work.** Phase 1 shipped two controls that read
as coverage while enforcing nothing:

- The diagnostic count used `warning [A-Z]+[0-9]+`, which cannot match `xUnit####`. A live
  `xUnit2031` sat uncounted for four tasks and would have failed the very gate the phase installs.
- `EnforceCodeStyleInBuild=true` only promotes IDE diagnostics whose severity is explicitly set.
  None were. Zero IDE diagnostics fired even under `AnalysisMode=All`.

Neither was found by whoever introduced it. Before trusting a new gate, make it fail on purpose.

**A resource leak can be the thing holding a design together.** Fixing the per-delegation
`Orchestrator` leak (CA2000) exposed that `Shell`'s progress hooks are process-wide statics: the
child had been leaking, so the hooks stayed installed pointing at its dead dictionary. Removing the
leak is when that surfaced. See `v3z.7`.

**Where the defects actually came from.** Four in this branch — three found by implementers, one by
the final review, **none by task review**. Three of them were defects in the plan, not the code: a
test trigger string the production gate rejects, a missing `CancellationToken` overload, and a test
asserting that an idempotent `Dispose()` throws. Task reviews confirmed correct work; they did not
catch wrong instructions. Worth weighting when deciding where review effort goes.
