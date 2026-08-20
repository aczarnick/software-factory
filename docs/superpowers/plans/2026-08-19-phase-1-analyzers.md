# Phase 1: Analyzers Enforced, Warnings Fail Builds — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `AnalysisLevel=latest-recommended` plus four named opt-in rules runs across the
solution, every resulting diagnostic is fixed, and `TreatWarningsAsErrors` is on so a warning
fails the build.

**Architecture:** Analyzer configuration lives in `.editorconfig` (rule severities, scoped by
path) and `Directory.Build.props` (analysis level, warning policy). No new code and no new
mechanism — `CheckStation` already runs `dotnet build`, so flipping the warning policy is
enforced by the existing pipeline the moment it lands.

**Tech Stack:** .NET 10 (`net10.0`), SDK pinned `10.0.400` via `global.json`, MSBuild,
Roslyn analyzers, xUnit 2.9.2.

**Spec:** `docs/superpowers/specs/2026-08-19-pipeline-gates-design.md` (§5 Phase 1)

## Global Constraints

- Target framework is `net10.0`; SDK is pinned to `10.0.400` with `rollForward: latestFeature`.
- `Nullable`, `ImplicitUsings` are `enable`; `LangVersion` is `latest`. Do not change these.
- One top-level type per file. Nested/private types may stay in their containing type's file.
- XML doc comments only on **public** APIs. No explanatory prose or narration in code.
- **`AnalysisMode=All` is rejected.** It was measured at 642 diagnostics; the 231-diagnostic
  difference was almost entirely policy rules this codebase deliberately does not follow. Do
  not raise the mode. Add individual rules by name instead, and only with a reason.
- **There are no rule suppressions in this plan.** `.editorconfig` must contain zero
  `severity = none` entries at the end of it. Every diagnostic is fixed, not waived. If you
  reach for a suppression, you have found something this plan did not anticipate — stop and
  raise it rather than adding one.
- Every opt-in above Recommended MUST name, in a comment, the defect it caught here.
- Verification is `dotnet build SoftwareFactory.sln` **and** `dotnet test SoftwareFactory.sln`.
  Show actual output. Non-zero exit means not done.
- The full suite takes ~4 minutes and is **not** safe to run concurrently with another
  `dotnet test` against the same worktree — a second run corrupts the first. Run one at a time.
- The suite has **415** tests. That number must be 415 before and after the rename task, and
  417 at the end of this plan. A change in count is a bug, not a detail.

## Measured Starting Point

Measured on this branch at `416c18a`, full rebuild, deduplicated by file/line/rule.

```
AnalysisMode=All            642      (measured, rejected)
AnalysisMode=Recommended    411
  + four opt-in rules       +27
                            ---
TOTAL TO CLEAR              438
  src                        26
  tests                     412
```

### `src` — 26 diagnostics

| Rule | Sites | What it is |
|---|---:|---|
| CA1859 | 4 | Use concrete types for improved performance |
| CA1826 | 4 | Use property instead of `Enumerable` method |
| CA1720 | 4 | Identifier contains type name |
| CA1068 | 3 | `CancellationToken` parameters must come last |
| CA1822 | 2 | Mark members as static |
| CA1716 | 2 | Identifiers should not match keywords |
| CA1849 | 2 | Call async methods in an async context — **opt-in** |
| CA1725 | 1 | Parameter names should match base declaration |
| CA1001 | 1 | Types owning disposable fields should be disposable |
| CA2000 | 1 | Dispose objects before losing scope — **opt-in** |
| CA5392 | 1 | Use `DefaultDllImportSearchPaths` — **opt-in** |
| CA1003 | 1 | Use generic event handler instances — **opt-in** |

### `tests` — 412 diagnostics

| Rule | Sites | What it is |
|---|---:|---|
| CA1707 | 362 | Identifiers should not contain underscores |
| CA1816 | 25 | Call `GC.SuppressFinalize` |
| CA1849 | 12 | Call async methods in an async context — **opt-in** |
| CA2000 | 10 | Dispose objects before losing scope — **opt-in** |
| CA1822 | 1 | Mark members as static |
| CA1711 | 1 | Identifiers should not have incorrect suffix |
| CA1416 | 1 | Validate platform compatibility |

**Read of these numbers:** `src` is nearly clean — 26 sites, of which 4 are genuine defects.
The work is concentrated in one mechanical rename: 362 test methods from snake_case to
PascalCase, per the decision that test projects follow the same naming conventions as product
code. That rename is the risk in this phase, not the difficulty.

### The four opt-in rules

