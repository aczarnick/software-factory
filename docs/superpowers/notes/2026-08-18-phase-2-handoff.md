# Storage Ports: Phase 2 Handoff

Phase 2 (plugin infrastructure, spec decision **D5**) implemented 2026-08-17/18 and **merged to
`master` 2026-08-18** as merge commit `1a611e2` (`--no-ff`; the branch `storage-ports-phase-2-work`
was deleted after merging). Suite on the merged result: **203 passing**, build clean apart from the
one pre-existing CA1416. This is what phase 3 needs and could not learn from the diff.

## What phase 2 delivered

`FactoryProviderAttribute` and `FactoryVersion.ContractVersion`, `ProviderRef` and
`RunHistoryConfig`, and `FactoryPaths.PluginsDir` in `Factory.Core` (still dependency-free).
`WorkItemStoreException` there too. In `Factory.Runtime`: `ProviderRegistry`, `PluginLoadContext`,
`PluginCatalog` under `Plugins/`, and `GuardedWorkItemStore`, `GuardedRunHistorySink`,
`FanOutRunHistory` under `Providers/`. `FactoryHost.Open` resolves every provider from config
through the registry instead of constructing them inline.

## OPERATIONAL: the factory follows the checkout, not the branch

The phase-1 handoff said "do phase 2 on its own branch" because a live `factory up` was committing
to `master`. **That advice does not work.** `factory up` commits to whatever branch the main
checkout has at HEAD — so creating `storage-ports-phase-2` and working on it simply moved the
autonomous committer onto the phase branch. It landed four commits (`db8a7d3`, `5b31b20`, `785ce27`,
`9aa1171`) interleaved with the phase work before this was noticed.

The fix that works is a **second checkout**: `git worktree add ../software-factory-phase2 -b <branch>`.
A branch can only be checked out in one worktree, so the factory keeps its own branch and the phase
work is isolated. Phase 2 was completed this way from Task 3 onward. Phase 3 should start there.

Consequence for this branch: `storage-ports-phase-2-work` carries those four factory commits as
ancestors. They are the factory's own work, already destined for master, and they touch only
`CheckStation.cs`, `Toolchain.cs` and their tests — zero file overlap with phase 2.

### Repository state phase 3 inherits

While phase 2 was in flight the main checkout was on `storage-ports-phase-2` and the factory kept
committing there. That work was brought onto `master` by cherry-picking the `factory: integrate`
**merge** commits with `-m 1` — that takes the diff against the first parent, which is the work-item
content, and so excluded the phase-2 commits interleaved in the same chain. A plain fast-forward
would have dragged partial phase-2 work onto `master`.

Settled 2026-08-18: the factory was stopped and the main checkout moved back to `master`, so it
commits there again and the divergence trap is closed. `storage-ports-phase-2` is now redundant — a
subset of `master` by content, apart from superseded phase-2 drafts — and can be deleted.

Two things still worth knowing:

1. **A SHA count is not a content count.** `git rev-list --count master..<branch>` counts commits
   absent by SHA, so cherry-picked work still shows as missing; `git log --cherry-pick` is no better
   for merge commits, because a merge's patch-id does not match the `-m 1` commit it produced. Both
   led to a false "18 commits behind" reading here when nothing was missing. The authoritative check
   is a tree diff: `git diff --stat master <branch>`.
2. **The duplicate-SHA history means re-merging that branch conflicts in files phase work never
   touched.** The phase-2 merge hit exactly one, in `Toolchain.cs`, where the resolution was "take
   master, the phase side contributed nothing". `master` is local-only; nothing here has been pushed.

## Where the plan was wrong

Four defects in the phase-2 plan, all found before or during execution. Phase 3's plan deserves the
same pre-flight scan; two of these were caught only because the scan was run against real code and
probed rather than reasoned about.

- **`ProviderRef` as specified could not deserialize.** A record with two public constructors and no
  parameterless one makes `System.Text.Json` throw `NotSupportedException`. Fixed with
  `[method: JsonConstructor]` on the primary constructor.
- **`Activator.CreateInstance(type, reference) ?? Activator.CreateInstance(type)` is unreachable
  fallback code.** `CreateInstance` throws `MissingMethodException` when no matching constructor
  exists; it does not return null. The catalog now selects the constructor explicitly.
