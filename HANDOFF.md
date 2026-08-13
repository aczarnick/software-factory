# Handoff

State of this repository as of the last session. `README.md` explains what the factory *is*;
this file explains where the work stands and how to pick it up.

## Where things are

Everything lives in `/opt/app/software-factory`. Nothing is outside it.

    factory version     # 1.1.x + commit
    factory status      # version, toolchain, usage windows, backlog, spend
    factory ls          # the queue
    factory report      # token economy and prompt fitness, from the ledger

`.factory/` is this deployment's own state — the event ledger (every work item, model call,
gate verdict and prompt promotion), the versioned prompt registry, the response cache, and
the toolchain baseline. It is gitignored but must not be deleted: it is the factory's memory.

## Environment

- Requires **.NET 10** (`global.json` pins SDK `10.0.400`) and the `claude` CLI on `PATH`.
- Build tooling on this host is intermittently unreliable: the Roslyn compiler server dies
  with `csc.dll exited with code 132` on roughly half of builds. **Retry rather than
  believing the first failure.** The factory's own check station already does this; a human
  or agent running `dotnet build`/`test` by hand should too.

## Verified

- `dotnet test` — **114 passing**.
- One command deploys a factory into a bare directory; one prompt builds a working app
  (`examples/one-prompt-app`, built by `factory build "..."`, 11 machine-checked criteria, $0.30).
- Two factories link into a composite that executes as one (`examples/linked-factories`).
- The factory has committed working code to its own source — `factory cancel` is its work,
  and it runs.

## Queued work

Run `factory up` to continue. The backlog is all machine-checked and falls into two groups.

**Observability** — the factory cannot currently be watched without reading process trees:
heartbeat file with stall detection, `factory ps`, `factory watch`, `factory doctor` tests.

**Self-remedy after external change** — filed after another factory landed a .NET 10 retarget
on master that this environment could not build. The factory reported it as a broken build,
which was misleading: the work was fine, the environment had drifted from what master now
required. Three items cover detecting a toolchain/environment mismatch distinctly from a
regression, attempting bounded remediation, and recapturing a stale baseline when master
moves from outside.

## Known gaps

- **Observability is the blocking gap.** Until the heartbeat lands, a long run is opaque; use
  `factory ls` and the ledger rather than inspecting processes.
- **The evolution loop has never promoted a prompt.** It has run and correctly *declined* to
  act on thin evidence, which is the behaviour that matters, but no champion has yet been
  replaced on real data. It needs ~10 runs per station before it will propose anything.
- **A baseline recorded under load is untrustworthy.** Fixed by retrying failed checks and
  recording attempt counts, but the deeper invariant — never capture a baseline while agents
  are compiling — is enforced only by capturing once before dispatch.

## Operating notes

- Run one factory at a time per repository. Concurrency is limited to 1 by default because the
  toolchain gate compiles, and parallel items contend for the same build tooling.
- Never edit tracked files while a factory is running against the repo: integration merges into
  the checkout, and a dirty mainline blocks it. The factory now blocks rather than failing in
  that case, preserving the worktree, and `factory activate` requeues it.
- Budgets are enforced before dispatch (`--budget`, `--item-budget`). Spend is in the ledger.
