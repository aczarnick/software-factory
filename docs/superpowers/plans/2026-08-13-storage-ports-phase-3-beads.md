# Beads Backlog Provider (Phase 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make beads the authoritative backlog store, shared across machines via a Dolt remote, with the ledger keeping an audit copy.

**Architecture:** `BeadsWorkItemStore` shells out to the `bd` CLI and maps `WorkItem` onto a bead: native fields where beads has them, `metadata` JSON for the structured remainder. The factory's own id becomes the bead id. Reconcile-on-open makes beads unconditionally win over the ledger fold.

**Tech Stack:** .NET 10, beads 1.2.1 (`bd` on PATH), embedded Dolt, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-13-storage-adapters-design.md`

**Depends on:** Phases 1 and 2 complete and merged.

## Global Constraints

- .NET 10 SDK pinned to `10.0.400`. Do not change the pin.
- `Factory.Core` must remain dependency-free.
- One top-level type per file. XML docs on public APIs only.
- Retry a failed build once (`csc.dll exited with code 132`).
- Verification gate: `dotnet build` and `dotnet test` green, output shown.
- **Beads tests must create their own throwaway database** under a temp directory and must never touch the repository's `.beads` or `.factory`.
- Every `bd` invocation must set `BD_NON_INTERACTIVE=1`, or it may block waiting on a prompt.

## Verified Beads Behaviour

Probed against beads 1.2.1. Treat as fact; re-probe only if `bd version` differs.

| Behaviour | Result |
|---|---|
| `bd create --id wi_abc123` | **rejected** — `invalid ID format (expected prefix-hash)` |
| `bd create --id wi-cba5198e7c96` | accepted |
| `bd config set status.custom "draft:frozen,..."` | accepted; custom statuses appear in `bd statuses` |
| `bd ready` with `frozen`/`wip` statuses | excludes them; only `open` (active) is returned |
| `bd create --deps depends-on:X` | dependent excluded from `bd ready` until X closes |
| `bd ready --claim --json` | atomic; sets `in_progress`, `assignee`, `started_at`, `lease_expires_at`, `heartbeat_at` |
| Default lease | 5 minutes; **no config key found** in `bd config list` |
| `bd update --metadata '{...}'` | arbitrary JSON, exact round-trip via `bd show --json` |
| `bd show --json` | returns a **JSON array**, not an object |
| Writes with no remote configured | succeed; `bd sync` exits 0 with guidance |
| Leases | **node-local**; heartbeats write no Dolt commit, so expiry does not replicate |

---

### Task 1: Synchronous shell execution

`IWorkItemStore` is synchronous but `Shell` is async-only. Sync-over-async risks deadlocking the thread pool, so add a genuinely synchronous path. `Shell.Which` already sets this precedent with a documented rationale.

**Files:**
- Modify: `src/Factory.Runtime/Shell.cs` — add `Run`
- Test: `tests/Factory.Tests/ShellRunTests.cs`

**Interfaces:**
- Produces: `Shell.Run(string fileName, IEnumerable<string> args, string workingDirectory, IDictionary<string,string>? environment = null, int timeoutSeconds = 60)` returning `ShellResult`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Factory.Runtime;

namespace Factory.Tests;

public class ShellRunTests
{
    [Fact]
    public void Run_captures_stdout_and_exit_code()
    {
        var result = Shell.Run("/bin/echo", ["hello"], Directory.GetCurrentDirectory());

        Assert.True(result.Ok);
        Assert.Contains("hello", result.Stdout);
    }

    [Fact]
    public void Run_reports_a_non_zero_exit()
    {
        var result = Shell.Run("/bin/sh", ["-c", "exit 3"], Directory.GetCurrentDirectory());

        Assert.False(result.Ok);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public void Run_passes_environment_variables_through()
    {
        var result = Shell.Run("/bin/sh", ["-c", "echo $FACTORY_PROBE"],
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string> { ["FACTORY_PROBE"] = "set" });

        Assert.Contains("set", result.Stdout);
    }

    [Fact]
    public void Run_times_out_rather_than_hanging()
    {
        var result = Shell.Run("/bin/sh", ["-c", "sleep 5"], Directory.GetCurrentDirectory(),
            timeoutSeconds: 1);

        Assert.True(result.TimedOut);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~ShellRunTests`
Expected: FAIL — no `Run` overload.

- [ ] **Step 3: Implement `Shell.Run`**

Add to `src/Factory.Runtime/Shell.cs`:

