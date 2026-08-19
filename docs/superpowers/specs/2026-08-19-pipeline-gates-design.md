# Declarative Pipelines and Gates — Design

**Date:** 2026-08-19
**Status:** Approved for planning
**Branch:** `worktree-guardrails`

---

## 1. Why

The harness enforces almost nothing on itself. There is no `.editorconfig`, no analyzer
configuration, no formatter, no warning policy, no coverage threshold, no complexity
threshold, no security scan. `Factory.Tests` is one project of 415 tests that takes
3m46s and mixes pure logic with real processes, timers and git.

The factory also cannot express a quality bar for the software it produces. `CheckStation`
calls `Toolchain.Detect()`, which hardcodes one list of commands per language. A pipeline
cannot say "this repository requires 100% unit coverage and a cyclomatic ceiling of 10",
because there is nowhere to say it.

Both are the same missing thing: **gates are not a declarable concept.**

### 1.1 Measured baseline

Captured on `worktree-guardrails` at `07b5e6b`, whole suite, before any change:

```
build                     exit 0
tests                     415 passed, 0 failed, 3m46s

coverage (all 415 tests, unit and integration combined)
  OVERALL                 line  72.4%   branch  56.2%
  Factory.Core            line  93.8%   branch  77.0%
  Factory.Runtime         line  84.0%   branch  72.4%
  Factory.Evolution       line  79.7%   branch  60.9%
  Factory.Agents          line  68.6%   branch  56.4%
  factory (CLI)           line  15.5%   branch   8.6%
```

Unit-only coverage will be lower than these figures, because they include the integration
tests. The distance from here to a 100% unit gate is the largest single item in this
programme, and `Factory.Cli` is most of it.

---

## 2. Principles

1. **One mechanism, both surfaces.** The harness and the software the factory produces are
   gated by the same system. This repository is its own first customer; a gate too weak or
   too flaky is discovered by the factory failing on itself.
2. **A gate is data.** Every node in a pipeline is serialisable, hashable, and attributable
   to a package and a resolved version. This preserves the existing content-hash response
   cache, the ledger's evidence guarantee, and `factory doctor`'s ability to answer *what
   will run* without running it.
3. **Extend, do not rewrite.** Crash-resume is the repository's headline durability
   guarantee. The orchestrator keeps a single station cursor per item.
4. **Fail at load, not mid-item.** Depth violations, cycles, unknown providers and unknown
   route targets are load-time errors, surfaced by `factory doctor`, costing zero tokens.
5. **Deterministic before inferential.** A gate that a compiler or a coverage tool can
   decide is never given to a model.

---

## 3. Architecture

### 3.1 The pipeline is C#

A pipeline definition is a C# class implementing `IPipelineDefinition`. It is a **builder**,
not a runtime: it executes once at load and returns a `Blueprint`, which serialises to
`.factory/blueprint.json`.

```csharp
public sealed class FactoryPipeline : IPipelineDefinition
{
    public Blueprint Build(IPipelineBuilder b) => b
        .Station("implement", s => s.Tier(ModelTier.Opus).Thick())
        .Station("check", s => s
            .Parallel(
                Shell.Run("dotnet csharpier check ."),
                DotnetCoverage.Unit(100).Integration(80),
                Complexity.MaxCyclomatic(10),
                Security.VulnerablePackages())
            .OnFail("implement"))
        .Station("integration", s => s
            .Setup(Container.Compose("./test/compose.yml").WaitHealthy())
            .Steps(Shell.Run("dotnet test tests/Factory.IntegrationTests"))
            .Teardown(Container.Down()))
        .Build();
}
```

The orchestrator, ledger, crash-resume, `doctor` and response cache continue to consume
`blueprint.json` unchanged. They never see C#.

**A gate may not be a lambda.** Every node is `uses(provider, options)`. An opaque node
could not be content-hashed for the cache, could not be diffed, and would reduce in the
ledger to a bare name — which contradicts principle 2. Custom logic ships as a gate package.

### 3.2 Version pinning is `PackageReference`

Gate implementations are distributed as NuGet packages. The pipeline project pins them the
way any .NET project pins anything:

```xml
<PackageReference Include="Factory.Gates.Dotnet" Version="1.*" />
```