Each is outside Recommended and each located an actual bug here. That is the only admissible
reason to add a rule.

| Rule | Defect | Site |
|---|---|---|
| CA2000 | An `Orchestrator` is leaked **once per delegation** | `DelegateStation.cs:59` |
| CA1849 | Synchronous `File.WriteAllText` inside `async` methods | `Toolchain.cs:535`, `:562` |
| CA5392 | `DllImport("libc")` unconstrained, while cwd is a repo being edited | `CliAgentTransport.cs:228` |
| CA1003 | `Changed` is `Action<string>`, not a standard event | `UsageGovernor.cs:55` |

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `.editorconfig` | Rule severities and per-path scoping, each decision justified | Create |
| `Directory.Build.props` | Analysis level and warning policy | Modify |
| `src/Factory.Runtime/Workspace.cs` | CA1001 — owns a `SemaphoreSlim`, is not disposable | Modify |
| `src/Factory.Runtime/DelegateStation.cs` | CA2000 — `Orchestrator` created, never disposed | Modify |
| `src/Factory.Runtime/Toolchain.cs` | CA1849 — sync file writes inside async methods | Modify |
| `src/Factory.Runtime/Providers/Beads/BeadsWorkItemStore.cs` | CA1725 — parameter name diverges from interface | Modify |
| `src/Factory.Agents/CliAgentTransport.cs` | CA5392 — `DllImport` without a search-path attribute | Modify |
| `src/Factory.Agents/UsageGovernor.cs` | CA1003 — `Action<string>` event | Modify |
| `src/Factory.Agents/UsageChangedEventArgs.cs` | The event's payload type | Create |
| `src/Factory.Runtime/FactoryHost.cs:140` | The only `Changed` subscriber | Modify |
| `tests/Factory.Tests/*.cs` | CA1707 — 362 method renames | Modify |

`.editorconfig` is the only new configuration file, and it has a single `[*.cs]` section — one
definition of the bar, applied identically to product and test code. There is no test-scoped
section, because nothing is waived for tests.

---

### Task 1: Turn analyzers on and record the count

Nothing is fixed here. This makes the problem visible and reproducible so every later task is
measured against a number rather than an impression.

**Files:**
- Modify: `Directory.Build.props:10`
- Create: `.editorconfig`

**Interfaces:**
- Consumes: nothing.
- Produces: `.editorconfig` at the repository root with a `[*.cs]` section; `AnalysisLevel` and
  `EnforceCodeStyleInBuild` set solution-wide. Later tasks add sections to this same file.

- [ ] **Step 1: Add the analyzer properties**

In `Directory.Build.props`, inside the existing `<PropertyGroup>`, immediately after the
`<NoWarn>` line:

```xml
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
```

Do **not** add `TreatWarningsAsErrors` yet. Task 6 does that, once the count is zero.

- [ ] **Step 2: Create `.editorconfig` with the opt-in rules**

Create `.editorconfig` at the repository root:

```ini
# Analyzer policy for the Software Factory.
#
# The floor is AnalysisMode=Recommended, set in Directory.Build.props. AnalysisMode=All was
# measured at 642 diagnostics against Recommended's 411, and nearly all of the difference was
# rules this codebase deliberately does not follow — CA1062 argument null checks (redundant
# under Nullable enable), CA2007 ConfigureAwait (no synchronisation context in an application),
# CA1063 full IDisposable ceremony (on sealed, managed-only types). Suppressing those would
# have made this file mostly justifications for ignoring the analyzer.
#
# A rule is added above Recommended only when it found an actual bug here. Each opt-in below
# names the bug.

root = true

[*.cs]
indent_style = space
indent_size = 4
end_of_line = lf
insert_final_newline = true
charset = utf-8

# CA2000: dispose objects before losing scope.
# DelegateStation created an Orchestrator (sealed, IDisposable) per delegated work item and
# never disposed it — one leak per delegation, for the life of the process.
dotnet_diagnostic.CA2000.severity = warning

# CA1849: call async methods in an async context.
# Toolchain wrote its baseline with synchronous File.WriteAllText from inside async methods,
# blocking a pool thread on disk for every baseline capture.
dotnet_diagnostic.CA1849.severity = warning

# CA5392: use DefaultDllImportSearchPaths.
# CliAgentTransport declared DllImport("libc") with no search-path constraint, which permits
# the loader to search the current working directory. This process runs with its working
# directory set to a repository it is actively modifying.
dotnet_diagnostic.CA5392.severity = warning

# CA1003: use generic event handler instances.
# UsageGovernor.Changed was Action<string>, so it could not carry additional context without a
# breaking signature change, and did not follow the sender/args convention callers expect.
dotnet_diagnostic.CA1003.severity = warning
```

