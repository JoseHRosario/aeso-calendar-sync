# AESO calendar sync

Deterministic .NET 10 console application for the AESO **Events list**, not the regular lesson timetable. No frontend, public server, AWS, or embedded scheduler.

## Build and test

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
dotnet build Aeso.CalendarSync.slnx -c Release
dotnet test Aeso.CalendarSync.slnx -c Release
dotnet publish src/Aeso.CalendarSync -c Release -o /absolute/deployment/path
```

All default tests are hermetic. They use fake `HttpMessageHandler` transports and fixed clocks; no real credentials or services are required. Package restoration uses NuGet.

## Run

Supply **absolute** configuration and Google service-account credential paths. Copy the shape of `appsettings.example.json` into an external runtime configuration file; do not put credentials in this repository.

```bash
dotnet /absolute/deployment/path/Aeso.CalendarSync.dll --config /absolute/private/config.json --dry-run
# Defaults to live dry-run when no mode is given.
dotnet /absolute/deployment/path/Aeso.CalendarSync.dll --config /absolute/private/config.json
# Only after reviewing and approving the live preview:
dotnet /absolute/deployment/path/Aeso.CalendarSync.dll --config /absolute/private/config.json --apply
```

The launcher injects `AESO_SCHOOL_USERNAME` and `AESO_SCHOOL_PASSWORD` as environment variables from a private external store. Do not type school passwords in chat, pass them as arguments, or add them to app configuration. Service-account JSON stays in its external protected file. Grant that account permission to edit the existing target calendar and enable Google Calendar API in its Google project. No real Google access has been validated by the hermetic suite.

### Offline source validation

```bash
dotnet /absolute/deployment/path/Aeso.CalendarSync.dll --validate-source /absolute/path/downloaded-events-list.json
# Optional external config establishes a different approved school year:
dotnet /absolute/deployment/path/Aeso.CalendarSync.dll --config /absolute/private/config.json --source-file /absolute/path/downloaded-events-list.json
```

These modes parse the original Portuguese list fields and print an explicitly **offline** source report. They do not read Google, invent a Google reconciliation result, or authorize any write. `--apply` with either offline option is rejected. The downloaded source is sensitive school data: keep it private and do not commit it.

## Launcher contract

Live successful dry-run/apply emits exactly one JSON line on stdout:

```json
{"status":"success","sourceCount":44,"futureCount":43,"created":0,"deleted":0,"kept":43,"actions":[]}
```

The example illustrates the schema, not a claimed live Google result. Each action is `{ "kind": "create" | "delete", "summary": string, "start": string, "end": string }`. Timed values include the Europe/Lisbon UTC offset; deletions of all-day events use ISO dates with an exclusive end. Counts on dry-run are proposed changes. Counts on apply reflect completed verified changes. Restoration via PATCH is reported as a logical delete/create pair; it never creates a tombstone just to restore a manually edited event. Successful no-op apply still emits the JSON line; the launcher decides whether to stay silent.

On failure the program emits only sanitized stderr, exits 1, and emits no stdout success. It never prints remote response bodies, exception traces, usernames, passwords, tokens, credential contents, or private keys. Apply is not a transaction: verified earlier writes may have succeeded before a later failure. Rerun the live preview/apply to reconcile remaining work. No failure may be interpreted as an empty source.

Offline success has `status`, `mode` (`offline-validation` or `offline-source-preview`), `sourceCount`, `futureCount`, and `slots` with authoritative source names/descriptions/times. It deliberately omits Google change counts and actions.

## Configuration fields

| Property | Default / rule |
|---|---|
| `CalendarId` | Exact calendar ID in the example; calendar must be shared to the service account |
| `GoogleCredentialsPath` | Required absolute path for live operation |
| `SchoolOrigin` | Fixed allowlisted `https://aeso.inovarmais.com`; other origins rejected |
| `SchoolEnrolmentId` | `43850` |
| `SchoolTipoEnsino` | `1` |
| `SchoolStudentId` | **Backend** `8937`, not school process number `7945` |
| `SchoolYear` | Start year `2026`, meaning 2026-09-01 through 2027-08-31 |
| `SourceInitialLimit` | `30` |
| `SourceLimitStep` | `15` |
| `SourceMaximumLimit` | `3000`; maximum configurable 10000; equality at cap fails |

Unknown configuration properties are rejected to catch misspellings. At/after **2027-09-01**, current default scope refuses to run until the new enrolment/year are explicitly configured. Selected-student enrolment evidence must contain the configured `MatriculaId`, `TipoEnsino`, and `AnoLectivo`.

Read [docs/spec.md](docs/spec.md), [docs/architecture.md](docs/architecture.md), and [docs/setup.md](docs/setup.md) before enabling apply. Scripts, secret setup, notifications, live preview approval, and daily Hermes deployment are deliberately outside this application's scope.