- **`FanOutRunHistory.Dispose` as specified leaked the durable writer** when a sink's `Flush` threw.
  Now `try`/`finally`.
- **The plan's `Register` chain registered a type under only its first matching port**, silently.
  Now every port the type satisfies.

## Traps that cost real time

- **Three tests in this phase passed without testing what their names claimed**, and one whole
  production branch (the contract-version gate) was unreachable from the suite until the final
  review's mutation found it. Mutation-check every new test by deleting the logic it names. The
  most instructive case: a fan-out ordering test could not distinguish write-before-sink from
  sink-before-write, because the guarded sink swallowed the exception the test relied on.
- **`OrderBy(p => p)` over filenames is ordinal here** — `Directory.Build.props` sets
  `InvariantGlobalization`. A fixture named `broken.dll` sorts *after* `Factory.*`, which silently
  disabled the "scan continues past a broken assembly" half of its test. `AAA-broken.dll` sorts first.
- **The fixture plugin must not ship `Factory.Core.dll`** (`Private=false` + `ExcludeAssets=runtime`),
  but the *test* must copy one into the temp plugins directory. Without that copy, deleting
  `PluginLoadContext`'s `Factory.Core → null` guard leaves the whole suite green.
- **`Factory.Tests` does not reference the fixture project**, so `dotnet test --filter` will not build
  it. Run `dotnet build` at the solution root first; the helper now fails with that instruction
  rather than a `DirectoryNotFoundException`.

## What phase 3 inherits

- **`ProviderRegistry` enforces "built-ins win" structurally**, not by registration order. Phase 3
  registers `"beads"` as a built-in `IWorkItemStore` into this registry; a plugin of the same name
  is refused and logged in either direction.
- **`PluginLoadContext`s are memoized by resolved DLL path.** Deliberate trade recorded in the final
  re-review: `LoadInto` runs on every `FactoryHost.Open`, and `DelegateStation` opens a child host
  per delegated work item, so per-call contexts leaked unboundedly (they are `isCollectible: false`).
  The cost is that a plugin DLL replaced on disk is not picked up until the process restarts. Real
  hot-reload needs a collectible context.
- **`IRunHistorySink.Emit` is documented as concurrent.** `FanOutRunHistory` offers events to sinks
  outside `JsonlRunHistory`'s lock, and stations run concurrently when `MaxConcurrency > 1`, so sinks
  can observe events out of durable-log order and must sort on `evt.Seq` if they care.
- **`Replay()` stayed off `IRunHistory`** (settled in phase 1, re-confirmed here).
  `FactoryHost.Open` folds with `FactoryState.Replay(history.ReadFrom(0))`. The condition that would
  reverse it is unchanged: a provider that can reconstruct state materially faster than a full fold.
  A beads store that replays events like the JSONL one does is not that provider.
- **The run-history *writer* cannot take provider options.** `RunHistoryConfig.Writer` is a bare
  string, matching the spec's config block; sinks and the work-item store both carry a full
  `ProviderRef`. A third-party writer needing a connection string has nowhere to put it. Changing it
  is a spec amendment plus a config migration, not a code fix — raise it against the spec before
  phase 3 if it matters. A `JsonConverter` accepting both `"jsonl"` and `{"provider":"jsonl"}` would
  keep existing `.factory/factory.json` files working.

## Known gaps, deliberately left

- The version gate is proven to refuse a mismatched provider and log the version pair, but not proven
  to skip only *that type* rather than abandoning the rest of its assembly. Closing it properly needs
  a second fixture assembly. Cheaper partial: assert that all contract-v1 fixture providers still
  resolve after a scan that includes the v2 type.
- No test produces a genuine missing-dependency `FileNotFoundException`; the widened catch filter is
  covered only by a plain-text `broken.dll`, which raises `BadImageFormatException`.
- Every `*.dll` in the plugins directory is loaded as a candidate, including dependency assemblies,
  each logging `registered no providers`. The clean filter is
  `assembly.GetReferencedAssemblies().Any(a => a.Name == "Factory.Core")` — it survives a
  unification failure (the failing assembly still references `Factory.Core`) while excluding
  dependency DLLs.
- Cosmetic, batched for whenever these files are next touched: `GuardedWorkItemStore`'s `return 0;`
  filler for void members, the `log2` local in `FactoryHost.Open`, and the "shadowed by a built-in"
  log line not naming its port.
