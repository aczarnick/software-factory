# Software Factory — Implementation Plan

Derived from `SPEC.md`. Each phase ends at a verifiable gate.

## Solution layout

```
software-factory/
  SoftwareFactory.sln
  src/
    Factory.Core/        Domain, ledger, blueprint, budgets, verification   (no deps)
    Factory.Agents/      Claude SDK harness: transport, sessions, profiles, cache
    Factory.Runtime/     Orchestrator, station executors, composition, worktrees
    Factory.Evolution/   Prompt registry, evaluator, optimiser, promotion gate
    Factory.Cli/         `factory` executable — one-command deploy
  tests/
    Factory.Tests/       xUnit; fake transport for deterministic agent tests
  kit/                   Default blueprints + prompt assets (shipped, copied on init)
  install.sh             One-line installer
```

Dependency direction is strictly one way:
`Cli -> Runtime -> {Evolution, Agents} -> Core`. Core depends on nothing.

---

## Phase 1 — Factory.Core

Domain types and durable state. No I/O beyond the ledger file.

- `WorkItem`, `WorkItemState`, `WorkItemKind`, `Provenance`
- `AcceptanceCriterion` + `Verification` union (Command / TestsPass / FileExists / AgentJudge)
- `Ledger`: append-only JSONL, `Append(evt)`, `Replay() -> FactoryState`, crash-safe
- `FactoryEvent` hierarchy with polymorphic JSON
- `Blueprint`, `StationDef`, `RoutingRule`, `GatePolicy`, `Budget`
- `BudgetLedger`: run/item/daily accumulators, `BudgetExhausted`
- `TokenProfile` enum: `None | Thin | Thick`

**Gate:** ledger round-trips; replay reconstructs state; budget ceilings raise correctly.

## Phase 2 — Factory.Agents (the harness)

The .NET wrapper around the Claude SDK transport.

- `IAgentTransport` / `CliAgentTransport` — spawn `claude -p --output-format stream-json`,
  stream-parse NDJSON, surface typed events
- `AgentMessage` model: `SystemInit`, `Assistant`, `ToolResult`, `RateLimit`, `Result`
- `AgentProfile` -> CLI argument materialisation:
  - `Thin`: `--tools "" --system-prompt <lean> --setting-sources "" --disable-slash-commands --strict-mcp-config`
  - `Thick`: minimal tool allowlist, **stable prefix**, `--exclude-dynamic-system-prompt-sections`
- `AgentRunResult`: cost, usage breakdown, session id, turns, stop reason, structured payload
- `StructuredAgent<T>`: `--json-schema` -> typed T, with repair retry
- `ResponseCache`: SHA-256 of (profile + prompt + context digest) -> cached result
- `ModelTier` -> model id mapping; `AuthProbe` for the `--bare` fast path
- Retry/backoff on rate limits, `--max-budget-usd` wiring

**Gate:** live call returns typed result with usage; thin profile measures ~99% below default;
cache hit returns without spawning a process.

## Phase 3 — Factory.Runtime

- `Orchestrator`: poll -> lease -> dispatch -> gate -> advance; bounded concurrency; drain
- `IStation` + executors: Intake, Decompose, Plan, Implement, Verify, Review, Integrate, Delegate
- `DeterministicVerifier`: executes Command/TestsPass/FileExists criteria at **zero token cost**
- `Workspace`: git worktree/branch per item, cleanup on failure
- `FactoryHost`: loads blueprint, wires stations, exposes in/out ports
- `Composition`: `CompositeBlueprint`, child factory resolution, depth/cycle limits, cost roll-up

**Gate:** a work item flows Draft -> Done autonomously; a composite runs two linked factories.

## Phase 4 — Factory.Evolution

- `PromptRegistry`: versioned prompt assets, content hash, champion pointer
- `RunStats` mined from ledger; `Fitness` scoring function
- `ChampionChallenger`: traffic split, `minSamples`, Wilson lower-bound promotion gate
- `Optimizer` station: champion + worst-run traces -> challenger prompt
- Emits improvement `WorkItem`s back into the backlog (`Provenance = Evolution`)

**Gate:** promotion is refused on noisy/insufficient data and accepted on a clear win;
decisions land in the ledger.

## Phase 5 — Factory.Cli

`init`, `up`, `build`, `intake`, `add`, `status`, `ls`, `show`, `link`, `evolve`, `report`.
Single-file publish + `install.sh` so deployment is one command.

**Gate:** `factory up` in a fresh codebase works with no prior setup.

## Phase 6 — Tests

xUnit across domain, ledger, budgets, profiles, verification, promotion gate.
`FakeTransport` replays recorded NDJSON so agent tests are deterministic and free.

**Gate:** `dotnet test` green.

## Phase 7 — End-to-end proof

- One command deploys the factory around a codebase.
- One prompt builds a complete, verified application.

## Phase 8 — Self-improvement

Deploy the factory around its own repository; let intake and evolution file and execute
improvement items against the factory's own codebase. Capture measured before/after.