This supplies, with no new syntax and no new machinery:

| Requirement | Mechanism |
|---|---|
| Float within major (`@1`) | `Version="1.*"` |
| Reproducible resolution | `packages.lock.json` |
| Cross-repo reuse | the package |
| Namespacing / collision rules | package id + `[FactoryProvider]` name |
| Deliberate upgrade | `dotnet restore --force-evaluate` |

`ProviderRef` is **unchanged**. A gate package registers providers by name through the
existing `[FactoryProvider("dotnet-coverage")]` attribute. The version is *discovered*, not
declared: it is read from the loaded assembly's informational version and stamped into the
run record.

```
RunRecord {
  StationId    = "check"
  GateId       = "coverage"
  GateProvider = "dotnet-coverage"
  GateVersion  = "Factory.Gates.Dotnet@1.4.2"
  Verdict      = Fail
  Detail       = "unit line 94.2% < 100%"
}
```

### 3.3 Where a pipeline comes from

Two sources, one mechanism, local wins:

1. **Package (default).** `factory.json` names a pipeline package. The factory restores it,
   loads it, calls `Build()`, writes `blueprint.json`. Nothing compiles in the target repo,
   so deploying into a Python or Rust repository adds no `.csproj`.
2. **Local project (override).** A pipeline project committed **with the source**, default
   `pipeline/` at the repository root, path configurable in `factory.json`. Treated like
   `infra/` or `.github/workflows/` — pipeline-as-code belongs next to the code it gates.

`.factory/` is **not** a candidate: it is gitignored, so a definition placed there would
never be version controlled.

`factory doctor` always prints which source resolved, and to what version.

Compilation of a local pipeline project is cached and staleness-checked against source
hashes, following the existing `HarnessStaleness` pattern.

### 3.4 The gate node model

One serialisable recursive record. A node is either a **leaf** (has `Uses`) or a
**composite** (has children) — never both, enforced by `Validate()`.

```csharp
public sealed record GateNode
{
    public required string Id { get; init; }

    /// <summary>Leaf: the provider that evaluates this gate. Null on a composite.</summary>
    public ProviderRef? Uses { get; init; }

    /// <summary>Composite: how Steps run. Setup and Teardown are always sequential.</summary>
    public GateMode Mode { get; init; } = GateMode.Sequential;

    public IReadOnlyList<GateNode> Setup { get; init; } = [];
    public IReadOnlyList<GateNode> Steps { get; init; } = [];
    public IReadOnlyList<GateNode> Teardown { get; init; } = [];

    /// <summary>Hard ceiling on this node's evaluation. Mirrors ToolchainCheck.TimeoutSeconds.</summary>
    public int TimeoutSeconds { get; init; } = 600;

    /// <summary>Station to route to when this gate fails. Falls back to the station's OnFail.</summary>
    public string? OnFail { get; init; }

    /// <summary>Advisory gate: records a verdict, never blocks. For staged adoption.</summary>
    public bool Advisory { get; init; }
}
```

`StationDef` gains `IReadOnlyList<GateNode> Gates`. Nothing else on `StationDef` changes;
tier, profile, budget, prompt and retries remain station-level concepts, because they are
meaningless on a node whose whole job is `csharpier check`.

### 3.5 Flow

Station-to-station flow stays a **linear cursor**: `Blueprint.Pipeline` is an ordered list,
`OnFail` is the edge back, and `WorkItem.Station` remains a single value in the ledger.
Crash-resume is untouched.

Parallelism and sequence live **inside** a station's gate tree, which is where CI
parallelism actually is. `check` runs format, coverage, complexity and security
concurrently; the integration station runs a container up, tests, and a container down in
order.

Consequence, accepted: two *stations* cannot run concurrently. `review` and
`security-audit` serialise even though neither depends on the other. Revisiting that means
replacing the single-cursor resume model, which is out of scope here.

### 3.6 Recursion limits

- `Blueprint.MaxStepDepth`, default 5, mirroring the existing `MaxDelegationDepth = 3`.
- `Blueprint.Validate()` gains depth checking and cycle detection over gate composition.
- Both are load-time. A too-deep or cyclic pipeline fails `factory init` and `factory
  doctor`, never a work item mid-flight.

### 3.7 Gate execution

