# Phase 1: Analyzers at Maximum, Warnings Fail Builds — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every .NET analyzer runs at maximum severity across the solution, every remaining
diagnostic is either fixed or suppressed with a written justification, and `TreatWarningsAsErrors`
is on so a warning fails the build.

**Architecture:** Analyzer configuration lives in `.editorconfig` (rule severities, scoped by
path) and `Directory.Build.props` (analysis level, warning policy). No new code and no new
mechanism — `CheckStation` already runs `dotnet build`, so flipping the warning policy is
enforced by the existing pipeline the moment it lands. The work is triage: decide per rule
whether it is a defect to fix or a convention that does not apply, and record why.

**Tech Stack:** .NET 10 (`net10.0`), SDK pinned `10.0.400` via `global.json`, MSBuild,
Roslyn analyzers (`AnalysisLevel=latest-all`), xUnit 2.9.2.

**Spec:** `docs/superpowers/specs/2026-08-19-pipeline-gates-design.md` (§5 Phase 1)

## Global Constraints

- Target framework is `net10.0`; SDK is pinned to `10.0.400` with `rollForward: latestFeature`.
- `Nullable`, `ImplicitUsings` are `enable`; `LangVersion` is `latest`. Do not change these.
- One top-level type per file. Nested/private types may stay in their containing type's file.
- XML doc comments only on **public** APIs. No explanatory prose or narration in code.
- Every suppression MUST carry a justification comment in `.editorconfig` saying why the rule
  does not apply here. A bare `severity = none` with no reason is a plan failure.
- Verification is `dotnet build SoftwareFactory.sln` **and** `dotnet test SoftwareFactory.sln`.
  Show actual output. Non-zero exit means not done.
- The full suite takes ~4 minutes and is **not** safe to run concurrently with another
  `dotnet test` against the same worktree — a second run corrupts the first. Run one at a time.

## Measured Starting Point

Captured on this branch at `f5e2269` with `AnalysisLevel=latest-all` and
`EnforceCodeStyleInBuild=true`, full rebuild, deduplicated by file/line/rule:

```
TOTAL          642
  tests/       471
  src/         171
```

| Rule | src | tests | What it is |
|---|---:|---:|---|
| CA1707 | 0 | 362 | Identifiers should not contain underscores |
| CA1062 | 85 | 2 | Validate arguments of public methods |
| CA1063 | 0 | 50 | Implement IDisposable correctly |
| CA1816 | 0 | 25 | Call GC.SuppressFinalize |
| CA1031 | 18 | 0 | Do not catch general exception types |
| CA1002 | 18 | 1 | Do not expose generic lists |
| CA2007 | 16 | 1 | Consider calling ConfigureAwait |
| CA1849 | 2 | 12 | Call async methods in an async context |
| CA2000 | 1 | 10 | Dispose objects before losing scope |
| CA1515 | 3 | 3 | Consider making public types internal |
| CA1859 | 4 | 0 | Use concrete types for improved performance |
| CA1826 | 4 | 0 | Use property instead of Enumerable method |
| CA1720 | 4 | 0 | Identifier contains type name |
| CA1068 | 3 | 0 | CancellationToken parameters must come last |
| CA1822 | 2 | 1 | Mark members as static |
| CA1716 | 2 | 0 | Identifiers should not match keywords |
| CA1032 | 2 | 0 | Implement standard exception constructors |
| CA1812 | 0 | 2 | Avoid uninstantiated internal classes |
| CA1416 | 0 | 1 | Validate platform compatibility |
| CA1711 | 0 | 1 | Identifiers should not have incorrect suffix |
| CA5394 | 1 | 0 | Do not use insecure randomness |
| CA5392 | 1 | 0 | Use DefaultDllImportSearchPaths |
| CA2225 | 1 | 0 | Operator overloads have named alternates |
| CA1725 | 1 | 0 | Parameter names should match base declaration |
| CA1034 | 1 | 0 | Nested types should not be visible |
| CA1003 | 1 | 0 | Use generic event handler instances |
| CA1001 | 1 | 0 | Types owning disposable fields should be disposable |