- [ ] **Step 3: Rebuild and count**

```bash
dotnet build SoftwareFactory.sln --nologo -v n --no-incremental 2>&1 | tee /tmp/an.log | tail -5
grep -oE "[A-Za-z0-9_./-]+\.cs\([0-9]+,[0-9]+\): warning [A-Z]+[0-9]+" /tmp/an.log \
  | sort -u > /tmp/uniq.txt
echo "total:"; wc -l < /tmp/uniq.txt
echo "src:";   grep -c "/src/"   /tmp/uniq.txt
echo "tests:"; grep -c "/tests/" /tmp/uniq.txt
```

Expected: build exits 0 (warnings do not fail yet); total **438**, src **26**, tests **412**.

If the totals differ, the SDK has moved. Do not proceed on a guess — record the new counts,
regenerate the per-rule tables with the command in Step 4, and use those for the rest of the
plan.

- [ ] **Step 4: Record the per-rule split**

```bash
echo "=== src ==="
grep "/src/"   /tmp/uniq.txt | grep -oE "warning [A-Z]+[0-9]+" | sort | uniq -c | sort -rn
echo "=== tests ==="
grep "/tests/" /tmp/uniq.txt | grep -oE "warning [A-Z]+[0-9]+" | sort | uniq -c | sort -rn
```

Expected: the two tables in "Measured Starting Point" above.

- [ ] **Step 5: Commit**

```bash
git add Directory.Build.props .editorconfig
git commit -m "Turn the analyzers on, at Recommended rather than All

All was measured at 642 diagnostics against Recommended's 411, and the
difference was almost entirely rules this codebase deliberately does not
follow. Four rules outside Recommended are added back by name, because
each one found a real bug: a per-delegation Orchestrator leak, sync file
writes inside async methods, an unconstrained native library search
path, and a non-standard event signature.

438 diagnostics: 26 in src, 412 in tests. Enforcement comes once that
count is zero."
```

---

### Task 2: Seal the test fixtures

CA1816 (25 sites, all in tests) asks for `GC.SuppressFinalize(this)` in a `Dispose`. All 25
sites are the same line — `public void Dispose() => TempDir.Delete(_dir);` — which deletes a
temp directory. There is no managed resource and no finalizer, so `GC.SuppressFinalize` would
suppress nothing.

What the analyzer is actually pointing at is that every one of these is a **non-sealed
`public class`** with a non-virtual `Dispose()`: a derived class could not extend cleanup. The
same fact is why CA1063 fired 50 times under `AnalysisMode=All` — 25 classes, two rules.

`sealed` is therefore the fix, and it was verified empirically: sealing all 25 takes CA1816
from 25 to **0**. No suppression and no ceremony.

No test class is inherited from — confirmed by grep in Step 1 — so sealing changes no behaviour.
xUnit constructs a fresh instance per test and calls `Dispose` after it; sealing does not affect
either.

**Files:**
- Modify: 25 files under `tests/Factory.Tests/`

**Interfaces:**
- Consumes: `.editorconfig` from Task 1.
- Produces: no API change. `.editorconfig` gains nothing — there is no `[tests/**/*.cs]`
  section in this plan.

- [ ] **Step 1: Confirm nothing inherits from a test class**

```bash
grep -rnE "public class [A-Za-z0-9]+ : [A-Za-z]" tests --include='*.cs' | grep -v IDisposable
```

Expected: empty. A hit means that class *is* a base and must not be sealed — exclude it and
handle it separately.

- [ ] **Step 2: Seal them**

```bash
python3 - <<'PY'
import pathlib, re
total = 0
for path in pathlib.Path('tests').rglob('*.cs'):
    text = path.read_text()
    sealed = re.sub(r'\bpublic class ([A-Za-z0-9]+) : IDisposable\b',
                    r'public sealed class \1 : IDisposable', text)
    if sealed != text:
        total += len(re.findall(r'\bpublic class [A-Za-z0-9]+ : IDisposable\b', text))
        path.write_text(sealed)
print("sealed", total, "classes")
PY
```

Expected: `sealed 25 classes`.

- [ ] **Step 3: Rebuild and verify the drop**

```bash
dotnet build SoftwareFactory.sln --nologo -v n --no-incremental 2>&1 | tee /tmp/an.log | tail -3
grep -c "warning CA1816" /tmp/an.log
grep -oE "[A-Za-z0-9_./-]+\.cs\([0-9]+,[0-9]+\): warning [A-Z]+[0-9]+" /tmp/an.log \
  | sort -u | wc -l
```

