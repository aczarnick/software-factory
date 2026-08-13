# Software Factory — Specification

**Version:** 1.0
**Status:** Authoritative design document
**Harness language:** .NET 9 (C#)
**Agent substrate:** Claude Agent SDK via the `claude` CLI headless transport

---

## 1. What this is

A **Software Factory** is a deployable, autonomous, self-improving unit of software
production. It is a .NET harness wrapping the Claude Agent SDK that adds the things a
raw agent loop does not have:

| Raw agent loop | Software Factory |
|---|---|
| Stateless conversation | Durable, event-sourced work ledger |
| One prompt, one answer | A pipeline of specialised stations with gates |
| Trust the model's word | Machine-checked acceptance criteria |
| Unbounded token spend | Budgeted, profiled, cached token economy |
| Static prompts | Versioned prompts under continuous champion/challenger evaluation |
| One agent | Composable factories that link into larger factories |
| Human drives every step | Autonomous daemon that pulls work when work exists |

The factory is **generic**: it contains no knowledge of any particular product, language,
or domain. Everything specific lives in a declarative **Blueprint**. The same binary runs
a web-app factory, a refactoring factory, or a factory that improves other factories.

### 1.1 Design principles

1. **Generic core, declarative edge.** The engine knows about work, stations, gates, and
   budgets. It knows nothing about React or Rust. Blueprints supply that.
2. **Composable.** Factories expose typed ports. A station may delegate a whole work item
   to a child factory. Factories nest into larger factories.
3. **Durable.** Every state transition is an event in an append-only ledger. Kill the
   process mid-item; it resumes.
4. **Evidence-driven.** Every model call emits a Run record with tokens, cost, latency,
   prompt version, and gate verdict. Self-improvement is driven by that table, not by vibes.
5. **Economical.** Tokens are a budgeted resource enforced at run, item, and factory level.
   Determinism is always preferred over inference.
6. **Autonomous but bounded.** It runs unattended inside explicit budget, permission, and
   blast-radius limits.

---

## 2. Substrate: the Claude SDK transport

The harness drives the `claude` CLI in headless streaming mode — the same transport the
official Agent SDK uses:

```
claude -p --output-format stream-json --input-format stream-json --verbose
```

Each run yields a typed event stream terminating in a `result` message carrying
`total_cost_usd`, `usage` (input / output / cache-read / cache-write tokens),
`session_id`, `num_turns`, and `stop_reason`. That message is the factory's unit of
telemetry and the input to prompt evaluation.

Transport capabilities the harness exploits:

| CLI surface | Factory use |
|---|---|
| `--output-format stream-json` | Typed event stream, incremental progress |
| `--json-schema` | Structured station outputs, no brittle text parsing |
| `--model`, `--fallback-model` | Model tiering per station |
| `--tools`, `--allowed-tools`, `--disallowed-tools` | Least-privilege tool policy per station |
| `--system-prompt` | Replace the default agent preamble with a lean station prompt |
| `--setting-sources ""`, `--disable-slash-commands`, `--strict-mcp-config` | Strip ambient context |
| `--exclude-dynamic-system-prompt-sections` | Stabilise the cache prefix across runs |
| `--max-budget-usd`, `--max-turns` | Hard spend and loop ceilings |
| `--session-id`, `--resume`, `--fork-session` | Session reuse for warm prompt cache |
| `--permission-mode` | Blast-radius control |
| `--add-dir`, `--mcp-config`, `--agents` | Workspace scoping and extension |

**Auth note.** `--bare` mode reads only `ANTHROPIC_API_KEY` / `apiKeyHelper` and never
OAuth. The harness therefore probes the active auth mode at startup and only enables the
`--bare` fast path when an API key is present; under subscription OAuth it uses the
equivalent explicit strip flags, which measure the same.

**Structured-output turn floor.** A call carrying `--json-schema` spends one turn producing
the answer and another emitting it against the schema: a one-turn structured call reports
`num_turns: 2` and terminates as `error_max_turns` with an empty result. Any station asking
for JSON therefore needs at least three turns. The harness raises the limit itself whenever a
schema is attached, so no blueprint can misconfigure it into silent failure.

---

## 3. Token economy

Token cost is a designed subsystem, not an afterthought. The design is grounded in
measurements taken against this transport (Haiku, trivial task, same wall clock):

| Profile | Input tokens | Cache read | Cache write | Cost |
|---|---|---|---|---|
| **A** Default agent (all tools, full preamble) | 10 | 16,293 | 3,043 | $0.008543 |
| **B** Thin (no tools, lean system prompt, no settings) | **165** | 0 | 0 | **$0.000973** |
| **C** Thick (6 tools, default preamble) | 18 | 23,638 | 1,612 | $0.007205 |
| **D** Thin + `--json-schema` structured output | 928 | 0 | 0 | $0.002883 |

**Profile B cuts billed input from 19,336 tokens to 165 — a 99.1% reduction and 8.8× lower
cost for identical work.**

A fifth measurement was decisive: a "lean thick" run (custom system prompt *and* tools)
cost *more* ($0.018) than plain thick, because replacing the preamble invalidated the
shared cache prefix and forced a 6,898-token cache write at 1.25× rate. This yields the
governing rule of the token economy:

> **Strip aggressively where there are no tools. Stabilise relentlessly where there are.**

Thin stations win by removing context. Thick stations win by making their prefix *byte-identical*
across every run so cache reads (~10% of input rate) dominate. Optimising a thick station the
thin way makes it worse.

### 3.1 Mechanisms

| # | Mechanism | Effect |
|---|---|---|
| 1 | **Profile stripping** — thin stations run with no tools, lean prompt, no ambient settings | 99.1% input reduction (measured) |
| 2 | **Cache-stable prefixes** — thick stations never vary system prompt per run; variable content is appended last; `--exclude-dynamic-system-prompt-sections` removes per-machine drift | Cache reads bill at ~10% |
| 3 | **Model tiering** — Haiku classifies/routes/scores, Sonnet implements, Opus only decomposes architecture | Blueprint-declared per station |
| 4 | **Deterministic verification first** — command-based acceptance criteria run at **zero token cost**; agent judging only where no command check exists | Removes the most repeated model call |
| 5 | **Response cache** — content hash of (profile + prompt + context digest) short-circuits the model entirely on repeats | 100% saving on cache hit |
| 6 | **Early-exit gates** — a cheap thin station decides whether an expensive station runs at all | Avoids the call, not just shrinks it |
| 7 | **Context budgeting** — stations receive digests and diffs, never whole trees; hard per-station byte cap | Bounds worst case |
| 8 | **Output shaping** — `--json-schema` + `--max-turns` cap generation | Bounds output tokens |
| 9 | **Budget enforcement** — `--max-budget-usd` per run, plus ledger accumulators per item / factory / day | Hard stop, not a hope |
| 10 | **Session reuse** — `--resume` within a work item to land on a warm prefix | Converts writes into reads |

### 3.2 Budget model

Budgets are declared hierarchically and enforced at dispatch:

```
factory.budget.dailyUsd        -> daily ceiling across all work
factory.budget.perItemUsd      -> ceiling per work item (all stations, all retries)
station.budget.perRunUsd       -> passed to --max-budget-usd
```

Exceeding a ceiling raises `BudgetExhausted`, parks the item in `Blocked`, and emits a
ledger event. The factory never silently overspends.

---

## 4. Domain model

### 4.1 WorkItem

The unit of production. Filed by a human via the intake agent, or by an agent as part of
continuous improvement.

```
WorkItem
  Id, Title, Intent
  Kind            : Feature | Bug | Chore | Refactor | Spike | Improvement
  Requirements    : string[]
  AcceptanceCriteria : AcceptanceCriterion[]
  State           : Draft|Ready|InProgress|InReview|Verified|Done|Blocked|Failed|Cancelled
  Priority, Labels[]
  ParentId, DependsOn[]
  Budget          : per-item USD ceiling
  Provenance      : Human | Agent(stationId) | Evolution
  Assignment      : station, worktree, lease
```

### 4.2 AcceptanceCriterion — the heart of the design

```
AcceptanceCriterion
  Id, Statement
  Verification :
    | Command   { Cmd, ExpectExitCode, ExpectStdoutMatch }   -- zero tokens
    | TestsPass { Suite }                                    -- zero tokens
    | FileExists{ Path }                                     -- zero tokens
    | AgentJudge{ Rubric }                                   -- costs tokens
```

The intake agent is explicitly instructed to produce **machine-checkable criteria wherever
possible**. This is simultaneously the quality mechanism (the factory cannot declare
success it did not demonstrate) and the single largest token-reduction lever, because
verification is otherwise the most frequently repeated model call in the system.

### 4.3 Ledger

Append-only JSONL. Current state is a fold over events. Events:

`WorkItemFiled`, `WorkItemStateChanged`, `RunStarted`, `RunCompleted`, `ArtifactProduced`,
`GateEvaluated`, `BudgetConsumed`, `PromptPromoted`, `PromptDemoted`, `FactoryLinked`,
`DelegationStarted`, `DelegationCompleted`.

Guarantees: crash-resumable, fully auditable, and the substrate the evaluator mines.

### 4.4 Blueprint

Declarative factory definition (YAML/JSON), the only domain-specific surface.

```yaml
name: standard
budget: { dailyUsd: 25, perItemUsd: 3 }
stations:
  - id: intake       role: Intake      tier: sonnet  profile: thin   schema: workitems.json
  - id: decompose    role: Decompose   tier: opus    profile: thin   schema: plan.json
  - id: implement    role: Implement   tier: sonnet  profile: thick  tools: [Read,Write,Edit,Bash,Grep,Glob]
  - id: verify       role: Verify      tier: -       profile: none   # deterministic, zero tokens
  - id: review       role: Review      tier: haiku   profile: thin   schema: verdict.json
routing:
  Draft -> intake -> decompose -> implement -> verify -> review -> Done
gates:
  verify:  { onFail: retry(implement, max=2) }
  review:  { onFail: retry(implement, max=1), escalate: human }
```

---

## 5. Stations

A **Station** is a typed transform over a work item: `WorkItem × Context → StationResult`.

| Role | Tier | Profile | Output |
|---|---|---|---|
| **Intake** | sonnet | thin | WorkItems with requirements + acceptance criteria |
| **Decompose** | opus | thin | Child work items, dependency DAG |
| **Plan** | sonnet | thin | Ordered edit plan, files to touch |
| **Implement** | sonnet | thick | Code changes in an isolated worktree |
| **Verify** | — | none | Deterministic criterion results, zero tokens |
| **Review** | haiku | thin | Pass/fail verdict + findings |
| **Integrate** | — | none | Merge, commit |
| **Evolve** | sonnet | thin | Challenger prompts, improvement work items |
| **Delegate** | — | — | Forwards the item to a child factory |

Every station declares: model tier, token profile, tool allowlist, prompt reference,
output schema, retry policy, gate, and per-run budget. The engine is generic over all of it.

---

## 6. Intake — how work enters

Work enters through an agent, never a form.

**Interactive.** The intake agent converses with the user: clarifies intent, surfaces
unstated constraints, proposes acceptance criteria, and iterates until the user confirms.
It is instructed to prefer criteria that can be checked by a command.

**Non-interactive** (`--yes`, and for agent-filed work). The agent derives requirements and
acceptance criteria directly, marks assumptions explicitly in the item, and files it.

**Agent-filed work.** Any station may emit work items as a side output. The evolution loop
files items such as *"implement station fails 40% of the time on repos with no test runner"*.
This is the mechanism by which the factory improves itself: its own defects become its own
backlog. Agent-filed items carry `Provenance = Agent|Evolution` and are budget-capped
separately so self-improvement can never starve user work.

---

## 7. Prompt evolution

Prompts are versioned assets under continuous evaluation.

**Registry.** Every prompt is `station/<id>@v<N>` with a content hash. Runs record the exact
version used.

**Fitness.** From ledger run records, per prompt version:

```
fitness = w_pass * passRate
        - w_cost * normalisedCost
        - w_turns * normalisedTurns
        - w_retry * retryRate
```

**Champion/challenger.** The `Evolve` station reads the champion prompt plus a sample of its
worst runs (failures, expensive runs, high-retry runs) and proposes a challenger. The
challenger receives a traffic slice (default 20%) until `minSamples` (default 20) is reached.

**Promotion gate.** Promotion requires *both*:
- fitness improvement above a threshold, **and**
- a Wilson score lower bound on the challenger's pass rate that exceeds the champion's point estimate.

This prevents promotion on noise — the classic failure mode of naive self-improving systems.
Regressions are auto-demoted and rolled back. Every promotion and demotion is a ledger event,
so prompt lineage is fully auditable.

---

## 8. Composition — linking factories

Factories compose through typed **ports**.

- Every factory exposes an `in` port (accepts work items) and an `out` port (emits verified items).
- A station of role `Delegate` forwards an item to a child factory's `in` port and awaits its
  `out`. To the parent it is just a station; internally it is an entire factory.
- A **composite blueprint** declares members and links:

```yaml
name: platform
factories:
  - { name: api,      path: ./factories/api }
  - { name: web,      path: ./factories/web }
  - { name: contract, path: ./factories/contract }
links:
  - { from: contract.out, to: api.in }
  - { from: contract.out, to: web.in }
```

- Cost, tokens, and ledger events roll up from children to parent.
- Recursion is bounded by a depth limit and cycle detection, so a factory containing itself
  cannot run away.

Because a factory is itself a station, **a factory of factories is just a factory** — the
composition is closed under nesting, which is what makes "link factories to make larger
factories" true rather than aspirational.

---

## 9. Autonomy and deployment

**Deploy in one command.** `factory up` in any codebase: detects the project, materialises
`.factory/` if absent, starts the orchestrator, and begins pulling work.

**Build an app from one prompt.** `factory build "<prompt>"`: intake → decompose →
implement → verify → review → integrate, autonomously to completion.

**Orchestrator loop.** Lease-based dispatch (crash-safe), bounded concurrency, budget checks
before every run, exponential backoff on rate limits, graceful drain on shutdown. Polls the
backlog; when work exists, it works; when it does not, it idles cheaply without burning tokens.

**Isolation.** Each in-flight item gets its own git worktree/branch. Failed items are
discarded without touching the mainline.

### 9.1 CLI surface

```
factory init [--blueprint <name>]      Scaffold .factory/ around a codebase
factory up   [--daemon] [--max-concurrency N] [--budget N]
                                        Deploy and autonomously work the backlog
factory build "<prompt>" [--yes]        One prompt -> a built, verified application
factory intake                          Interactive requirements conversation
factory add "<title>"                   File a work item directly
factory status | ls | show <id>         Inspect backlog, runs, cost
factory link <child> [--as <name>]      Link a child factory
factory evolve [--promote]              Run the evaluation/optimisation loop
factory report                          Token, cost, and fitness report
```

---

## 10. Safety and guardrails

- **Budget ceilings** at run, item, and daily scope; hard stop, not advisory.
- **Least-privilege tools** per station; thin stations get no tools at all.
- **Workspace isolation** per item; mainline is only touched by `Integrate` after gates pass.
- **Blast-radius limits** — permission mode is per-station, and destructive shell patterns
  are denied by default.
- **Depth and cycle limits** on delegation and on self-improvement recursion.
- **Human escalation** — any gate may escalate rather than retry.
- **Full auditability** — the ledger records who (human or which station) filed every item,
  which prompt version ran, what it cost, and why each gate passed or failed.

---

## 11. Success criteria for this specification

1. `factory up` deploys a working factory around an arbitrary codebase in **one command**.
2. `factory build "<one prompt>"` produces a verified, running application.
3. Multiple factories link into a composite that executes as one.
4. Thin stations demonstrably cost ~1% of the naive agent-call input tokens.
5. Prompt versions are scored from real run data and promoted only through a statistical gate.
6. The factory files and executes improvement work items against its own codebase.
