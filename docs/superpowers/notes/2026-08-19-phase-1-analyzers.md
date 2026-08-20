# Phase 1: Analyzers — Handoff

Phase 1 landed 2026-08-19/20 on `worktree-guardrails`. Diagnostic count went 438 → 413 → 51 →
46 → **0** across six tasks. This is what phase 2 (CSharpier) and phase 5 (the coverage gate)
need to know and could not learn from the diff.

## The two measurements, and why `All` was rejected

`AnalysisMode=All` produced **642** diagnostics. `AnalysisMode=Recommended` (the effective mode
under `AnalysisLevel=latest-recommended`) produced **411**. The 231-diagnostic gap between them
is almost entirely rules this codebase deliberately does not follow, not rules it accidentally
fails:

- **CA1062** (validate arguments are non-null) — redundant under `<Nullable>enable</Nullable>`;
  the compiler's nullable-reference-type warnings already cover the same defect at the call
  site, so this rule would be pure noise on top of an existing guarantee.
- **CA2007** (`ConfigureAwait(false)`) — this is application code with no synchronization
  context to deadlock against (no ASP.NET Core `SynchronizationContext`, no WinForms/WPF
  message pump), so the rule protects against a hazard that cannot occur here.
- **CA1063** (full `IDisposable` dispose pattern, `Dispose(bool)` + finalizer) — the
  `IDisposable` types in this codebase are `sealed`; the extra ceremony exists to support
  inheritance that will never happen.

`AnalysisLevel` stays `latest-recommended`. `All` was measured, not guessed, and rejected on
those grounds.

## The four opt-in rules, and the defect each one found

`latest-recommended` alone didn't request these; they were turned on deliberately and each one
found a real bug on first run, not a style nit:

- **CA2000** (dispose objects before losing scope) → a per-delegation `Orchestrator` leak in
  `DelegateStation`: an orchestrator was constructed per delegated work item and never disposed.
- **CA1849** (use async methods when in an async method) → synchronous `File.WriteAllText`
  calls inside `async` methods in `Toolchain`, blocking a thread pool thread on I/O that should
  have awaited.
- **CA5392** (`DllImport` should specify `DefaultDllImportSearchPathsAttribute`) →
  `DllImport("libc")` with no search-path constraint, letting the OS loader search an
  attacker-influenceable default path set before finding the intended library.
- **CA1003** (use generic `EventHandler` instead of a custom delegate) →
  `UsageGovernor.Changed` was typed `Action<string>` instead of the standard event pattern.

## CA1816 — cleared by sealing, not suppressing

CA1816 (call `GC.SuppressFinalize` from `Dispose`, or don't implement the full dispose pattern
on a type nothing will subclass) had 25 sites, all in test fixtures. Each was cleared by
`sealed`-ing the fixture class, not by adding a suppression. `.editorconfig` ends this phase
with **zero** `severity = none` entries — every diagnostic in this phase was fixed at its
source, none waived.

## CA1707 — test naming

CA1707 (identifiers should not contain underscores) forced a choice for test method names,
which conventionally use underscores to separate the scenario from the expectation
(`Method_Condition_ExpectedResult`). Decision: tests follow the same naming convention as
product code — no underscores — rather than carving out a per-project waiver. 362 test methods
were renamed; the test count held at **415** immediately before and immediately after the
rename (renaming a method doesn't change how many there are, but a broken rename script could
silently drop or duplicate one, so both counts were checked).

## The one suppression in source, and why it's not a waiver

`IWorkItemStore.Get` carries `[SuppressMessage("Naming", "CA1716", Justification = "...")]`
(`src/Factory.Core/IWorkItemStore.cs:16`). `Get` collides with the `Microsoft.VisualBasic`
namespace under CA1716's naming rule, but renaming it is a plugin ABI break:
`FactoryProviderAttribute`-marked plugins implement this interface as a typed C# member, so an
already-compiled plugin binary would fail to cast at `ProviderRegistry.Resolve` after a rename.
This is a source-level `[SuppressMessage]` attribute scoped to one member with a documented
reason, not an `.editorconfig` `severity = none` waiver — the distinction phase 5 needs to know
is that **no rule is silently off project-wide**; this is the one member-level, justified
exception.

## The counting-regex defect — a lesson, not a footnote

An earlier counting method used `grep -oE 'warning [A-Z]+[0-9]+'` (uppercase-only rule prefix).
That regex requires an all-caps rule id and silently skips mixed-case analyzer ids such as
`xUnit2031`. A live `xUnit2031` violation (`Assert.Single(collection.Where(...))` instead of
the predicate overload) hid behind this regex for four tasks of this plan, reporting a false
zero while a real diagnostic sat in the build log. It was only caught when someone dumped
distinct rule ids by full match instead of trusting a count.

**Lesson: a verification method that cannot see a whole family of diagnostics is worse than no
verification, because it reports success.** The corrected form used from Task 6 onward:

```bash
grep -oE '[A-Za-z0-9_./-]+\.cs\([0-9]+,[0-9]+\): warning [A-Za-z]+[0-9]+' LOG | sort -u | wc -l
grep -oE 'warning [A-Za-z]+[0-9]+' LOG | sort -u
```

The second line dumps every distinct rule id seen, so a new diagnostic family can't hide behind
a case assumption baked into the regex.

## Task 6: warnings fail the build

`TreatWarningsAsErrors` is now `true` in `Directory.Build.props`, next to the Task 1 analyzer
properties. `AnalysisLevel` stays `latest-recommended`. No `NoWarn` entries were added.
`CheckStation` already runs `dotnet build`, so this flip is enforced on every future work item
without a gate being written.

Verified both ways:

1. **Clean build, zero warnings:**

   ```
   Build succeeded.
       0 Warning(s)
       0 Error(s)
   ```

   `grep -oE 'warning [A-Za-z]+[0-9]+' LOG | sort -u` returned nothing.

2. **A deliberate CA1707 violation fails the build as an error:**

   ```
   src/Factory.Core/BadName.cs(3,21): error CA1707: Remove the underscores from type name
   Factory.Core.Bad_Name (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1707)
   [.../src/Factory.Core/Factory.Core.csproj]

   Build FAILED.
   ```

   Exit code 1. The violating file was removed afterward and the build returned to green,
   0 warnings / 0 errors.

3. **Full suite, unchanged:**

   ```
   Passed!  - Failed:     0, Passed:   417, Skipped:     0, Total:   417, Duration: 4 m 18 s
   ```

   `PipelineTests.TwoIndependentReadyItemsAreBothClaimedAndCompleted` and
   `HeartbeatTimerTests.StopHaltsFurtherInvocations` are known timing-sensitive tests (real
   `dotnet`/git subprocess contention under xUnit's parallel collections); neither fired in
   this run.

## What phase 5 needs from this

The coverage gate phase 5 adds must not reintroduce a suppression to satisfy its own thresholds
— this phase ends with `.editorconfig` at zero waivers and exactly one justified member-level
`[SuppressMessage]`, and that's the bar to hold.