Expected: CA1816 count **0**; total **413** (438 − 25).

- [ ] **Step 4: Run the suite**

```bash
dotnet test SoftwareFactory.sln --nologo 2>&1 | tail -3
```

Expected: 415 passed, 0 failed. Sealing is behaviour-neutral, so any change here is a real
problem.

- [ ] **Step 5: Commit**

```bash
git add tests
git commit -m "Seal the test fixtures instead of waiving the rule about them

All 25 CA1816 sites are the same line -- Dispose deleting a temp
directory. There is no managed resource and no finalizer, so
GC.SuppressFinalize would suppress nothing. What the analyzer is
actually pointing at is that each of these is a non-sealed public class
with a non-virtual Dispose, which a derived class could not extend.
Nothing inherits from them, so sealing is free and takes CA1816 to zero.

Same 25 diagnostics cleared either way; this way .editorconfig keeps no
suppressions at all."
```

---

### Task 3: Rename 362 test methods to PascalCase

The largest task and the only risky one. Renaming is mechanical; losing a test to it is not.
The guard is the test count: **415 before, 415 after**.

**Files:**
- Modify: `tests/Factory.Tests/*.cs` (all 34 files)

**Interfaces:**
- Consumes: `.editorconfig` from Tasks 1–2.
- Produces: no API change — test method names only. No product code is touched.

- [ ] **Step 1: Record the exact pre-rename count**

```bash
dotnet test SoftwareFactory.sln --nologo 2>&1 | tail -3
```

Expected: `Passed! - Failed: 0, Passed: 415, Skipped: 0, Total: 415`. Write the number down.
If it is not 415, stop — the baseline moved and this plan's guard is invalid.

- [ ] **Step 2: Check for names referenced outside the test source**

A rename breaks anything that names a test as a string.

```bash
grep -rn "FullyQualifiedName" . --include='*.md' --include='*.json' --include='*.sh' \
  --include='*.props' --include='*.csproj' 2>/dev/null | grep -v '/obj/\|/bin/'
grep -rn "_" docs/superpowers/notes --include='*.md' | grep -E "\b[A-Z][a-z]+(_[a-z]+){2,}" | head
```

Record every hit. Each one must be updated in Step 6 or the reference goes stale.

- [ ] **Step 3: List the methods to rename**

```bash
grep -rhoE "public (async Task|void) [A-Za-z0-9]+(_[A-Za-z0-9]+)+\(" tests --include='*.cs' \
  | sed -E 's/public (async Task|void) //; s/\($//' | sort -u > /tmp/renames.txt
wc -l < /tmp/renames.txt
```

Expected: 362 or slightly fewer — a name appearing in two files counts once here but twice in
the diagnostic count. Record both numbers.

- [ ] **Step 4: Check for collisions before renaming anything**

Two distinct snake_case names can collapse to the same PascalCase name, which silently deletes
a test.

```bash
python3 - <<'PY'
import re, pathlib
names = pathlib.Path('/tmp/renames.txt').read_text().split()
def pascal(n):
    return ''.join(p[:1].upper() + p[1:] for p in n.split('_') if p)
seen = {}
for n in names:
    seen.setdefault(pascal(n), []).append(n)
clashes = {k: v for k, v in seen.items() if len(v) > 1}
print(f"{len(names)} names -> {len(seen)} PascalCase")
for k, v in clashes.items():
    print("COLLISION", k, v)
PY
```

Expected: no `COLLISION` lines. If any appear, resolve them by hand — give one of the pair a
distinguishing word — before running the bulk rename.

- [ ] **Step 5: Apply the rename**

Only the method *declaration* names change. Test methods are never called by name from other
code, so declaration-site renaming is sufficient — but the script below also rewrites
`nameof(...)` and `[MemberData]`/`[Theory]` references to the same identifiers, so a fixture
that names a method still compiles.

```python
# /tmp/rename_tests.py
import pathlib, re

def pascal(name: str) -> str:
    return ''.join(p[:1].upper() + p[1:] for p in name.split('_') if p)

decl = re.compile(r'\b(public\s+(?:async\s+Task|void)\s+)([A-Za-z0-9]+(?:_[A-Za-z0-9]+)+)\b')
mapping: dict[str, str] = {}

files = list(pathlib.Path('tests').rglob('*.cs'))

for path in files:
    for _, name in decl.findall(path.read_text()):
        mapping[name] = pascal(name)

for path in files:
    text = original = path.read_text()
    for old, new in mapping.items():
        text = re.sub(rf'\b{re.escape(old)}\b', new, text)
    if text != original:
        path.write_text(text)

print(f"renamed {len(mapping)} identifiers across {len(files)} files")
```

