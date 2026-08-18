# Storage Adapters: Spec and Plans

Work planned on 2026-08-13, branch `storage-adapters`. **Phase 1 shipped 2026-08-14 and is
merged to `master`**; phases 2-4 are design and plans only. Start at the spec, then execute
the remaining phases in order. Read
[`notes/2026-08-14-phase-1-handoff.md`](notes/2026-08-14-phase-1-handoff.md) before phase 2 —
it carries what the diff cannot tell you.

## Read first

- [`specs/2026-08-13-storage-adapters-design.md`](specs/2026-08-13-storage-adapters-design.md) — the design, its seven decisions, and the limitations that could not be designed away.

## Phases

Each phase is independently buildable and verifiable, and leaves the factory working.

| Phase | Plan | Delivers | Status |
|---|---|---|---|
| 1 | [`plans/2026-08-13-storage-ports-phase-1.md`](plans/2026-08-13-storage-ports-phase-1.md) | `IWorkItemStore`, `IRunHistory`, `IRunHistorySink`; `Ledger` becomes `JsonlRunHistory`; `LedgerWorkItemStore`; host routed through the ports. No behaviour change. | **shipped 2026-08-14** (`f1a3eb2..ecec44c`, 142 tests green) |
| 2 | [`plans/2026-08-13-storage-ports-phase-2-plugins.md`](plans/2026-08-13-storage-ports-phase-2-plugins.md) | `FactoryProviderAttribute`, `PluginLoadContext`, `PluginCatalog`, `ProviderRegistry`, guard decorators, sink fan-out, provider config. | not started |
| 3 | [`plans/2026-08-13-storage-ports-phase-3-beads.md`](plans/2026-08-13-storage-ports-phase-3-beads.md) | `BeadsWorkItemStore`, the mapping, claim/heartbeat, reconcile-on-open, id and priority narrowing. | not started |
| 4 | [`plans/2026-08-13-storage-ports-phase-4-sync-gate.md`](plans/2026-08-13-storage-ports-phase-4-sync-gate.md) | Integrate sync gate, degraded reporting, doctor checks, backlog migration and cutover. | not started |

**Phase 1 is worth doing even if beads is never adopted** — it is a pure refactor that leaves
the factory behaviourally identical and makes every later phase a drop-in.

## Why this exists

`.factory/` is gitignored (`.gitignore:4`), so the backlog — 23 ready items, every dependency
edge, every acceptance criterion — exists on exactly one machine and nothing replicates it.
`HANDOFF.md` documents this with a comment where a mechanism belongs.

## The three things to know before implementing

1. **Beads is authoritative; the ledger keeps an audit copy.** Write order is beads first,
   ledger second. A failed beads write aborts the transition; a failed ledger write self-heals
   at the next reconcile-on-open.

2. **Beads leases are node-local.** `status` and `assignee` replicate, so mutual exclusion
   across machines works, but lease *expiry* does not — machine B cannot reclaim machine A's
   dead work. Reporting is in scope; auto-reaping is deliberately deferred. A dead machine's
   item needs an operator to requeue it.

3. **Only `integrate` is gated on sync.** Everything upstream is local and cheap to redo, so
   the worst case of an offline double-claim is wasted tokens rather than a double-merge.

## What was verified, and what was not

Every beads behaviour cited in the spec was probed against **beads 1.2.1** in a throwaway
database: id format, all nine status mappings via `status.custom`, dependency gating, atomic
claim and its lease fields, metadata round-trip, and offline writes with no remote.

Three things were **not** verified and are flagged where they matter:

- what the beads telemetry transmits (`metrics.disabled = false` by default);
- whether the 5-minute lease TTL is configurable — no config key was found;
- the `dolt sql-server` lifecycle inside CI containers.

Some `bd` flag spellings in the phase 3 plan were also not probed; that plan says which, and
to check them against `bd <command> --help` first.
