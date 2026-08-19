#!/usr/bin/env bash
# The gate. One command that answers "is this repository good".
#
#   ./verify.sh              build, test, format, then acceptance criteria
#   ./verify.sh --fast       skip the acceptance criteria (build, test, format only)
#
# Exits non-zero if anything fails. Nothing here writes to the working repository: the
# acceptance criteria run against a throwaway clone, because proving that the factory notices
# its own commit has moved means moving a commit, and that must never be your checkout.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOLUTION="$ROOT/SoftwareFactory.sln"
WORK="$(mktemp -d)"
FAST=0
[ "${1:-}" = "--fast" ] && FAST=1

PASS=0
FAIL=0
FAILED_NAMES=()

trap 'rm -rf "$WORK"' EXIT

pass() { printf '  \033[32m✔\033[0m %s\n' "$1"; PASS=$((PASS + 1)); }
fail() { printf '  \033[31m✘\033[0m %s\n' "$1"; FAIL=$((FAIL + 1)); FAILED_NAMES+=("$1"); }

step() {                               # step <name> <command...>
  local name="$1"; shift
  if "$@" >"$WORK/out" 2>&1; then pass "$name"; else fail "$name"; tail -25 "$WORK/out" | sed 's/^/      /'; fi
}

check() {                              # check <name> <predicate...>
  local name="$1"; shift
  if "$@" >/dev/null 2>&1; then pass "$name"; else fail "$name"; fi
}

contains()     { grep -qF -- "$2" <<<"$1"; }
not_contains() { ! grep -qF -- "$2" <<<"$1"; }
matches()      { grep -qE -- "$2" <<<"$1"; }

section() { printf '\n\033[1m%s\033[0m\n' "$1"; }

# ── the toolchain gates ──────────────────────────────────────────────────────
# Serialised deliberately. These compile, and a second compile running beside them contends
# for the same MSBuild and Roslyn servers, which is how this repository produces spurious
# failures. Never background a step here.

section "Build"
step "solution builds" dotnet build "$SOLUTION" --nologo -v quiet

section "Tests"
step "test suite passes" dotnet test "$SOLUTION" --nologo -v quiet

section "Format"
step "no formatting drift" dotnet format "$SOLUTION" --verify-no-changes --no-restore -v quiet

if [ "$FAST" -eq 1 ]; then
  section "Result"
  printf '%d passed, %d failed (acceptance criteria skipped)\n' "$PASS" "$FAIL"
  [ "$FAIL" -eq 0 ]; exit
fi

# ── acceptance criteria ──────────────────────────────────────────────────────
# Exercised through the built CLI, the way a user reaches them, rather than by calling the
# functions behind them. A criterion that only ever calls the function cannot tell you the
# command was never wired up.

section "Acceptance criteria"

CLI="$WORK/cli"
dotnet publish "$ROOT/src/Factory.Cli/Factory.Cli.csproj" -c Release -o "$CLI" --nologo -v quiet >/dev/null 2>&1 \
  || { fail "the CLI publishes"; printf '\n%d passed, %d failed\n' "$PASS" "$FAIL"; exit 1; }

new_factory() {
  local dir="$WORK/f$(od -An -N4 -tu4 </dev/urandom | tr -d ' ')"
  mkdir -p "$dir"
  git -C "$dir" init -q .
  git -C "$dir" -c user.email=v@v -c user.name=v commit -q --allow-empty -m init
  "$CLI/factory" init --dir "$dir" >/dev/null 2>&1
  echo "$dir"
}

cat >"$WORK/seed.py" <<'PY'
import json, os, sys
target, kind = sys.argv[1], sys.argv[2]
led, seq = os.path.join(target, ".factory", "ledger.jsonl"), [9000]
def add(f, d):
    seq[0] += 1
    d["eventId"], d["at"], d["seq"] = f"evt_v{seq[0]}", "2026-08-19T14:00:00+00:00", seq[0]
    f.write(json.dumps(d) + "\n")
def machine(cid, stmt):
    return {"id": cid, "statement": stmt, "verification": {"kind": "command", "command": "true",
            "expectExitCode": 0, "timeoutSeconds": 60, "isDeterministic": True, "describe": "`true` exits 0"}}
def judged(cid, stmt):
    return {"id": cid, "statement": stmt, "verification": {"kind": "judge", "rubric": "is it sensible",
            "isDeterministic": False, "describe": "judged: is it sensible"}}
def item(i, t, crits=None, parent=None):
    return {"id": i, "title": t, "intent": "", "kind": "Feature", "requirements": [],
            "acceptanceCriteria": crits or [], "state": "Ready", "priority": 2, "labels": [],
            "dependsOn": [], "parentId": parent, "provenance": {"kind": "Human"}, "assumptions": [],
            "attempts": 0, "spentUsd": 0, "createdAt": "2026-08-19T14:00:00+00:00",
            "updatedAt": "2026-08-19T14:00:00+00:00", "isFullyDeterministic": True}
