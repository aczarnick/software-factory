# Software Factory

An autonomous, self-improving software factory built as a .NET harness around the
Claude Agent SDK.

```bash
cd your-project
factory build "a Python CLI that manages a todo list in JSON"
```

One command deploys a factory into the codebase. One prompt produces working,
verified, committed software.

---

## What it adds to a raw agent loop

| Raw agent loop | Software Factory |
|---|---|
| Stateless conversation | Durable, event-sourced work ledger; crash-resumable |
| One prompt, one answer | A pipeline of specialised stations with gates |
| Trust the model's word | Acceptance criteria checked by shell command |
| Unbounded token spend | Budgeted, profiled, cached token economy |
| Static prompts | Versioned prompts promoted only on statistical evidence |
| One agent | Factories that link into larger factories |
| Human drives every step | A daemon that pulls work when work exists |

The engine is **generic**. It knows about work, stations, gates, and budgets — not about
React or Rust. Everything domain-specific lives in a declarative **Blueprint**.

---

## The token economy

Token cost is a designed subsystem, grounded in measurement against this transport.
Identical trivial task, same wall clock, Haiku:

| Profile | Billed input | Cost |
|---|---|---|
| Default agent (all tools, full preamble, settings, skills) | 19,336 | $0.008543 |
| **Thin** (no tools, lean system prompt, no ambient context) | **165** | **$0.000973** |

**A 99.1% reduction in billed input and 8.8× lower cost for the same work.**

A third measurement decided the architecture. A "lean thick" run — custom system prompt
*and* tools — cost **$0.018**, more than twice the plain thick run's $0.0072, because
replacing the preamble invalidated the shared cache prefix and forced a 6,898-token cache
write at 1.25× rate. Hence the governing rule:

> **Strip aggressively where there are no tools. Stabilise relentlessly where there are.**

Thin stations win by removing context. Thick stations win by keeping their prefix
byte-identical so cache reads (~10% rate) dominate. Optimising a thick station the thin way
makes it worse.

The mechanisms, in order of leverage:

1. **Deterministic verification** — acceptance criteria are shell commands, checked at zero
   token cost. Verification runs after every attempt and every retry, so moving it off the
   model removes the most repeated call in the system. It is also the only kind of check
   that cannot be argued out of a failure.
2. **Early exit** — when every criterion was machine-checked and passed, the review station
   is skipped entirely. A model has nothing to add to a proof.
3. **Profile stripping** — thin stations drop tools, preamble, settings, skills, MCP.
4. **Cache-stable prefixes** — thick stations never vary their system prompt per run.
5. **Response cache** — content-hash of profile + prompt + workspace digest; a hit skips the
   call outright.
6. **Model tiering** — Haiku reviews, Sonnet implements, Opus decomposes.
7. **Context budgeting** — planning stations get a bounded repository *digest*, not a tree.
8. **Budget ceilings** — enforced at run, item, and daily scope, before dispatch.

`factory report` prints all of it from the ledger.

---

## Guarantees, not opinions

Wherever a deterministic tool can answer a question, the factory asks the tool instead of the
model. This is cheaper, but that is the lesser reason: a check the agent did not author is a
check the agent cannot satisfy by lowering the bar.

**The repository's own toolchain is a gate.** `check` detects and runs the compiler, test
suite, linter and formatter the project already has — `dotnet build`/`test`, `cargo
clippy`/`fmt`, `go vet`, whatever `package.json` declares, `ruff`/`pytest`. Zero tokens, and
the tool's own error output is fed verbatim into the next implementation attempt, because a
compiler error is already the best possible description of what to fix.

**Failures are attributed, not assumed.** Before work starts, the toolchain runs on the
mainline to record what was already passing, cached against the commit. Afterwards, only
checks that *regressed* block the item. A repository that arrived with a failing linter does
not fail every item forever — and a check introduced by the work itself must pass, or the
work has not been demonstrated.

**Acceptance criteria prefer commands.** Intake is instructed to write criteria a shell can
settle, and to exercise the feature the way a user reaches it — invoking the built program
proves it is wired up; calling the function behind it does not. That last rule came from
watching the factory pass a test for a CLI command it had not routed.

**Review is skipped when nothing needs judging.** If every criterion was machine-checked and
passed, a model has nothing to add to a proof.

---

## Staying inside the usage limits

The transport reports its own ceilings: every run emits a `rate_limit_event` carrying a
status, which window it applies to (five-hour, weekly) and when that window resets. The
factory reads that sensor rather than inferring limits from failures after the fact.

The response is graded:

| Reported status | What the factory does |
|---|---|
| allowed | full configured concurrency |
| warning | narrows to one item at a time and spaces runs out, stretching the window |
| rejected | stops dispatching, lets in-flight items drain, waits for the reset |

Concurrency is re-derived on every scheduling pass, so throttling takes effect immediately
rather than at the next run. Window state is persisted, so a factory restarted inside an
exhausted window does not spend its way back into the same rejection. If a reset is further
away than the wait ceiling, it stops and leaves the work queued rather than blocking a
command for hours.

---

## The pipeline

```
intake → decompose → plan → implement → check → verify → review → integrate
```

- **intake** — an agent, not a form. Elicits requirements and acceptance criteria, and is
  instructed to prefer criteria a machine can check.
- **decompose** — splits work into independently buildable, independently verifiable items
  with a real dependency graph.