**Read of these numbers:** the bulk is policy, not defects. 471 of 642 are in test code and
come from three rules that encode library-authoring conventions test projects do not follow.
Of the 171 in `src`, 85 are CA1062 — argument null validation, which is largely redundant when
`Nullable` is `enable`. The genuinely defective sites number about eight.

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `.editorconfig` | Rule severities and per-path scoping, each suppression justified | Create |
| `Directory.Build.props` | Analysis level and warning policy | Modify |
| `src/Factory.Runtime/Workspace.cs` | CA1001 — owns a `SemaphoreSlim`, is not disposable | Modify |
| `src/Factory.Runtime/DelegateStation.cs` | CA2000 — `Orchestrator` created and not disposed | Modify |
| `src/Factory.Runtime/Toolchain.cs` | CA1849 — sync file writes inside async methods | Modify |
| `src/Factory.Runtime/Providers/Beads/BeadsWorkItemStore.cs` | CA1725 — parameter name diverges from the interface | Modify |
| `src/Factory.Agents/CliAgentTransport.cs` | CA5392 — `DllImport` without a search-path attribute | Modify |
| `src/Factory.Agents/UsageGovernor.cs` | CA1003 — `Action<string>` event | Modify |
| `src/Factory.Evolution/PromptRegistry.cs` | CA5394 — `Random` used for A/B traffic split | Modify (suppress in place) |

`.editorconfig` is the only new file. It is deliberately one file rather than one per project:
the whole point is a single definition of the bar, and per-path sections express the test/src
distinction without splitting the file.

---

### Task 1: Turn analyzers on as warnings and record the count

Nothing is fixed in this task. It makes the problem visible and reproducible, so every later
task can be measured against a number rather than an impression.

**Files:**
- Modify: `Directory.Build.props:10`
- Create: `.editorconfig`

**Interfaces:**
- Consumes: nothing.
- Produces: `.editorconfig` at the repository root with a `[*.cs]` section; the MSBuild
  properties `AnalysisLevel` and `EnforceCodeStyleInBuild` set solution-wide. Later tasks add
  sections to this same file.

- [ ] **Step 1: Add the analyzer properties**

In `Directory.Build.props`, inside the existing `<PropertyGroup>`, immediately after the
`<NoWarn>` line:

```xml
    <AnalysisLevel>latest-all</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
```

`latest-all` is the shorthand for the spec's `AnalysisLevel=latest` plus `AnalysisMode=All`.
Use the shorthand; setting both separately does the same thing in two lines.

Do **not** add `TreatWarningsAsErrors` yet. Task 6 does that, and only once the count is zero.

- [ ] **Step 2: Create the `.editorconfig` skeleton**

Create `.editorconfig` at the repository root:

```ini
# Analyzer policy for the Software Factory.
#
# Every rule set below `default` is either a defect class we fix, or a convention that does
# not apply here. A suppression without a stated reason is not allowed: the reason is the
# whole value of the file.

root = true

[*.cs]
indent_style = space
indent_size = 4
end_of_line = lf
insert_final_newline = true
charset = utf-8
```

- [ ] **Step 3: Rebuild and count**

Run:

```bash
dotnet build SoftwareFactory.sln --nologo -v n --no-incremental 2>&1 | tee /tmp/analyzers.log | tail -5
grep -oE "[A-Za-z0-9_./-]+\.cs\([0-9]+,[0-9]+\): warning [A-Z]+[0-9]+" /tmp/analyzers.log | sort -u | wc -l
```

Expected: build exits 0 (warnings do not fail yet), count is **642**.

If the count differs from 642, the SDK has moved. Do not proceed on a guess — record the new
count, regenerate the per-rule table with the command in Step 4, and use those numbers for the
rest of the plan.