with open(led, "a") as f:
    if kind == "superseded":
        add(f, {"type": "work_item_filed", "item": item("wi_parent", "a decomposed parent")})
        add(f, {"type": "work_item_filed", "item": item("wi_child", "the real work", parent="wi_parent")})
        add(f, {"type": "work_item_state_changed", "itemId": "wi_parent", "from": "Ready",
                "to": "InProgress", "reason": "dispatched"})
        add(f, {"type": "work_item_state_changed", "itemId": "wi_parent", "from": "InProgress",
                "to": "Superseded", "reason": "decomposed into 2 child items"})
    elif kind == "verdicts":
        add(f, {"type": "work_item_filed", "item": item("wi_pass", "all passed",
                [machine("ac_1", "one"), machine("ac_2", "two")])})
        add(f, {"type": "criteria_verified", "itemId": "wi_pass", "results": [
            {"criterionId": "ac_1", "passed": True, "detail": "ok"},
            {"criterionId": "ac_2", "passed": True, "detail": "ok"}]})
        add(f, {"type": "work_item_filed", "item": item("wi_part", "one failed",
                [machine("ac_3", "one"), machine("ac_4", "two")])})
        add(f, {"type": "criteria_verified", "itemId": "wi_part", "results": [
            {"criterionId": "ac_3", "passed": True, "detail": "ok"},
            {"criterionId": "ac_4", "passed": False, "detail": "exited 1"}]})
        add(f, {"type": "work_item_filed", "item": item("wi_skip", "never verified",
                [machine("ac_5", "one"), machine("ac_6", "two")])})
        add(f, {"type": "run_completed", "record": {"runId": "r1", "itemId": "wi_skip",
                "stationId": "implement", "costUsd": 1.25}})
        add(f, {"type": "work_item_updated", "item": item("wi_skip", "never verified",
                [machine("ac_5", "one"), machine("ac_6", "two")])})
        add(f, {"type": "work_item_filed", "item": item("wi_mixed", "machine and judged",
                [machine("ac_7", "one"), machine("ac_8", "two"), judged("ac_9", "reads well")])})
        add(f, {"type": "criteria_verified", "itemId": "wi_mixed", "results": [
            {"criterionId": "ac_7", "passed": True, "detail": "ok"},
            {"criterionId": "ac_8", "passed": True, "detail": "ok"}]})
        add(f, {"type": "work_item_filed", "item": item("wi_judge", "judged only",
                [judged("ac_10", "reads well")])})
PY

printf '\n  A decomposed parent is superseded, never done\n'
F1="$(new_factory)"; python3 "$WORK/seed.py" "$F1" superseded
ALL="$("$CLI/factory" ls --all --dir "$F1" 2>&1)"
DEF="$("$CLI/factory" ls --dir "$F1" 2>&1)"
check "renders as superseded"                     contains "$ALL" "superseded"
check "never renders as done"                     not_contains "$ALL" "done"
check "hidden from the default backlog"           not_contains "$DEF" "wi_parent"
check "the outstanding child stays visible"       contains "$DEF" "wi_child"

printf '\n  The passed column counts what was settled\n'
F2="$(new_factory)"; python3 "$WORK/seed.py" "$F2" verdicts
LS="$("$CLI/factory" ls --dir "$F2" 2>&1)"
check "column is headed 'passed'"                 contains "$LS" "passed"
check "a fully verified item reads 2/2"           matches "$LS" 'wi_pass .* 2/2'
check "a partly verified item reads 1/2"          matches "$LS" 'wi_part .* 1/2'
check "an unverified item reads a dash"           matches "$LS" 'wi_skip .* —/2'
check "judged criteria leave the total alone"     matches "$LS" 'wi_mixed .* 2/2'
check "an all-judged item is not a ratio"         matches "$LS" 'wi_judge .* judged'
check "cost survives a stale item update"         matches "$LS" 'wi_skip .* \$1\.250'
check "show marks a passing criterion"            contains "$("$CLI/factory" show wi_part --dir "$F2" 2>&1)" "passed"
check "show marks a failing criterion"            contains "$("$CLI/factory" show wi_part --dir "$F2" 2>&1)" "failed"
check "show marks one never checked"              contains "$("$CLI/factory" show wi_skip --dir "$F2" 2>&1)" "never checked"
check "show marks a judged one deferred"          contains "$("$CLI/factory" show wi_mixed --dir "$F2" 2>&1)" "deferred to review"

printf '\n  The harness notices it is older than the repository\n'
CLONE="$WORK/clone"
git clone -q "$ROOT" "$CLONE"
"$CLI/factory" init --dir "$CLONE" >/dev/null 2>&1
AT_HEAD="$("$CLI/factory" status --dir "$CLONE" 2>&1)"
git -C "$CLONE" -c user.email=v@v -c user.name=v commit -q --allow-empty -m "move HEAD past the built binary"
BEHIND="$("$CLI/factory" status --dir "$CLONE" 2>&1)"
check "silent while the build matches HEAD"       not_contains "$AT_HEAD" "commits behind"
check "warns once HEAD has moved"                 contains "$BEHIND" "1 commit behind"
check "names the remedy"                          contains "$BEHIND" "install.sh"
check "an unrelated repository never warns"       contains "$("$CLI/factory" doctor --dir "$(new_factory)" 2>&1)" "not built from this repository"

printf '\n  A baseline keeps the evidence that decides a gate\n'
F4="$(new_factory)"
printf '{"commit":"deadbeefdeadbeef","passing":{"build":true,"test":false},"capturedAt":"2026-08-19T13:57:53+00:00"}' \
  >"$F4/.factory/baseline.json"
DOC="$("$CLI/factory" doctor --dir "$F4" 2>&1)"
check "doctor names the gate that cannot block"   contains "$DOC" "can no longer block anything"
check "status names the gates that are off"       contains "$("$CLI/factory" status --dir "$F4" 2>&1)" "gates off"
check "a baseline predating attempts still loads" not_contains "$DOC" "no baseline captured"

section "Result"
printf '%d passed, %d failed\n' "$PASS" "$FAIL"
for n in ${FAILED_NAMES+"${FAILED_NAMES[@]}"}; do printf '  failed: %s\n' "$n"; done
[ "$FAIL" -eq 0 ]