Gates load in-process through the existing `PluginLoadContext`, under the same trust and
contract-version model that `IWorkItemStore` plugins already have. `PluginCatalog`
`RegisterAssembly` gains `IGate` alongside the three ports it already claims — one line.

In-process is required for gates that must stay inside the token economy: an `llm-review`
gate needs the budget guard, the `AgentRunner` and the run ledger.

```csharp
public interface IGate
{
    Task<GateVerdict> EvaluateAsync(GateContext ctx);
}

public sealed record GateVerdict(bool Passed, string Detail,
                                 IReadOnlyDictionary<string, string>? Metrics = null);
```

`GateRunner` evaluates a tree:

- Each leaf runs under a linked `CancellationTokenSource` with `CancelAfter(TimeoutSeconds)`.
- `Setup` runs sequentially; a setup failure skips `Steps` and still runs `Teardown`.
- `Teardown` always runs, in a `finally`, with a **fresh** bounded token so a cancelled or
  timed-out run still tears down containers.
- `Parallel` steps run concurrently; all must pass. First failure does not cancel siblings —
  every gate reports, so one run surfaces every violation rather than one.
- Each leaf emits a `GateEvaluated` event, which already exists.

**Known limit, accepted.** Cancellation is cooperative. A gate that shells out is killed
because the process is killed; a gate that spins in managed code ignores its token, and
`AssemblyLoadContext` cannot be force-unloaded — so a pathological gate can hang the
factory. Bounding this fully requires the subprocess model, which was considered and
declined for the token-economy reasons above. Mitigations: the hard timeout is mandatory
and logged on expiry, and `doctor` reports any gate that has ever timed out.

### 3.8 Baseline semantics

Gates inherit the existing `ToolchainRunner.Compare(results, baseline)` behaviour: a gate
already failing on the mainline does not block new work, and says so in the log. This is
what makes staged adoption of a 100% coverage gate possible without freezing the backlog,
and it is why `Advisory` exists as a distinct, weaker setting for gates that should never
block even on regression.

### 3.9 Named and scheduled pipelines

`Blueprint.Pipeline` today is a single ordered list — the default route for a work item. A
scheduled sweep is a *second* route through the same station set, so `Blueprint` gains:

```csharp
public sealed record RouteDef
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Stations { get; init; }

    /// <summary>Cron expression. Null means this route only runs when invoked.</summary>
    public string? Schedule { get; init; }

    /// <summary>Priority of the work item filed when a gate on this route fails.
    /// Null means a failure blocks rather than files.</summary>
    public int? FileWorkItemOnFailure { get; init; }
}
```

`Blueprint.Pipeline` is retained as the default route so every existing `blueprint.json`
deserialises unchanged; `Routes` is additive.

`FileWorkItemOnFailure` is deliberately **not** an overload of `OnFail`. `OnFail` names a
station to route back to; filing work is a different outcome and needs a different word.

```csharp
.Route("security-sweep", r => r
    .Schedule("0 3 * * *")
    .FilesWorkOnFailure(priority: 1)
    .Station("scan", s => s
        .Parallel(Security.VulnerablePackages(),
                  Security.Secrets(),
                  Security.LicenseAudit())))
```

The daemon (`factory up`) fires the schedule; a failing gate files work through the
existing `StationResult.NewItems` path, which lands in beads and appears in `bd ready`.

No CI, no git remote, no external scheduler, and the scan lands in the same ledger as
everything else. Accepted consequence: nothing fires while the daemon is down.

---

## 4. Scope of change

| Project | Change |
|---|---|
| `Factory.Core` | `GateNode`, `GateMode`, `IGate`, `GateVerdict`, `GateContext`; `StationDef.Gates`; `RouteDef` + `Blueprint.Routes`; `Blueprint.MaxStepDepth`; `Validate()` depth + cycle checks; `RunRecord` gate attribution fields |
| `Factory.Runtime` | `GateRunner`; `CheckStation` runs declared gates instead of `Toolchain.Detect()`; `PluginCatalog` claims `IGate`; route scheduling in the daemon; pipeline discovery, restore and compile |
| `Factory.Cli` | `factory doctor` reports pipeline source, resolved versions, gate tree, timeouts |
| new: `Factory.Pipelines` | `IPipelineDefinition`, `IPipelineBuilder`, the fluent surface |
| new: `Factory.Gates.Dotnet` | `Shell`, `DotnetCoverage`, `Complexity`, `Security`, `Container` |
| `tests/` | split into `Factory.UnitTests`, `Factory.IntegrationTests`, `Factory.E2ETests`, `Factory.TestSupport` |

