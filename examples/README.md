# Proof artifacts

These are real outputs, kept with their own git history. They are excluded from the
factory's repository (see `.gitignore`) because each is an independent checkout.

## `one-prompt-app/`

Built by a single command in an empty directory:

    factory build "A Python CLI tool called 'todo' in todo.py that manages a todo list
    stored in todos.json. It must support: add <text>, list, done <index>.
    Keep it a single file with no external dependencies." --yes

Intake derived 11 acceptance criteria, all machine-checkable. Cost $0.30 across 4 model
calls. The review station skipped itself because the commands had already proved the work.
`todo.py` runs; `todos.json` is its state.

## `linked-factories/`

Two factories (`api/`, `web/`) linked into a composite whose pipeline became
`decompose → api → web`. One work item was routed through both children, each of which ran
its own full pipeline and committed in its own repository. Child spend rolled up to the
parent's budget.