Run it:

```bash
python3 /tmp/rename_tests.py
```

- [ ] **Step 6: Update any external references found in Step 2**

Edit each file recorded in Step 2 to use the new PascalCase name. If Step 2 found nothing,
this step is a no-op — say so rather than skipping it silently.

- [ ] **Step 7: Verify the count is unchanged**

```bash
dotnet test SoftwareFactory.sln --nologo 2>&1 | tail -3
```

Expected: `Passed! - Failed: 0, Passed: 415, Skipped: 0, Total: 415`.

**415 is the gate.** A lower number means the rename merged or hid a test. If it is not 415,
revert with `git checkout -- tests` and resolve the collision before retrying.

- [ ] **Step 8: Verify CA1707 is gone**

```bash
dotnet build SoftwareFactory.sln --nologo -v n --no-incremental 2>&1 | tee /tmp/an.log | tail -3
grep -c "warning CA1707" /tmp/an.log
grep -oE "[A-Za-z0-9_./-]+\.cs\([0-9]+,[0-9]+\): warning [A-Z]+[0-9]+" /tmp/an.log \
  | sort -u | wc -l
```

Expected: CA1707 count **0**; total **51** (413 − 362).

- [ ] **Step 9: Commit**

```bash
git add tests
git commit -m "Name test methods the way the rest of the codebase is named

362 snake_case test methods become PascalCase. Test projects follow the
same naming conventions as product code; CA1707 is enforced everywhere
rather than waived for tests.

Test count is unchanged at 415, which is the check that matters here --
a rename that collapses two names into one would silently delete a test."
```

---

### Task 4: Fix the four defects the opt-in rules caught

Each of these is why its rule was added. Fix them, and the opt-in has paid for itself.

**Files:**
- Modify: `src/Factory.Runtime/DelegateStation.cs:59`
- Modify: `src/Factory.Runtime/Toolchain.cs:535,562`
- Modify: `src/Factory.Agents/CliAgentTransport.cs:228`
- Modify: `src/Factory.Agents/UsageGovernor.cs:55,137,189,193`
- Create: `src/Factory.Agents/UsageChangedEventArgs.cs`
- Modify: `src/Factory.Runtime/FactoryHost.cs:140`
- Test: `tests/Factory.Tests/AgentTests.cs`

**Interfaces:**
- Consumes: `.editorconfig` from Tasks 1–2.
- Produces:
  - `UsageChangedEventArgs` — public sealed class in `Factory.Agents`, constructor
    `UsageChangedEventArgs(string message)`, property `public string Message { get; }`.
  - `UsageGovernor.Changed` — type changes from `Action<string>?` to
    `EventHandler<UsageChangedEventArgs>?`.

- [ ] **Step 1: Dispose the delegated orchestrator**

`DelegateStation.cs:59` creates an `Orchestrator` — `sealed class Orchestrator : IDisposable`
— and never disposes it. One leak per delegated work item.

Change:

```csharp
        var report = await child.CreateOrchestrator().RunAsync(new OrchestratorOptions
        {
            StopWhenIdle = true,
            Depth = ctx.Run.Depth + 1,
            MaxConcurrency = 1
        }, ctx.Ct).ConfigureAwait(false);
```

to:

```csharp
        using var orchestrator = child.CreateOrchestrator();
        var report = await orchestrator.RunAsync(new OrchestratorOptions
        {
            StopWhenIdle = true,
            Depth = ctx.Run.Depth + 1,
            MaxConcurrency = 1
        }, ctx.Ct).ConfigureAwait(false);
```

- [ ] **Step 2: Make the baseline writes async**

`Toolchain.cs:535` and `:562` call synchronous `File.WriteAllText` inside `async` methods,
blocking a pool thread on disk. Both sites have the same shape:

```csharp
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
            File.WriteAllText(cachePath, FactoryJson.Write(baseline, pretty: true));
        }
        catch (IOException) { }
```

becomes:

```csharp
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
            await File.WriteAllTextAsync(cachePath, FactoryJson.Write(baseline, pretty: true))
                      .ConfigureAwait(false);
        }
        catch (IOException) { }
```

At `:562` the local is named `fresh`, not `baseline` — substitute accordingly. Do not widen the
`catch`: `WriteAllTextAsync` throws the same `IOException` family.