- **plan** — an edit plan from a bounded repo digest.
- **implement** — the only station with tools, working in an isolated git worktree.
- **check** — the repository's own compiler, tests and linter. Zero tokens, not authored by
  the thing being checked, and only regressions block.
- **verify** — runs the acceptance criteria. Zero tokens. Failures route back to
  implementation *with the failure attached*, so the station learns without a human relaying it.
- **review** — judges what a command cannot see. Skipped when nothing needs judging.
- **integrate** — merges to the mainline. The only station that touches your checkout.

---

## Self-improvement

Prompts are versioned assets with lineage. Every run records the exact version that produced
it, its cost, tokens, turns, and gate verdict.

The hard part of a self-improving system is not generating variants — it is refusing to adopt
one that only looked better. A challenger winning 4 of 5 runs against a champion at 80% is
indistinguishable from noise, and a system that promotes it drifts randomly while reporting
continuous improvement.

Promotion therefore requires **two independent things to agree**:

- a better composite fitness — `pass rate − cost − turns − retries`, so a prompt that passes
  more often but costs three times as much has not improved anything; and
- a **Wilson score lower bound** on the challenger's pass rate that still exceeds the
  champion's observed rate. The bound collapses towards zero when samples are few, so an
  undersampled challenger cannot clear the bar however lucky its streak.

Challengers are also rejected before trial if they exceed 2× the champion's length: every
token is paid on every run forever.

Stations file work too. A review that spots something out of scope files a follow-up; the
evolution loop files items about the factory's own defects. Agent-filed work lands as a
**proposal** in Draft rather than queued work, so one request cannot snowball into unbounded
self-directed effort. `factory up --include-proposed` opts in.

---

## Composition

Factories expose ports and link into larger factories.

```bash
factory init --dir ./api
factory init --dir ./web
factory link ./api --as api --pipeline
factory link ./web --as web --pipeline
```

The parent's pipeline becomes `decompose → api → web`. A delegate is an ordinary station to
the parent and an entire factory internally — so a factory of factories is just a factory.
Child spend rolls up into the parent's budget and report; recursion is bounded by a depth
limit.

---

## Commands

```
factory build "<what you want>"    Deploy if needed, then build it
factory up [--daemon]              Work the backlog; --daemon keeps watching
factory init                       Deploy without starting work

factory intake ["<request>"]       Interactive requirements conversation
factory add "<title>" [--criterion "<cmd>"]
factory activate <id> | --all      Promote proposed work into the queue
factory ls [--all] · show <id> · status

factory link <path> [--as <name>] [--pipeline]

factory evolve                     Score prompts, settle trials, propose challengers
factory prompts [--show <station>] Versions and champions
factory report                     Token economy and prompt fitness
```

Options: `--dir`, `--budget`, `--item-budget`, `--concurrency`, `--yes`,
`--include-proposed`, `--evolve` / `--no-evolve`.

---

## Measured outcomes

Three things were run end to end against the real transport, not simulated:

**One prompt → a working application.** `factory build "a Python CLI todo tool…"` in an empty
directory deployed a factory, elicited 11 acceptance criteria (all machine-checkable),
planned, implemented, verified, and committed a working `todo.py`. Total: **$0.30, 4 model
calls**. The review station skipped itself because the commands had already proved the work.

**Composition across two codebases.** A parent factory with `decompose → api → web` routed
one item through two linked child factories, each of which ran its own full pipeline in its
own repository and committed there. Child spend rolled up into the parent's budget.

**The factory improving itself.** Deployed onto its own repository and asked to add a
`factory cancel` command, it decomposed the request into three dependent items, implemented
the first, verified it, and committed to its own source — code that matched the surrounding
conventions without being told what they were.

**The evolution gate refusing to act.** Asked to optimise prompts on one run of data, it
spent **$0.00** and reported `1/10 runs before proposing a challenger`. Forced past that
threshold, the optimiser inspected the evidence and still declined to change anything,
because a single passing run gives no basis to distinguish waste from necessary work. A
self-improving system that cannot say "leave it alone" does not improve — it drifts.

---

## Install

Requires the [.NET 10 SDK](https://dot.net) and the `claude` CLI on `PATH`.

```bash
./install.sh                  # to /usr/local/bin
PREFIX=~/.local ./install.sh
```

---

## Layout

```
SPEC.md                  The design document
PLAN.md                  The build plan
examples/                Proof artifacts, each with its own git history (see examples/README.md)
.factory/                This deployment's own state: ledger, prompt registry, cache, baseline

src/Factory.Core/        Domain, ledger, blueprint, budgets, verification  (no dependencies)
src/Factory.Agents/      Claude SDK harness: transport, profiles, cache, token economy
src/Factory.Runtime/     Orchestrator, stations, workspaces, composition
src/Factory.Evolution/   Prompt registry, evaluator, promotion gate, optimiser
src/Factory.Cli/         The `factory` command
tests/Factory.Tests/     85 tests; a fake transport exercises the whole pipeline for free
```

`SPEC.md` is the design document. `PLAN.md` is the build plan.

---

## Safety

Each item runs in its own git worktree and reaches the mainline only after its gates pass.
Stations get least-privilege tool allowlists — thin stations get no tools at all. Budgets are
enforced before dispatch, not discovered afterwards. Self-improvement is capped at a fraction
of the daily budget so it can never starve user work. Every item records who filed it, which
prompt version ran, what it cost, and why each gate passed or failed.