- [ ] **Step 4: Record the per-rule split**

Run:

```bash
grep -oE "[A-Za-z0-9_./-]+\.cs\([0-9]+,[0-9]+\): warning [A-Z]+[0-9]+" /tmp/analyzers.log \
  | sort -u > /tmp/uniq.txt
echo "src:";   grep -c "/src/"   /tmp/uniq.txt
echo "tests:"; grep -c "/tests/" /tmp/uniq.txt
grep "/src/" /tmp/uniq.txt | grep -oE "warning [A-Z]+[0-9]+" | sort | uniq -c | sort -rn
```

Expected: `src: 171`, `tests: 471`, and the src breakdown matching the table above.

- [ ] **Step 5: Commit**

```bash
git add Directory.Build.props .editorconfig
git commit -m "Turn every analyzer on, so the bar is visible before it is enforced

Warnings only. 642 diagnostics: 471 in tests, 171 in src. Enforcement
comes once that count is zero."
```

---

### Task 2: Scope library-authoring rules away from test code

471 of 642 diagnostics are in test projects, from rules that encode conventions for shipping
libraries. Test method names are deliberately snake_case sentences; test fixtures are sealed
and never subclassed. These are not defects.

**Files:**
- Modify: `.editorconfig`

**Interfaces:**
- Consumes: `.editorconfig` from Task 1.
- Produces: a `[tests/**/*.cs]` section in `.editorconfig`. Task 3 adds `[src/**/*.cs]`.

- [ ] **Step 1: Add the test-scoped section**

Append to `.editorconfig`:

```ini
# ── Test code ────────────────────────────────────────────────────────────────
# Test projects are not libraries. They ship to nobody, are never subclassed, and their
# identifiers are sentences on purpose.
[tests/**/*.cs]

# CA1707: identifiers should not contain underscores.
# Test names are sentences — `Two_independent_ready_items_are_both_claimed_and_completed`
# reads as a specification. Renaming them to PascalCase would destroy the only documentation
# the suite has.
dotnet_diagnostic.CA1707.severity = none

# CA1063 / CA1816: full IDisposable pattern with GC.SuppressFinalize.
# The pattern exists so a base class can be safely subclassed and finalized. Test fixtures are
# sealed and hold managed handles only, so the ceremony protects against nothing.
dotnet_diagnostic.CA1063.severity = none
dotnet_diagnostic.CA1816.severity = none

# CA1515: consider making public types internal.
# xUnit discovers test classes by reflection over public types.
dotnet_diagnostic.CA1515.severity = none

# CA1812: avoid uninstantiated internal classes.
# Fixture and plugin-probe types are instantiated by xUnit or by the plugin loader through
# reflection, which the analyzer cannot see.
dotnet_diagnostic.CA1812.severity = none
```

Note the rules deliberately **not** suppressed for tests: CA1849 (12), CA2000 (10), CA1062
(2), CA2007 (1), CA1822 (1), CA1711 (1), CA1416 (1), CA1002 (1). Sync-over-async and undisposed
objects in tests are exactly the shared-state bugs that make this suite slow and
concurrency-fragile. Task 5 fixes them.

- [ ] **Step 2: Rebuild and verify the drop**

Run:

```bash
dotnet build SoftwareFactory.sln --nologo -v n --no-incremental 2>&1 | tee /tmp/analyzers.log | tail -3
grep -oE "[A-Za-z0-9_./-]+\.cs\([0-9]+,[0-9]+\): warning [A-Z]+[0-9]+" /tmp/analyzers.log \
  | sort -u > /tmp/uniq.txt
wc -l < /tmp/uniq.txt
grep -c "/tests/" /tmp/uniq.txt
```

Expected: total **200**, tests **29**.

The arithmetic: 471 test diagnostics minus CA1707 (362), CA1063 (50), CA1816 (25), CA1515 (3)
and CA1812 (2) — 442 suppressed — leaves 29 in tests, and 642 − 442 = 200 overall. The `src`
count is unchanged at 171.