- [ ] **Step 3: Constrain the native library search path**

`CliAgentTransport.cs:228` declares `DllImport("libc")` with no search-path constraint, which
permits the loader to search the current working directory — and this process runs with its
working directory set to a repository it is actively modifying.

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

- [ ] **Step 4: Write the failing test for the event signature**

`ObserveRejection` is the public trigger: it calls the private `Record`, whose snapshot has a
status other than `Allowed`, which raises `Changed` (`UsageGovernor.cs:107`, `:125`, `:137`).

Add to `tests/Factory.Tests/AgentTests.cs`:

```csharp
[Fact]
public void UsageGovernorReportsARejectionThroughAStandardEvent()
{
    var governor = new UsageGovernor();
    string? reported = null;
    governor.Changed += (_, e) => reported = e.Message;

    governor.ObserveRejection("rate limit exceeded");

    Assert.NotNull(reported);
}
```

The assertion is on delivery, not on wording: the message is composed by
`RateLimitSnapshot.Describe` from a clock reading, and asserting its text would test the
formatter rather than the event contract this task changes. The method name is PascalCase
because Task 3 already made that the convention.

- [ ] **Step 5: Run it and watch it fail**

```bash
dotnet test SoftwareFactory.sln --filter "FullyQualifiedName~UsageGovernorReportsARejection"
```

Expected: FAIL — the lambda takes two parameters and `Action<string>` supplies one.

- [ ] **Step 6: Introduce the event args type**

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

In `src/Factory.Agents/UsageGovernor.cs:55`:

```csharp
    /// <summary>Raised when the governor changes what it will allow, so callers can report it.</summary>
    public event EventHandler<UsageChangedEventArgs>? Changed;
```

There are exactly three raise sites. Each becomes
`Changed?.Invoke(this, new UsageChangedEventArgs(<the same expression>))`:

- `:137` — `snapshot.Describe(_clock.GetUtcNow())`
- `:189` — `$"{reason} — longer than the {RateLimitSnapshot.Format(Policy.MaxWait)} wait ceiling, stopping"`
- `:193` — `$"{reason} — waiting {RateLimitSnapshot.Format(wait)}"`

- [ ] **Step 7: Update the only subscriber**

`src/Factory.Runtime/FactoryHost.cs:140`:

```csharp
        governor.Changed += message => (log ?? (_ => { }))($"  [usage] {message}");
```

becomes:

```csharp
        governor.Changed += (_, e) => (log ?? (_ => { }))($"  [usage] {e.Message}");
```

Confirm nothing was missed:

```bash
grep -rn "Changed?.Invoke\|\.Changed +=" src tests --include='*.cs'
```

Expected: three raise sites in `UsageGovernor.cs`, one subscriber in `FactoryHost.cs`, one in
the new test. Nothing else.

- [ ] **Step 8: Run the full suite**

```bash
dotnet test SoftwareFactory.sln --nologo 2>&1 | tail -3
```

Expected: 416 passed, 0 failed.

- [ ] **Step 9: Commit**

```bash
git add src tests
git commit -m "Fix the four defects that justified the opt-in rules

Every delegation leaked an Orchestrator. Toolchain blocked a pool thread
writing its baseline synchronously from an async method. A DllImport with
no search path let the loader look in the working directory, which for
this process is a repository it is in the middle of editing. And Changed
was an Action<string>, so it could not carry context without a breaking
change.

Each of these is why its rule was added above Recommended."
```

---

### Task 5: Clear the remaining 46

What is left after Tasks 2–4: **21 in `src`, 25 in `tests`**, across twelve rules. Each is
small; none is architectural.

The running total, so a task that lands wrong is visible immediately:

```
Task 1  turn on                        438   (src 26, tests 412)
Task 2  seal 25 fixtures,   tests  -25 413   (src 26, tests 387)
Task 3  CA1707 renames,     tests -362  51   (src 26, tests  25)
Task 4  four defects fixed,   src   -5  46   (src 21, tests  25)
Task 5  the rest                         0
```