`Toolchain.Detect()` is retained as the **default** gate set for a repository with no
pipeline definition. It stops being the only possible answer.

---

## 5. Delivery order

Content first, then the mechanism on a clean base. Analyzer configuration belongs in
`.editorconfig` whatever invokes it, and a `Shell.Run("dotnet build")` gate calls the same
command — so nothing here is thrown away. The tier split blocks the coverage gate under
either ordering.

Each step is **small batch, low WIP**: finished and proven before the next begins.

### Phase 1 — Analyzers
1. `.editorconfig`, `AnalysisLevel=latest`, `AnalysisMode=All`, `EnforceCodeStyleInBuild`.
2. **Restore green.** Turn on as *warnings*. Count. Remediate to zero. Evidence: build +
   full suite green.
3. Flip `TreatWarningsAsErrors`. `CheckStation` already runs `dotnet build`, so this is
   enforced immediately with no gate.

### Phase 2 — Formatting
4. CSharpier as a local tool, pinned in `.config/dotnet-tools.json`.
5. **Restore green.** Format the tree. Evidence: `csharpier check .` exit 0, suite green.

### Phase 3 — Test tiers
6. Create `Factory.UnitTests`, `Factory.IntegrationTests`, `Factory.E2ETests`,
   `Factory.TestSupport`. Unit tier: no filesystem, no process, no clock.
7. Triage and move all 415 tests.
8. **Restore green.** Evidence: all four projects green, unit-tier wall clock recorded, and
   per-tier coverage measured and recorded as the new baseline.

### Phase 4 — Vertical slice
9. `GateNode`, `IGate`, `GateVerdict`, `Validate()` extensions, `GateRunner` with timeout.
10. `Factory.Pipelines` builder; pipeline discovery and compile; `blueprint.json` emission.
11. `Shell` gate only. Wire CSharpier through the pipeline. Evidence: the loop proven end to
    end — load, resolve, evaluate, record, route.

### Phase 5 — Deterministic gates
12. `DotnetCoverage` (per-tier thresholds), `Complexity`, `Security`.
13. Each lands `Advisory` first, then blocking once green.

### Phase 6 — Inferential gates and scheduling
14. `llm-review` gate package: architectural review of plans, security and maintainability
    review of diffs, each with a rubric.
15. Cron scheduling on pipelines; `NewItems` wiring to beads.
16. Worktree isolation enforced as a precondition rather than a convention.

---

## 6. Open decisions, deferred to their own specs

- **What "100% unit coverage" means.** Line only, or line and branch. Whether `Factory.Cli`
  is in scope — at 15.5% line and 8.6% branch it dominates the cost. Decide in the Phase 5
  coverage-gate spec, against the per-tier baseline measured in Phase 3 step 8.
- **The integration coverage figure.** Left as `N%` until the tier split makes a defensible
  number measurable.
- **Complexity thresholds and the tool.** `Microsoft.CodeAnalysis.Metrics` for .NET;
  language-agnostic equivalents for generated output must be chosen per toolchain.
- **Phase 1 remediation size.** Unknown and unestimated until analyzers are switched on and
  the warnings are counted. That count is the first deliverable of Phase 1.

---

## 7. Risks

| Risk | Mitigation |
|---|---|
| A pathological in-process gate hangs the factory | Mandatory per-gate timeout; process-killing for shell gates; `doctor` reports historical timeouts. Full isolation was declined for token-economy reasons (§3.7) |
| Phase 1 remediation is larger than expected | Warnings-first, counted before committing to the flip; `Advisory` and baseline semantics let adoption stage |
| The 100% unit gate proves unreachable | Per-tier baseline measured before the threshold is chosen; gate lands `Advisory` first |
| Two pipeline sources confuse provenance | `doctor` always prints which resolved, and to what version |
| Nothing runs while the daemon is down | Accepted. Revisit only if scheduled sweeps prove to be missed in practice |