```csharp
    /// <summary>
    /// Runs a short-lived local command synchronously. Deliberately not async: the storage
    /// ports are synchronous, and sync-over-async on a saturated thread pool deadlocks. Use
    /// only for fast local processes — never for network calls or builds.
    /// </summary>
    public static ShellResult Run(
        string fileName,
        IEnumerable<string> args,
        string workingDirectory,
        IDictionary<string, string>? environment = null,
        int timeoutSeconds = 60)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (environment is not null)
            foreach (var (key, value) in environment) psi.Environment[key] = value;

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return new ShellResult(127, "", $"could not start {fileName}", false);

            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(timeoutSeconds * 1000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new ShellResult(124, "", $"{fileName} timed out after {timeoutSeconds}s", true);
            }

            return new ShellResult(proc.ExitCode, stdout.Result, stderr.Result, false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or SystemException)
        {
            return new ShellResult(127, "", ex.Message, false);
        }
    }
```

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~ShellRunTests`
Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Factory.Runtime/Shell.cs tests/Factory.Tests/ShellRunTests.cs
git commit -m "Add synchronous shell execution for short local commands"
```

---

### Task 2: Identifier format and priority narrowing

**Files:**
- Modify: `src/Factory.Core/Ids.cs:6`
- Modify: `src/Factory.Core/WorkItem.cs:66`
- Test: `tests/Factory.Tests/CoreTests.cs` — add `IdFormatTests`

**Interfaces:**
- Produces: `Ids.New` emitting `prefix-hash`; `WorkItem.Priority` defaulting to `2`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Factory.Tests/CoreTests.cs`:

```csharp
public class IdFormatTests
{
    [Fact]
    public void New_emits_a_beads_compatible_identifier()
    {
        var id = Ids.New("wi");

        Assert.StartsWith("wi-", id);
        Assert.DoesNotContain("_", id);
    }