**Files:**
- Modify: `src/Factory.Runtime/Workspace.cs:13`
- Modify: `src/Factory.Runtime/Providers/Beads/BeadsWorkItemStore.cs:56`
- Modify: various under `src/` and `tests/` as the build reports them
- Test: `tests/Factory.Tests/RuntimeTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–4.
- Produces:
  - `Workspace : IDisposable` — `public void Dispose()`.
  - `BeadsWorkItemStore.TryClaim(string owner)` — parameter renamed from `claimant`.

- [ ] **Step 1: Write the failing test for `Workspace` disposal**

`Workspace` holds `private readonly SemaphoreSlim _integrateGate = new(1, 1);` and never
releases it (CA1001). Add to `tests/Factory.Tests/RuntimeTests.cs`:

```csharp
[Fact]
public void WorkspaceReleasesItsIntegrationGateWhenDisposed()
{
    var root = Directory.CreateTempSubdirectory().FullName;
    var workspace = new Workspace(root, new FactoryPaths(root));

    workspace.Dispose();

    Assert.Throws<ObjectDisposedException>(() => workspace.Dispose());
}
```

A second `Dispose` on a disposed `SemaphoreSlim` throwing is the observable proof the semaphore
was really disposed. Do not make `Dispose` idempotent to make this pass — that would leave the
assertion proving nothing.

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test SoftwareFactory.sln --filter "FullyQualifiedName~WorkspaceReleasesItsIntegrationGate"
```

Expected: FAIL — `Workspace` has no `Dispose`.

- [ ] **Step 3: Make `Workspace` disposable**

In `src/Factory.Runtime/Workspace.cs`:

```csharp
public sealed class Workspace(string repoRoot, FactoryPaths paths) : IDisposable
{
    private readonly SemaphoreSlim _integrateGate = new(1, 1);

    public void Dispose() => _integrateGate.Dispose();
```

`Workspace` is already `sealed` and holds only a managed `SemaphoreSlim` with no finalizer, so a
bare `Dispose` is correct and CA1816 does not fire — the same reason Task 2 sealed the test
fixtures. Do not add the full IDisposable pattern.

- [ ] **Step 4: Run it and watch it pass**

```bash
dotnet test SoftwareFactory.sln --filter "FullyQualifiedName~WorkspaceReleasesItsIntegrationGate"
```

Expected: PASS.

- [ ] **Step 5: Match the interface parameter name**

`BeadsWorkItemStore.cs:56` declares `TryClaim(string claimant)` while
`IWorkItemStore.TryClaim(string owner)` (`IWorkItemStore.cs:16`) names it `owner` (CA1725). A
caller using a named argument against the interface breaks on the concrete type.

```csharp
    public WorkItem? TryClaim(string owner) =>
        cli.Json<BeadRecord>([.. BeadMapper.ClaimArgs(owner)])
           .Select(BeadMapper.ToWorkItem)
           .FirstOrDefault();
```

Then check for named-argument call sites and update any found:

```bash
grep -rn "claimant" src tests --include='*.cs'
```

- [ ] **Step 6: Work the rest to zero**

Rebuild and take them in descending count order:

```bash
dotnet build SoftwareFactory.sln --nologo -v n --no-incremental 2>&1 | tee /tmp/an.log | tail -3
grep -oE "[A-Za-z0-9_./-]+\.cs\([0-9]+,[0-9]+\): warning [A-Z]+[0-9]+" /tmp/an.log \
  | sort -u > /tmp/uniq.txt
wc -l < /tmp/uniq.txt
cat /tmp/uniq.txt | sed 's#.*/guardrails/##'
```

Guidance per rule, so nothing is decided from scratch:

- **CA1859** (use concrete types), 4 in src: change the local or field's declared type to the
  concrete one the analyzer names.
- **CA1826** (use property instead of `Enumerable` method), 4 in src: replace `.First()` /
  `.Last()` / `.Count()` on a list with `[0]` / `[^1]` / `.Count`.
- **CA1720** (identifier contains type name), 4 in src: rename. One is
  `AgentEvent.cs:62`. If a rename would break the plugin ABI described in
  `FactoryProviderAttribute`, suppress CA1720 for that one site with that reason instead.
- **CA1068** (`CancellationToken` must be last), 3 in src: reorder the parameter. The compiler
  finds every call site.
- **CA1822** (mark members as static), 2 in src + 1 in tests: add `static`.
- **CA1716** (identifiers should not match keywords), 2 in src: rename. Same ABI caveat as
  CA1720.
- **CA2000** (dispose before losing scope), 10 in tests: wrap in `using`. Where the object must
  outlive the statement, assign it to a fixture field that is already disposed.
- **CA1849** (async in async context), 12 in tests: `await` the async overload.
- **CA1711** (incorrect suffix), 1 in tests: rename.
- **CA1416** (platform compatibility), 1 in tests: guard with `OperatingSystem.IsMacOS()` or
  annotate the member with `[SupportedOSPlatform]`.

