# Agent instructions

Read [README.md](README.md) for the overview; this file contains repository rules.

## Commands
Run from the repository root; this host's SDK is installed under `$HOME/.dotnet`.
```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
export TMPDIR="${TMPDIR:-$HOME/.cache/aeso-calendar-sync}"; mkdir -p "$TMPDIR"
dotnet build Aeso.CalendarSync.slnx --configuration Release
dotnet test Aeso.CalendarSync.slnx --configuration Release
python3 scripts/test_runner.py
git diff --check
```

## Structure and style
- .NET 10 console application; nullable reference types enabled, AWS SDK v4, Google authentication library, xUnit tests. Use the `.slnx` solution, not a separate `.sln`.
- Application code belongs in `src/`, C# tests in `tst/`, documentation in `docs/`; `scripts/` contains the Hermes launcher and its Python tests.
- One class or interface per C# file, named after that type; keep interfaces and implementations in separate files.
- Keep synchronization deterministic. Use injected SDK/HTTP clients and clocks; preserve cancellation and explicit disposal ownership.
- Default tests are hermetic: fake AWS/HTTP transports, synthetic credentials, fixed clocks; no network, live school data, AWS login, or Google mutations.

## Git workflow
- Fetch and inspect branch/status before editing; preserve unrelated work. Use a feature/bug branch and PR into `main`; never push implementation directly to `main` or `master`.
- Follow the branch conventions in `.github/ISSUE_TEMPLATE/`; isolate commits by task. Keep PRs draft while required live acceptance is outstanding.
- Run the checks above before committing. Verify remote SHA/PR state after a push; local tests do not establish GitHub CI or deployment success.

## Boundaries
**Always**
- Read [docs/spec.md](docs/spec.md) before reconciliation changes: all Events-list items, separate slots, Lisbon times, calendar-default reminders, future `[AESO]` prefix-only ownership, protected past/nonprefixed entries, complete-read barrier and exact write readbacks.
- Read [docs/setup.md](docs/setup.md) before secret-loading changes: fresh SSM SecureString values in memory, complete payload validation before school/Google requests, explicit workload profile and fail-closed errors.
- Read [docs/hermes-deployment.md](docs/hermes-deployment.md) and [docs/roles-anywhere.md](docs/roles-anywhere.md) before runtime, identity, scheduling or deployment changes; recheck live state rather than assuming resources exist.
**Ask first**
- Obtain approval for AWS provisioning, IAM/trust changes, live calendar writes, or scheduler activation. A live read-only preview must be approved before apply; publishing code does not authorize activation.
- Confirm changes to ownership, deletion, secret backend, workload identity or deployment automation. Keep runtime secrets out of GitHub build jobs.
**Never**
- Commit secrets or downloaded school data; load application secrets from local files/environment variables, persist a secret cache, or expose values/provider bodies in diagnostics. The protected Roles Anywhere host key is the approved bootstrap exception, outside Git.
- Edit, create or delete anything under `~/.aws`; José performs AWS login and profile configuration manually. Never fall back to operator credentials when the dedicated runtime profile is unavailable.
- Treat authentication failures, empty/truncated/unstable sources, offline snapshots or failed reads as permission to reconcile/delete calendar events.

## Living doc
Add a concise rule when an agent correction recurs; prune superseded guidance with the implementation change. Keep detailed procedures in the linked docs and this file under 50 lines.