    [Fact]
    public void Work_items_default_to_the_middle_priority_band()
    {
        Assert.Equal(2, WorkItem.Create("thing").Priority);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~IdFormatTests`
Expected: FAIL on both.

- [ ] **Step 3: Change the separator**

In `src/Factory.Core/Ids.cs`:

```csharp
    public static string New(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("n")[..12]}";
```

This changes every generated id (`wi-`, `run-`, `evt-`, `ac-`), not just work items. That is intended: one format is simpler than two, and only work item ids cross into beads. Existing ledger entries keep their `wi_` ids and still resolve — ids are opaque strings.

- [ ] **Step 4: Narrow the priority band**

In `src/Factory.Core/WorkItem.cs`, replace the `Priority` property:

```csharp
    /// <summary>Dispatch priority, 0 (highest) to 4. Lower sorts first. Matches the beads
    /// band so the backlog store and the factory agree on order.</summary>
    public int Priority { get; init; } = 2;
```

- [ ] **Step 5: Find and fix every other priority assumption**

Run: `grep -rn "Priority" --include="*.cs" src tests | grep -v "/obj/\|/bin/"`

Fix any site that assumes the old 0-100 range. Expect hits in the decompose station prompt handling and `Commands.cs` sorting. Sorting by ascending priority is unchanged and needs no edit.

Also check the decompose prompt text: `grep -rn "priority" .factory/prompts/decompose/` — if it instructs the model to emit a 0-100 priority, update the prompt to say 0-4. A stale prompt would file items outside the band.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: all pass. Any failure here is a real range assumption; fix the production code.

- [ ] **Step 7: Commit**

```bash
git add src/Factory.Core/Ids.cs src/Factory.Core/WorkItem.cs tests/Factory.Tests/CoreTests.cs
git commit -m "Adopt beads-compatible identifiers and narrow the priority band to 0-4"
```

---

### Task 3: The bead mapper

Pure translation, no process execution — so it is testable without `bd` installed.

**Files:**
- Create: `src/Factory.Runtime/Providers/Beads/BeadMapper.cs`
- Create: `src/Factory.Runtime/Providers/Beads/BeadRecord.cs`
- Test: `tests/Factory.Tests/BeadMapperTests.cs`

**Interfaces:**
- Produces: `BeadRecord` (the `bd --json` shape), `BeadMapper.ToWorkItem(BeadRecord)`, `BeadMapper.CreateArgs(WorkItem)`, `BeadMapper.UpdateArgs(WorkItem)`, `BeadMapper.StatusFor(WorkItemState)`, `BeadMapper.StateFor(string)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public class BeadMapperTests
{
    [Fact]
    public void Every_work_item_state_maps_to_a_status_and_back()
    {
        foreach (var state in Enum.GetValues<WorkItemState>())
            Assert.Equal(state, BeadMapper.StateFor(BeadMapper.StatusFor(state)));
    }

    [Fact]
    public void Create_args_carry_the_explicit_id_and_native_fields()
    {
        var item = WorkItem.Create("add a flag", "users want it", WorkItemKind.Feature) with
        {
            Priority = 1
        };

        var args = BeadMapper.CreateArgs(item);

        Assert.Contains("--id", args);
        Assert.Contains(item.Id, args);
        Assert.Contains("feature", args);
        Assert.Contains("1", args);
    }

    [Fact]
    public void Structured_criteria_survive_the_metadata_round_trip()
    {
        var item = WorkItem.Create("thing") with
        {
            AcceptanceCriteria =
            [
                AcceptanceCriterion.Command("cli runs", "dotnet run -- --help"),
                AcceptanceCriterion.Judged("reads well", "prose is clear")
            ],
            BudgetUsd = 1.25m,
            Provenance = Provenance.FromAgent("review")
        };

        var bead = new BeadRecord
        {
            Id = item.Id,
            Title = item.Title,
            Status = BeadMapper.StatusFor(item.State),
            Metadata = BeadMapper.MetadataFor(item)
        };

        var restored = BeadMapper.ToWorkItem(bead);

        Assert.IsType<CommandVerification>(restored.AcceptanceCriteria[0].Verification);
        Assert.IsType<AgentJudgeVerification>(restored.AcceptanceCriteria[1].Verification);
        Assert.Equal(1.25m, restored.BudgetUsd);
        Assert.Equal(ProvenanceKind.Agent, restored.Provenance.Kind);
    }

    [Fact]
    public void Volatile_run_state_is_not_sent_to_the_backlog()
    {
        var item = WorkItem.Create("thing") with
        {
            Station = "implement",
            Worktree = "/tmp/wt",
            Attempts = 3,
            SpentUsd = 0.42m
        };

        var metadata = BeadMapper.MetadataFor(item);

        Assert.DoesNotContain("worktree", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("spentUsd", metadata, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refactor_and_improvement_map_to_custom_types()
    {
        Assert.Equal("refactor", BeadMapper.TypeFor(WorkItemKind.Refactor));
        Assert.Equal("improvement", BeadMapper.TypeFor(WorkItemKind.Improvement));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~BeadMapperTests`
Expected: FAIL — `BeadMapper` does not exist.

- [ ] **Step 3: Create `BeadRecord`**

```csharp
using System.Text.Json.Serialization;

namespace Factory.Runtime;

/// <summary>The subset of <c>bd --json</c> output the factory reads.</summary>
public sealed record BeadRecord
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "open";
    [JsonPropertyName("priority")] public int Priority { get; init; } = 2;
    [JsonPropertyName("issue_type")] public string IssueType { get; init; } = "task";
    [JsonPropertyName("acceptance_criteria")] public string? AcceptanceCriteria { get; init; }
    [JsonPropertyName("assignee")] public string? Assignee { get; init; }
    [JsonPropertyName("revision")] public long Revision { get; init; }
    [JsonPropertyName("started_at")] public DateTimeOffset? StartedAt { get; init; }
    [JsonPropertyName("lease_expires_at")] public DateTimeOffset? LeaseExpiresAt { get; init; }

    /// <summary>Raw JSON object holding everything beads has no native field for.</summary>
    [JsonPropertyName("metadata")] public System.Text.Json.JsonElement? Metadata { get; init; }
}
```

- [ ] **Step 4: Create `BeadMapper`**

```csharp
using System.Text.Json;
using Factory.Core;

namespace Factory.Runtime;

/// <summary>Translates between a <see cref="WorkItem"/> and a bead. Native beads fields are
/// used where they exist; everything structured travels in the bead's metadata JSON.</summary>
public static class BeadMapper
{
    /// <summary>Custom vocabulary this mapping requires. Installed once at deployment.</summary>
    public const string CustomStatuses =
        "draft:frozen,in_review:wip,verified:wip,failed:frozen,cancelled:done";

    public const string CustomTypes = "refactor,improvement";

    public static string StatusFor(WorkItemState state) => state switch
    {
        WorkItemState.Draft => "draft",
        WorkItemState.Ready => "open",
        WorkItemState.InProgress => "in_progress",
        WorkItemState.InReview => "in_review",
        WorkItemState.Verified => "verified",
        WorkItemState.Done => "closed",
        WorkItemState.Blocked => "blocked",
        WorkItemState.Failed => "failed",
        WorkItemState.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unmapped work item state.")
    };

    public static WorkItemState StateFor(string status) => status switch
    {
        "draft" => WorkItemState.Draft,
        "open" => WorkItemState.Ready,
        "in_progress" => WorkItemState.InProgress,
        "in_review" => WorkItemState.InReview,
        "verified" => WorkItemState.Verified,
        "closed" => WorkItemState.Done,
        "blocked" => WorkItemState.Blocked,
        "failed" => WorkItemState.Failed,
        "cancelled" => WorkItemState.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped bead status.")
    };

    public static string TypeFor(WorkItemKind kind) => kind switch
    {
        WorkItemKind.Feature => "feature",
        WorkItemKind.Bug => "bug",
        WorkItemKind.Chore => "chore",
        WorkItemKind.Spike => "spike",
        WorkItemKind.Refactor => "refactor",
        WorkItemKind.Improvement => "improvement",
        _ => "task"
    };

    public static WorkItemKind KindFor(string issueType) => issueType switch
    {
        "feature" => WorkItemKind.Feature,
        "bug" => WorkItemKind.Bug,
        "chore" => WorkItemKind.Chore,
        "spike" => WorkItemKind.Spike,
        "refactor" => WorkItemKind.Refactor,
        "improvement" => WorkItemKind.Improvement,
        _ => WorkItemKind.Feature
    };

    /// <summary>Everything beads has no native field for. Volatile per-run state is
    /// deliberately excluded: it belongs to the local ledger, not the shared backlog.</summary>
    public static string MetadataFor(WorkItem item) => FactoryJson.Write(new BeadMetadata
    {
        Intent = item.Intent,
        Requirements = item.Requirements,
        Criteria = item.AcceptanceCriteria,
        Assumptions = item.Assumptions,
        Labels = item.Labels,
        ParentId = item.ParentId,
        BudgetUsd = item.BudgetUsd,
        ProvenanceKind = item.Provenance.Kind,
        ProvenanceSource = item.Provenance.Source
    });

    public static WorkItem ToWorkItem(BeadRecord bead)
    {
        var metadata = bead.Metadata is { } element
            ? FactoryJson.Read<BeadMetadata>(element.GetRawText()) ?? new BeadMetadata()
            : new BeadMetadata();

        return new WorkItem
        {
            Id = bead.Id,
            Title = bead.Title,
            Intent = metadata.Intent ?? bead.Description ?? "",
            Kind = KindFor(bead.IssueType),
            State = StateFor(bead.Status),
            Priority = bead.Priority,
            Requirements = metadata.Requirements,
            AcceptanceCriteria = metadata.Criteria,
            Assumptions = metadata.Assumptions,
            Labels = metadata.Labels,
            ParentId = metadata.ParentId,
            BudgetUsd = metadata.BudgetUsd,
            Provenance = new Provenance(metadata.ProvenanceKind, metadata.ProvenanceSource)
        };
    }

    public static IReadOnlyList<string> CreateArgs(WorkItem item)
    {
        var args = new List<string>
        {
            "create", item.Title,
            "--id", item.Id,
            "-t", TypeFor(item.Kind),
            "-p", item.Priority.ToString(),
            "--metadata", MetadataFor(item),
            "--json"
        };

        if (!string.IsNullOrWhiteSpace(item.Intent)) { args.Add("-d"); args.Add(item.Intent); }

        if (item.AcceptanceCriteria.Count > 0)
        {
            args.Add("--acceptance");
            args.Add(string.Join("\n", item.AcceptanceCriteria.Select(c => $"- {c.Statement} ({c.Verification.Describe})")));
        }

        foreach (var dependency in item.DependsOn) { args.Add("--deps"); args.Add($"depends-on:{dependency}"); }

        return args;
    }

    public static IReadOnlyList<string> UpdateArgs(WorkItem item) =>
    [
        "update", item.Id,
        "--status", StatusFor(item.State),
        "-p", item.Priority.ToString(),
        "--metadata", MetadataFor(item)
    ];
}
```

- [ ] **Step 5: Create `BeadMetadata`**

Create `src/Factory.Runtime/Providers/Beads/BeadMetadata.cs`:

```csharp
using Factory.Core;

namespace Factory.Runtime;

/// <summary>The structured remainder of a work item, stored in the bead's metadata JSON.</summary>
public sealed record BeadMetadata
{
    public string? Intent { get; init; }
    public IReadOnlyList<string> Requirements { get; init; } = [];
    public IReadOnlyList<AcceptanceCriterion> Criteria { get; init; } = [];
    public IReadOnlyList<string> Assumptions { get; init; } = [];
    public IReadOnlyList<string> Labels { get; init; } = [];
    public string? ParentId { get; init; }
    public decimal? BudgetUsd { get; init; }
    public ProvenanceKind ProvenanceKind { get; init; } = ProvenanceKind.Human;
    public string? ProvenanceSource { get; init; }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~BeadMapperTests`
Expected: PASS, 5 tests.

- [ ] **Step 7: Commit**

```bash
git add src/Factory.Runtime/Providers/Beads/ tests/Factory.Tests/BeadMapperTests.cs
git commit -m "Map work items onto beads issues"
```

---

### Task 4: `BeadsWorkItemStore`

**Files:**
- Create: `src/Factory.Runtime/Providers/Beads/BeadsWorkItemStore.cs`
- Create: `src/Factory.Runtime/Providers/Beads/BeadsCli.cs`
- Test: `tests/Factory.Tests/BeadsWorkItemStoreTests.cs`

**Interfaces:**
- Consumes: `BeadMapper`, `BeadRecord` (Task 3), `Shell.Run` (Task 1).
- Produces: `BeadsCli(string workingDirectory)` with `ShellResult Exec(params string[] args)` and `T? Json<T>(params string[] args)`; `BeadsWorkItemStore(BeadsCli cli, string owner)` implementing `IWorkItemStore`.

Tests require `bd` on PATH. Skip the whole class when absent rather than failing — CI without beads must stay green.

- [ ] **Step 1: Write the failing tests**

```csharp
using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public class BeadsWorkItemStoreTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    private static bool Available => Shell.Which("bd");

    public BeadsWorkItemStoreTests()
    {
        if (!Available) return;
        Shell.Run("git", ["init", "-q", "."], _dir);
        var cli = new BeadsCli(_dir);
        cli.Exec("init", "--prefix", "wi");
        cli.Exec("config", "set", "status.custom", BeadMapper.CustomStatuses);
        cli.Exec("config", "set", "types.custom", BeadMapper.CustomTypes);
    }

    public void Dispose() => TempDir.Delete(_dir);

    private BeadsWorkItemStore Store() => new(new BeadsCli(_dir), "test-machine");

    [SkippableFact]
    public void Add_then_Get_round_trips_a_work_item()
    {
        Skip.IfNot(Available, "bd is not on PATH");
        var store = Store();

        var item = store.Add(WorkItem.Create("build a thing", "because") with
        {
            State = WorkItemState.Ready,
            Priority = 1,
            AcceptanceCriteria = [AcceptanceCriterion.Command("runs", "dotnet run")]
        });

        var restored = store.Get(item.Id)!;

        Assert.Equal("build a thing", restored.Title);
        Assert.Equal(WorkItemState.Ready, restored.State);
        Assert.Equal(1, restored.Priority);
        Assert.Single(restored.AcceptanceCriteria);
    }

    [SkippableFact]
    public void TryClaim_marks_the_item_in_progress_and_assigns_it()
    {
        Skip.IfNot(Available, "bd is not on PATH");
        var store = Store();
        store.Add(WorkItem.Create("claimable") with { State = WorkItemState.Ready });

        var claimed = store.TryClaim("test-machine")!;

        Assert.Equal(WorkItemState.InProgress, store.Get(claimed.Id)!.State);
    }

    [SkippableFact]
    public void TryClaim_withholds_an_item_with_an_unmet_dependency()
    {
        Skip.IfNot(Available, "bd is not on PATH");
        var store = Store();
        var blocker = store.Add(WorkItem.Create("first") with { State = WorkItemState.Ready });
        store.Add(WorkItem.Create("second") with
        {
            State = WorkItemState.Ready,
            DependsOn = [blocker.Id]
        });

        store.TryClaim("test-machine");

        Assert.Null(store.TryClaim("test-machine"));
    }

    [SkippableFact]
    public void A_draft_item_is_never_claimed()
    {
        Skip.IfNot(Available, "bd is not on PATH");
        var store = Store();
        store.Add(WorkItem.Create("proposal"));   // Draft

        Assert.Null(store.TryClaim("test-machine"));
    }

    [SkippableFact]
    public void Sync_without_a_remote_does_not_throw()
    {
        Skip.IfNot(Available, "bd is not on PATH");
        Store().Sync();
    }
}
```

`SkippableFact` requires the `Xunit.SkippableFact` package. Add it to `tests/Factory.Tests/Factory.Tests.csproj` — it is a test-only dependency and does not touch `Factory.Core`'s zero-dependency constraint. If you prefer no new package, replace `[SkippableFact]`/`Skip.IfNot` with a plain `[Fact]` that returns early when `!Available`, and note the silent skip in the completion report.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~BeadsWorkItemStoreTests`
Expected: FAIL — `BeadsCli` does not exist.

- [ ] **Step 3: Create `BeadsCli`**

```csharp
using Factory.Core;

namespace Factory.Runtime;

/// <summary>Thin synchronous wrapper over the <c>bd</c> executable.</summary>
public sealed class BeadsCli(string workingDirectory)
{
    private static readonly Dictionary<string, string> Environment =
        new() { ["BD_NON_INTERACTIVE"] = "1" };

    public ShellResult Exec(params string[] args) =>
        Shell.Run("bd", args, workingDirectory, Environment);

    /// <summary>Runs a command expected to emit JSON. <c>bd</c> returns an array for both
    /// <c>show</c> and <c>list</c>, so callers always deserialise a collection.</summary>
    public IReadOnlyList<T> Json<T>(params string[] args)
    {
        var result = Exec(args);
        if (!result.Ok)
            throw new InvalidOperationException($"bd {string.Join(' ', args)} failed: {result.Combined}");

        var text = result.Stdout.Trim();
        if (string.IsNullOrEmpty(text) || text == "null") return [];

        return text.StartsWith('[')
            ? FactoryJson.Read<List<T>>(text) ?? []
            : [FactoryJson.Read<T>(text)!];
    }
}
```

- [ ] **Step 4: Create `BeadsWorkItemStore`**

```csharp
using Factory.Core;

namespace Factory.Runtime;

/// <summary>
/// Backlog stored in beads. Authoritative for item state across every machine sharing the
/// Dolt remote; volatile per-run state stays in the local ledger.
/// </summary>
public sealed class BeadsWorkItemStore(BeadsCli cli, string owner) : IWorkItemStore
{
    public WorkItem Add(WorkItem item)
    {
        var result = cli.Exec([.. BeadMapper.CreateArgs(item)]);
        if (!result.Ok)
            throw new InvalidOperationException($"Could not file {item.Id} in beads: {result.Combined}");

        // Created beads default to open; anything else needs an explicit status write.
        return item.State == WorkItemState.Ready ? item : Update(item);
    }

    public WorkItem Update(WorkItem item)
    {
        var result = cli.Exec([.. BeadMapper.UpdateArgs(item)]);
        if (!result.Ok)
            throw new InvalidOperationException($"Could not update {item.Id} in beads: {result.Combined}");

        return item with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    public WorkItem Transition(WorkItem item, WorkItemState to, string? reason)
    {
        if (!WorkItemStates.CanTransition(item.State, to))
            throw new InvalidOperationException(
                $"Illegal transition {item.State} -> {to} for {item.Id}.");

        var moved = item with { State = to, UpdatedAt = DateTimeOffset.UtcNow };
        Update(moved);

        if (!string.IsNullOrWhiteSpace(reason)) cli.Exec("note", item.Id, reason);

        return moved;
    }

    public WorkItem? Get(string id) =>
        cli.Json<BeadRecord>("show", id, "--json").Select(BeadMapper.ToWorkItem).FirstOrDefault();

    public IReadOnlyList<WorkItem> All() =>
        [.. cli.Json<BeadRecord>("list", "--all", "--json").Select(BeadMapper.ToWorkItem)];

    public WorkItem? TryClaim(string claimant) =>
        cli.Json<BeadRecord>("ready", "--claim", "--json")
           .Select(BeadMapper.ToWorkItem)
           .FirstOrDefault();

    public void Heartbeat(string id) => cli.Exec("heartbeat", id);

    public void Release(string id, string reason)
    {
        cli.Exec("unclaim", id);
        if (!string.IsNullOrWhiteSpace(reason)) cli.Exec("note", id, reason);
    }

    public void Sync() => cli.Exec("sync");

    public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan)
    {
        var result = cli.Exec("reclaim", "--older-than", $"{(int)olderThan.TotalMinutes}m", "--json");
        if (!result.Ok) return [];

        return [.. cli.Json<BeadRecord>("list", "--status", "open", "--assignee", owner, "--json")
                      .Select(BeadMapper.ToWorkItem)];
    }
}
```

Verify the exact flag spellings for `list --all`, `list --status`, `list --assignee`, `unclaim`, and `reclaim --older-than` against `bd <command> --help` before running. Only the commands in the Verified Beads Behaviour table above were probed; these were not.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~BeadsWorkItemStoreTests`
Expected: PASS, 5 tests (or 5 skipped if `bd` is absent).

- [ ] **Step 6: Commit**

```bash
git add src/Factory.Runtime/Providers/Beads/ tests/Factory.Tests/BeadsWorkItemStoreTests.cs \
        tests/Factory.Tests/Factory.Tests.csproj
git commit -m "Add the beads-backed work item store"
```

---

### Task 5: Deployment, reconcile, and heartbeat

**Files:**
- Create: `src/Factory.Runtime/Providers/Beads/BeadsDeployment.cs`
- Create: `src/Factory.Runtime/Providers/Beads/BacklogReconciler.cs`
- Modify: `src/Factory.Runtime/FactoryHost.cs` — register the provider, reconcile at open
- Modify: `src/Factory.Runtime/Orchestrator.cs` — heartbeat while an item is in flight
- Test: `tests/Factory.Tests/BacklogReconcilerTests.cs`

**Interfaces:**
- Produces: `BeadsDeployment.EnsureInitialised(BeadsCli, string prefix, Action<string> log)`, `BacklogReconciler.Reconcile(IWorkItemStore, FactoryState, IRunHistory, Action<string>)`.

- [ ] **Step 1: Write the failing reconciler test**

```csharp
using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public class BacklogReconcilerTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private sealed class StubStore(List<WorkItem> items) : IWorkItemStore
    {
        public WorkItem Add(WorkItem item) { items.Add(item); return item; }
        public WorkItem Update(WorkItem item) => item;
        public WorkItem Transition(WorkItem item, WorkItemState to, string? reason) => item with { State = to };
        public WorkItem? Get(string id) => items.FirstOrDefault(i => i.Id == id);
        public IReadOnlyList<WorkItem> All() => items;
        public WorkItem? TryClaim(string owner) => null;
        public void Heartbeat(string id) { }
        public void Release(string id, string reason) { }
        public void Sync() { }
        public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan) => [];
    }

    [Fact]
    public void The_store_wins_when_the_ledger_disagrees()
    {
        using var history = new JsonlRunHistory(Path.Combine(_dir, "ledger.jsonl"));
        var item = WorkItem.Create("thing") with { State = WorkItemState.Ready };

        history.Append(new WorkItemFiled(item));
        var state = history.Replay();

        var store = new StubStore([item with { State = WorkItemState.Done }]);
        BacklogReconciler.Reconcile(store, state, history, _ => { });

        Assert.Equal(WorkItemState.Done, state.Items[item.Id].State);
    }

    [Fact]
    public void An_item_only_the_store_knows_about_is_folded_in()
    {
        using var history = new JsonlRunHistory(Path.Combine(_dir, "ledger.jsonl"));
        var state = history.Replay();
        var remote = WorkItem.Create("filed elsewhere") with { State = WorkItemState.Ready };

        BacklogReconciler.Reconcile(new StubStore([remote]), state, history, _ => { });

        Assert.True(state.Items.ContainsKey(remote.Id));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~BacklogReconcilerTests`
Expected: FAIL — `BacklogReconciler` does not exist.

- [ ] **Step 3: Create `BacklogReconciler`**

```csharp
using Factory.Core;

namespace Factory.Runtime;

/// <summary>
/// Makes the local ledger agree with the authoritative backlog. Corrections only ever flow
/// one way, so a ledger write that failed mid-transition self-heals at the next open.
/// </summary>
public static class BacklogReconciler
{
    public static void Reconcile(
        IWorkItemStore store, FactoryState state, IRunHistory history, Action<string> log)
    {
        var corrected = 0;

        foreach (var authoritative in store.All())
        {
            var local = state.Items.GetValueOrDefault(authoritative.Id);
            if (local is not null && local.State == authoritative.State) continue;

            var evt = new WorkItemUpdated(authoritative);
            history.Append(evt);
            state.Apply(evt);
            corrected++;
        }

        if (corrected > 0) log($"reconciled {corrected} item(s) from the backlog store");
    }
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~BacklogReconcilerTests`
Expected: PASS, 2 tests.

- [ ] **Step 5: Create `BeadsDeployment`**

```csharp
using Factory.Core;

namespace Factory.Runtime;

/// <summary>Idempotent beads setup. Safe to run on every open: initialising an existing
/// database is a no-op, and the vocabulary writes are last-write-wins.</summary>
public static class BeadsDeployment
{
    public static void EnsureInitialised(BeadsCli cli, string prefix, Action<string> log)
    {
        if (!Shell.Which("bd"))
            throw new InvalidOperationException(
                "The beads backlog provider needs `bd` on PATH. Install it, or set " +
                "\"workItemStore\": { \"provider\": \"ledger\" } in .factory/factory.json.");

        if (!cli.Exec("info").Ok)
        {
            log("initialising beads database");
            var init = cli.Exec("init", "--prefix", prefix);
            if (!init.Ok) throw new InvalidOperationException($"bd init failed: {init.Combined}");
        }

        cli.Exec("config", "set", "status.custom", BeadMapper.CustomStatuses);
        cli.Exec("config", "set", "types.custom", BeadMapper.CustomTypes);
    }
}
```

- [ ] **Step 6: Register the provider and reconcile in `FactoryHost.Open`**

After the registry is built (phase 2, Task 5), add:

```csharp
        registry.Register<IWorkItemStore>("beads", reference =>
        {
            var cli = new BeadsCli(paths.RepoRoot);
            BeadsDeployment.EnsureInitialised(cli, reference.Options.GetValueOrDefault("prefix", "wi"), log2);
            return new BeadsWorkItemStore(cli, config.Name);
        });
```

After `items` is resolved, add:

```csharp
        items.Sync();
        BacklogReconciler.Reconcile(items, state, history, message => log2($"  [backlog] {message}"));
        foreach (var reclaimed in items.Reclaim(TimeSpan.FromMinutes(15)))
            log2($"  [backlog] reclaimed {reclaimed.Id} from a stale lease");
```

- [ ] **Step 7: Heartbeat in-flight items**

In `src/Factory.Runtime/Orchestrator.cs`, inside the polling loop that already runs every `PollSeconds`, add before the claim block:

```csharp
            foreach (var inFlight in _s.State.InFlight()) _s.Items.Heartbeat(inFlight.Id);
```

The default lease is 5 minutes and the default poll is 10 seconds, so this heartbeats far inside the window. If `PollSeconds` is ever raised above 120, the lease will expire mid-run — add that as a validation on `FactoryConfig` if you change the default.

- [ ] **Step 8: Run the full suite**

Run: `dotnet build && dotnet test`
Expected: all pass. The default config still selects `ledger`, so nothing changes for existing tests.

- [ ] **Step 9: Commit**

```bash
git add src/Factory.Runtime/Providers/Beads/ src/Factory.Runtime/FactoryHost.cs \
        src/Factory.Runtime/Orchestrator.cs tests/Factory.Tests/BacklogReconcilerTests.cs
git commit -m "Deploy, reconcile and heartbeat the beads backlog"
```

---

### Task 6: Verification gate

- [ ] **Step 1: Confirm `Factory.Core` still has no dependencies**

Run: `grep -c "ProjectReference\|PackageReference" src/Factory.Core/Factory.Core.csproj`
Expected: `0`.

- [ ] **Step 2: Confirm no test touched the repository's own beads or factory state**

Run: `git status --short && ls -d .beads 2>/dev/null`
Expected: clean tree, no `.beads` directory in the repository root.

- [ ] **Step 3: Run the full gate and show the output**

Run: `dotnet build && dotnet test`
Expected: build succeeds, all tests pass. Paste the summary line.

- [ ] **Step 4: Exercise it end to end against a scratch repository**

```bash
cd "$(mktemp -d)" && git init -q .
factory init
# edit .factory/factory.json: "workItemStore": { "provider": "beads" }
factory add "prove the beads backlog works" --criterion "true"
factory ls
```

Expected: the item appears in `factory ls` **and** in `bd list`. This is the proof that the store is authoritative rather than the ledger.

---

## Self-Review

**Spec coverage:** identifier format and priority narrowing (D6) — Task 2. Full mapping table including custom statuses and types — Task 3. Claim, heartbeat, release, sync, reclaim — Task 4. Reconcile-on-open with beads winning unconditionally (D1, D2) — Task 5. Own-node reclaim, limitation mitigation 1 — Task 5 Step 6.

**Deferred to phase 4:** the `integrate` sync gate (D7), `Degraded` in `factory status`, foreign-orphan reporting (limitation mitigation 2), the `factory doctor` dolt-server check, and migration of the existing 23 items.

**Unverified assumptions flagged in-plan:** the exact flag spellings in `BeadsWorkItemStore.All`, `Release`, and `Reclaim` were not probed — Task 4 Step 4 says to check them against `bd <command> --help` first. The lease TTL is assumed to be 5 minutes and not configurable; Task 5 Step 7 derives a safe cadence from the poll interval rather than depending on it.

**Type consistency:** `BeadMapper.StatusFor`/`StateFor` are total over all nine `WorkItemState` values and round-trip, asserted in Task 3's first test. `BeadsCli.Json<T>` returns `IReadOnlyList<T>` and every call site uses `.FirstOrDefault()` or enumerates. `BeadsWorkItemStore(BeadsCli, string)` matches both the test helper and the registry registration.