Commit in small batches — one rule per commit — not one commit at the end.

- [ ] **Step 7: Verify zero**

```bash
dotnet build SoftwareFactory.sln --nologo -v n --no-incremental 2>&1 | tee /tmp/an.log | tail -3
grep -oE "[A-Za-z0-9_./-]+\.cs\([0-9]+,[0-9]+\): warning [A-Z]+[0-9]+" /tmp/an.log \
  | sort -u | wc -l
```

Expected: **0**.

- [ ] **Step 8: Restore green**

```bash
dotnet test SoftwareFactory.sln --nologo 2>&1 | tail -3
```

Expected: 417 passed, 0 failed. Paste the actual line into the commit body.

---

### Task 6: Make warnings fail the build

**Files:**
- Modify: `Directory.Build.props`
- Create: `docs/superpowers/notes/2026-08-19-phase-1-analyzers.md`

**Interfaces:**
- Consumes: a zero-warning build from Task 5.
- Produces: `TreatWarningsAsErrors` on, solution-wide. Phase 2 (CSharpier) assumes it.

- [ ] **Step 1: Flip the policy**

In `Directory.Build.props`, next to the analyzer properties from Task 1:

```xml
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
```

- [ ] **Step 2: Prove it holds**

```bash
dotnet build SoftwareFactory.sln --nologo --no-incremental
```

Expected: exit 0, zero warnings, zero errors.

- [ ] **Step 3: Prove it bites**

A gate that cannot be shown to fail has not been shown to work. Introduce a violation of a
rule that is definitely enabled — CA1707, which Task 3 just cleared:

```bash
printf '\nnamespace Factory.Core;\npublic sealed class Bad_Name { }\n' > src/Factory.Core/BadName.cs
dotnet build SoftwareFactory.sln --nologo --no-incremental 2>&1 | tail -5
```

Expected: **build FAILS** with `error CA1707` — an error, not a warning.

Then remove it:

```bash
rm -f src/Factory.Core/BadName.cs
dotnet build SoftwareFactory.sln --nologo --no-incremental 2>&1 | tail -3
```

Expected: exit 0 again.

- [ ] **Step 4: Restore green, with evidence**

```bash
dotnet test SoftwareFactory.sln --nologo 2>&1 | tail -3
```

Expected: 417 passed, 0 failed.

- [ ] **Step 5: Write the note**

Create `docs/superpowers/notes/2026-08-19-phase-1-analyzers.md` recording:

- Both measurements: `AnalysisMode=All` at 642, `Recommended` at 411, and why All was rejected.
- The four opt-in rules and the defect each one found.
- That CA1816's 25 sites were cleared by sealing the fixtures, not by a suppression, and that
  `.editorconfig` therefore contains no waivers at all.
- The CA1707 decision — tests follow product naming conventions — and that 362 methods were
  renamed with the test count held at 415.
- The final build and test output, pasted.

Phase 5's coverage gate needs this to know which rules are off and on what grounds.

- [ ] **Step 6: Commit**

```bash
git add Directory.Build.props docs/superpowers/notes/2026-08-19-phase-1-analyzers.md
git commit -m "Make a warning fail the build

Verified both ways: a clean build exits 0, and a deliberately introduced
CA1707 violation fails it as an error. CheckStation already runs dotnet
build, so every work item is gated on this without a gate being written."
```

---

## Definition of Done

- [ ] `dotnet build SoftwareFactory.sln --no-incremental` exits 0 with zero warnings.
- [ ] A deliberately introduced violation **fails** the build — demonstrated, then removed.
- [ ] `dotnet test SoftwareFactory.sln` reports **417 passed, 0 failed**, with output pasted.
- [ ] The test count was 415 immediately before and immediately after the CA1707 rename.
- [ ] `.editorconfig` contains **zero** `severity = none` entries. Every diagnostic was fixed,
      none waived. Verify with `grep -c "severity = none" .editorconfig` returning 0.
- [ ] Each of the four opt-in rules names the defect it found.
- [ ] `docs/superpowers/notes/2026-08-19-phase-1-analyzers.md` records both measurements, the
      decisions, and the evidence.

## Out of Scope

- CSharpier — Phase 2.
- Splitting `Factory.Tests` into tiers — Phase 3. `.editorconfig` has one `[*.cs]` section with
  no path scoping, so it applies to the new projects with no change.
- Any coverage or complexity threshold — Phase 5.
- `IGate`, the pipeline builder, gate packages — Phase 4 onward.
- Raising `AnalysisMode` to `All`. Measured and rejected; see Global Constraints.