- [ ] **Step 3: Commit**

```bash
git add .editorconfig
git commit -m "Stop applying library-authoring rules to test code

Test names are sentences and fixtures are sealed, so CA1707, CA1063,
CA1816, CA1515 and CA1812 flag conventions that do not apply. Each
suppression carries its reason. Sync-over-async and undisposed objects
in tests stay flagged: those are real."
```

---

### Task 3: Decide the src-wide policy rules

Three rules account for 119 of the 171 src diagnostics and each is a genuine policy question,
not a defect. Decide once, write the reason down, move on.

**Files:**
- Modify: `.editorconfig`

**Interfaces:**
- Consumes: `.editorconfig` from Tasks 1–2.
- Produces: a `[src/**/*.cs]` section in `.editorconfig`.

- [ ] **Step 1: Add the src-scoped section**

Append to `.editorconfig`:

```ini
# ── Product code ─────────────────────────────────────────────────────────────
[src/**/*.cs]

# CA1062: validate arguments of public methods. (85 sites)
# The solution builds with `Nullable` enable. A non-nullable reference parameter is already a
# compile-time contract, and adding a runtime null check to every public method restates it in
# a weaker form at every call site. Microsoft's own guidance is to disable CA1062 when nullable
# reference types are on. This factory has no public package surface: `src` is consumed by the
# CLI and the test projects, both of which compile under the same nullable contract.
dotnet_diagnostic.CA1062.severity = none

# CA2007: consider calling ConfigureAwait on the awaited task. (16 sites)
# This is an application and a CLI, not a library. There is no synchronisation context to
# deadlock against, so ConfigureAwait(false) is noise rather than protection. Where the codebase
# already writes it — the orchestrator and station paths — that is a deliberate local choice and
# is left alone.
dotnet_diagnostic.CA2007.severity = none

# CA1031: do not catch general exception types. (18 sites)
# Downgraded, not disabled. Several sites are deliberate and already carry a comment saying so:
# a broken plugin assembly must not stop a factory whose configured providers all work, and
# best-effort diagnostics must not fail the run they are diagnosing. But a blanket suppression
# would also hide the accidental ones, and this repository's stated rule is that errors are
# handled explicitly and never swallowed. Kept as a suggestion so new ones are visible in the
# IDE without failing the build.
dotnet_diagnostic.CA1031.severity = suggestion
```

- [ ] **Step 2: Rebuild and verify**

Run:

```bash
dotnet build SoftwareFactory.sln --nologo -v n --no-incremental 2>&1 | tee /tmp/analyzers.log | tail -3
grep -oE "[A-Za-z0-9_./-]+\.cs\([0-9]+,[0-9]+\): warning [A-Z]+[0-9]+" /tmp/analyzers.log \
  | sort -u | wc -l
```

Expected: **81** — 200 minus the src-only CA1062 (85), CA2007 (16) and CA1031 (18). The two
CA1062 and one CA2007 sites in `tests/` are deliberately *not* covered by a `[src/**/*.cs]`
section and remain.

- [ ] **Step 3: Commit**

```bash
git add .editorconfig
git commit -m "Settle the three analyzer rules that are policy, not defects

CA1062 restates a contract the nullable compiler already enforces.
CA2007 protects against a synchronisation context an application does
not have. CA1031 stays visible as a suggestion, because some of those
catches are deliberate and some are not, and a blanket suppression
would stop telling them apart."
```

---

### Task 4: Fix the genuine defects

Eight sites where the analyzer found a real problem. Each gets a test where the behaviour is
observable, and a fix.

**Files:**
- Modify: `src/Factory.Runtime/Workspace.cs:13`
- Modify: `src/Factory.Runtime/DelegateStation.cs:59`
- Modify: `src/Factory.Runtime/Providers/Beads/BeadsWorkItemStore.cs:56`
- Modify: `src/Factory.Agents/CliAgentTransport.cs:228`
- Modify: `src/Factory.Agents/UsageGovernor.cs:55`
- Modify: `src/Factory.Runtime/Toolchain.cs:535,562`
- Modify: `src/Factory.Evolution/PromptRegistry.cs:97`
- Test: `tests/Factory.Tests/RuntimeTests.cs`

**Interfaces:**
- Consumes: `.editorconfig` from Tasks 1–3.
- Produces:
  - `Workspace : IDisposable` — `public void Dispose()`.
  - `BeadsWorkItemStore.TryClaim(string owner)` — parameter renamed from `claimant`; call sites
    that use a named argument must be updated.
  - `UsageGovernor.Changed` becomes `EventHandler<UsageChangedEventArgs>`; `UsageChangedEventArgs`
    is a new public type in `src/Factory.Agents/UsageChangedEventArgs.cs` with a
    `public string Message { get; }`.

- [ ] **Step 1: Write the failing test for `Workspace` disposal**

`Workspace` holds `private readonly SemaphoreSlim _integrateGate = new(1, 1);` and never
releases it (CA1001). Add to `tests/Factory.Tests/RuntimeTests.cs`:

```csharp
[Fact]
public void Workspace_releases_its_integration_gate_when_disposed()
{
    var root = Directory.CreateTempSubdirectory().FullName;
    var workspace = new Workspace(root, new FactoryPaths(root));

    workspace.Dispose();

    Assert.Throws<ObjectDisposedException>(() => workspace.Dispose());
}
```

This asserts the semaphore was really disposed: a second `Dispose` on a disposed
`SemaphoreSlim` is the observable consequence. If you make `Dispose` idempotent instead, change
the assertion to match — but then it proves nothing, so prefer the throwing form.

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test SoftwareFactory.sln --filter "FullyQualifiedName~Workspace_releases_its_integration_gate"`
Expected: FAIL — `Workspace` does not contain a definition for `Dispose`.

- [ ] **Step 3: Make `Workspace` disposable**

In `src/Factory.Runtime/Workspace.cs`, change the declaration and add the method:

```csharp
public sealed class Workspace(string repoRoot, FactoryPaths paths) : IDisposable
{
    private readonly SemaphoreSlim _integrateGate = new(1, 1);

    public void Dispose() => _integrateGate.Dispose();
```

Sealed, managed-only, no finalizer — so a bare `Dispose` is correct and CA1063/CA1816 do not
apply. Do not add the full pattern.

- [ ] **Step 4: Run it and watch it pass**

Run: `dotnet test SoftwareFactory.sln --filter "FullyQualifiedName~Workspace_releases_its_integration_gate"`
Expected: PASS.

- [ ] **Step 5: Dispose the delegated orchestrator**

`src/Factory.Runtime/DelegateStation.cs:59` creates an `Orchestrator` — which is
`sealed class Orchestrator : IDisposable` — and never disposes it (CA2000). One delegation per
work item leaks one orchestrator.

Change:

```csharp
        var report = await child.CreateOrchestrator().RunAsync(new OrchestratorOptions
```

to:

```csharp
        using var orchestrator = child.CreateOrchestrator();
        var report = await orchestrator.RunAsync(new OrchestratorOptions
```

- [ ] **Step 6: Match the interface parameter name**

`src/Factory.Runtime/Providers/Beads/BeadsWorkItemStore.cs:56` declares
`TryClaim(string claimant)` while `IWorkItemStore.TryClaim(string owner)` names it `owner`
(CA1725). A caller using a named argument against the interface breaks on the concrete type.

Rename the parameter to `owner` and update its use in the body:

```csharp
    public WorkItem? TryClaim(string owner) =>
        cli.Json<BeadRecord>([.. BeadMapper.ClaimArgs(owner)])
           .Select(BeadMapper.ToWorkItem)
           .FirstOrDefault();
```

Then check for named-argument call sites: `grep -rn "claimant:" src tests --include='*.cs'`.
Update any that appear.

- [ ] **Step 7: Constrain the native library search path**

`src/Factory.Agents/CliAgentTransport.cs:228` declares `DllImport("libc")` with no search-path
constraint (CA5392), which allows the loader to search the current working directory — and the
factory runs with its working directory set to a repository it is actively modifying.

Change:

```csharp
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint Geteuid();
```

to:

```csharp
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(
        System.Runtime.InteropServices.DllImportSearchPath.SafeDirectories)]
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint Geteuid();
```

- [ ] **Step 8: Run the full suite**

Run: `dotnet test SoftwareFactory.sln --nologo`
Expected: 416 passed (415 plus the new one), 0 failed.

- [ ] **Step 9: Commit**

```bash
git add src tests
git commit -m "Fix what the analyzers found that was actually broken

Workspace held a SemaphoreSlim it never released. Every delegation
leaked an Orchestrator. BeadsWorkItemStore named a parameter the
interface calls something else, so a named argument breaks on the
concrete type. And a DllImport with no search path let the loader look
in the working directory, which for this process is a repository it is
in the middle of editing."
```

---

### Task 5: Clear the long tail

The rules left after Tasks 2–4: CA1002, CA1849, CA2000 (tests), CA1859, CA1826, CA1720,
CA1515 (src), CA1068, CA1822, CA1716, CA1032, CA1003, CA5394, CA2225, CA1034, CA1711, CA1416.
Roughly 60 sites. Each is small; none is architectural.

**Files:**
- Modify: `.editorconfig`
- Modify: various under `src/` and `tests/` as the build reports them
- Modify: `src/Factory.Agents/UsageGovernor.cs:55`
- Create: `src/Factory.Agents/UsageChangedEventArgs.cs`
- Modify: `src/Factory.Evolution/PromptRegistry.cs:97`

**Interfaces:**
- Consumes: everything from Tasks 1–4.
- Produces: `UsageChangedEventArgs` (public, in `Factory.Agents`), and
  `UsageGovernor.Changed` as `EventHandler<UsageChangedEventArgs>`.

- [ ] **Step 1: Suppress the two rules that are wrong here, with reasons**

Append to the `[src/**/*.cs]` section of `.editorconfig`:

```ini
# CA5394: do not use insecure randomness.
# PromptRegistry.Select uses Random to split traffic between a champion and a challenger
# prompt. This is A/B sampling, not a security decision — nothing is protected by the value
# being unpredictable, and a cryptographic RNG would only make the evolution loop slower.
dotnet_diagnostic.CA5394.severity = none

# CA1515: consider making public types internal.
# The factory exposes a plugin ABI. Provider and port types are public because third-party
# assemblies implement them; narrowing them would break the contract described in
# FactoryProviderAttribute.
dotnet_diagnostic.CA1515.severity = none
```

- [ ] **Step 2: Write the failing test for the event signature**

`UsageGovernor.Changed` is `Action<string>` (CA1003). Add to `tests/Factory.Tests/AgentTests.cs`:

`ObserveRejection` is the public trigger: it calls the private `Record`, whose snapshot has a
status other than `Allowed`, which raises `Changed` (`UsageGovernor.cs:107`, `:125`, `:137`).

```csharp
[Fact]
public void Usage_governor_reports_a_rejection_through_a_standard_event()
{
    var governor = new UsageGovernor();
    string? reported = null;
    governor.Changed += (_, e) => reported = e.Message;

    governor.ObserveRejection("rate limit exceeded");

    Assert.NotNull(reported);
}
```

The assertion is deliberately on delivery, not on the exact text: the message is composed by
`RateLimitSnapshot.Describe` from a clock reading, and asserting its wording would test the
formatter rather than the event contract this task changes.

- [ ] **Step 3: Run it and watch it fail**

Run: `dotnet test SoftwareFactory.sln --filter "FullyQualifiedName~Usage_governor_reports_changes"`
Expected: FAIL — the lambda's parameter count does not match `Action<string>`.

- [ ] **Step 4: Introduce the event args type**

Create `src/Factory.Agents/UsageChangedEventArgs.cs`:

```csharp
namespace Factory.Agents;

/// <summary>Describes a change in what the usage governor will allow.</summary>
public sealed class UsageChangedEventArgs(string message) : EventArgs
{
    /// <summary>Human-readable description of the change, for callers that report it.</summary>
    public string Message { get; } = message;
}
```

In `src/Factory.Agents/UsageGovernor.cs`, change the event and every site that raises it:

```csharp
    /// <summary>Raised when the governor changes what it will allow, so callers can report it.</summary>
    public event EventHandler<UsageChangedEventArgs>? Changed;
```

There are exactly three raise sites — `UsageGovernor.cs:137`, `:189` and `:193` — each of which
becomes `Changed?.Invoke(this, new UsageChangedEventArgs(<same expression>))`.

There is exactly one subscriber, `src/Factory.Runtime/FactoryHost.cs:140`:

```csharp
        governor.Changed += message => (log ?? (_ => { }))($"  [usage] {message}");
```

becomes:

```csharp
        governor.Changed += (_, e) => (log ?? (_ => { }))($"  [usage] {e.Message}");
```

Confirm nothing was missed with
`grep -rn "Changed?.Invoke\|\.Changed +=" src tests --include='*.cs'`.

- [ ] **Step 5: Run it and watch it pass**

Run: `dotnet test SoftwareFactory.sln --filter "FullyQualifiedName~Usage_governor_reports_changes"`
Expected: PASS.

- [ ] **Step 6: Work the remaining diagnostics to zero**

Rebuild and take them in descending count order:

```bash
dotnet build SoftwareFactory.sln --nologo -v n --no-incremental 2>&1 | tee /tmp/analyzers.log | tail -3
grep -oE "[A-Za-z0-9_./-]+\.cs\([0-9]+,[0-9]+\): warning [A-Z]+[0-9]+" /tmp/analyzers.log \
  | sort -u | grep -oE "warning [A-Z]+[0-9]+" | sort | uniq -c | sort -rn
```

Guidance per rule, so you are not deciding from scratch:

- **CA1002** (do not expose `List<T>`): change the property or return type to
  `IReadOnlyList<T>`. The codebase already uses `IReadOnlyList<T>` throughout `Blueprint` and
  `StationDef`; follow that.
- **CA1849** (call async methods in an async context): `Toolchain.cs:535` and `:562` call
  `File.WriteAllText` inside an `async` method. Change to
  `await File.WriteAllTextAsync(...).ConfigureAwait(false)` and widen the surrounding
  `catch (IOException)` to keep covering it.
- **CA2000** (dispose before losing scope), 10 sites in tests: wrap in `using`. Where the object
  must outlive the statement, assign it to a field on a fixture that is already disposed.
- **CA1859** (use concrete types): change the local or field's declared type to the concrete one.
- **CA1826** (use property instead of `Enumerable` method): replace `.First()`/`.Last()`/
  `.Count()` on a list with `[0]` / `[^1]` / `.Count`.
- **CA1068** (`CancellationToken` must be the last parameter): reorder. Update call sites; the
  compiler finds them all.
- **CA1822** (mark members as static): add `static`.
- **CA1032** (standard exception constructors): `WorkItemStoreException` needs the
  `(string message)` and `(string message, Exception innerException)` constructors.
- **CA1720 / CA1716 / CA1711** (identifier naming): rename. These are public API names —
  if a rename would break the plugin ABI described in `FactoryProviderAttribute`, suppress
  the specific rule with that reason instead of renaming.
- **CA2225** (operator overloads need named alternates): add the named method
  (e.g. an `Add` alongside `operator +` on `TokenUsage`).
- **CA1034** (nested types should not be visible): move the nested type to its own file — the
  repository's file-organization rule requires one top-level type per file anyway.
- **CA1416** (platform compatibility): guard with `OperatingSystem.IsMacOS()` or annotate with
  `[SupportedOSPlatform]`.

Commit in small batches — one rule per commit — rather than one commit at the end.

- [ ] **Step 7: Verify zero**

Run:

```bash
dotnet build SoftwareFactory.sln --nologo -v n --no-incremental 2>&1 | tee /tmp/analyzers.log | tail -3
grep -oE "[A-Za-z0-9_./-]+\.cs\([0-9]+,[0-9]+\): warning [A-Z]+[0-9]+" /tmp/analyzers.log \
  | sort -u | wc -l
```

Expected: **0**.

- [ ] **Step 8: Restore green**

Run: `dotnet test SoftwareFactory.sln --nologo`
Expected: 417 passed (415 + 2 new), 0 failed. Paste the actual line into the commit body.

---

### Task 6: Make warnings fail the build

**Files:**
- Modify: `Directory.Build.props`
- Modify: `docs/superpowers/notes/2026-08-19-phase-1-analyzers.md` (create)

**Interfaces:**
- Consumes: a zero-warning build from Task 5.
- Produces: `TreatWarningsAsErrors` on, solution-wide. Phase 2 (CSharpier) assumes it.

- [ ] **Step 1: Flip the policy**

In `Directory.Build.props`, next to the analyzer properties added in Task 1:

```xml
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
```

- [ ] **Step 2: Prove it holds**

Run: `dotnet build SoftwareFactory.sln --nologo --no-incremental`
Expected: exit 0, zero warnings, zero errors.

- [ ] **Step 3: Prove it bites**

Introduce a deliberate violation and confirm the build now fails:

```bash
printf '\nclass Unused_Name { }\n' >> src/Factory.Core/Ids.cs
dotnet build SoftwareFactory.sln --nologo --no-incremental 2>&1 | tail -5
```

Expected: **FAIL**, with `error CA1707` (or another rule) rather than a warning. A gate that
cannot be shown to fail has not been shown to work.

Then revert:

```bash
git checkout -- src/Factory.Core/Ids.cs
```

- [ ] **Step 4: Restore green, with evidence**

Run: `dotnet test SoftwareFactory.sln --nologo`
Expected: 417 passed, 0 failed.

- [ ] **Step 5: Write the note**

Create `docs/superpowers/notes/2026-08-19-phase-1-analyzers.md` recording: the starting count
(642), the split (471 tests / 171 src), every rule suppressed and why, the eight defects fixed,
and the final build and test output. Phase 5's coverage gate needs this to know which rules are
off and on what grounds.

- [ ] **Step 6: Commit**

```bash
git add Directory.Build.props docs/superpowers/notes/2026-08-19-phase-1-analyzers.md
git commit -m "Make a warning fail the build

Verified both ways: a clean build exits 0, and a deliberately
introduced violation fails it. CheckStation already runs dotnet build,
so every work item is now gated on this without a gate being written."
```

---

## Definition of Done

- [ ] `dotnet build SoftwareFactory.sln --no-incremental` exits 0 with zero warnings.
- [ ] A deliberately introduced violation fails the build — demonstrated, not assumed.
- [ ] `dotnet test SoftwareFactory.sln` reports 417 passed, 0 failed, with output pasted.
- [ ] Every `severity = none` in `.editorconfig` has a comment saying why the rule does not
      apply here.
- [ ] `docs/superpowers/notes/2026-08-19-phase-1-analyzers.md` records the counts, the
      decisions, and the evidence.

## Out of Scope

- CSharpier — Phase 2.
- Splitting `Factory.Tests` into tiers — Phase 3. The analyzer suppressions scoped to
  `tests/**` will apply to the new projects automatically because the glob is path-based.
- Any coverage or complexity threshold — Phases 5.
- `IGate`, the pipeline builder, gate packages — Phase 4 onward.
