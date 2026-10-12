# AESO calendar sync

Deterministic .NET 10 console application for the AESO **Events list**, not the regular lesson timetable. No frontend, public server, or embedded scheduler. Application secrets are fetched from AWS SSM Parameter Store into memory.

## Build and test

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
dotnet build Aeso.CalendarSync.slnx -c Release
dotnet test Aeso.CalendarSync.slnx -c Release
dotnet publish src/Aeso.CalendarSync -c Release -o /absolute/deployment/path
```

All default tests are hermetic. They use fake `HttpMessageHandler` transports and fixed clocks; no real credentials or services are required. Package restoration uses NuGet.

## Run

Supply an **absolute** nonsecret configuration path. Copy `appsettings.example.json` into an external runtime configuration file; do not put credentials in it or this repository.

```bash
dotnet /absolute/deployment/path/Aeso.CalendarSync.dll --config /absolute/private/config.json --dry-run
# Defaults to live dry-run when no mode is given.
dotnet /absolute/deployment/path/Aeso.CalendarSync.dll --config /absolute/private/config.json
# Only after reviewing and approving the live preview:
dotnet /absolute/deployment/path/Aeso.CalendarSync.dll --config /absolute/private/config.json --apply
```

Live startup resolves only the native AWS SDK profile `aeso-calendar-sync`, then fetches the two approved SecureString parameters in one `GetParameters` request with `WithDecryption=true`. Both payloads must validate before any school or calendar request. Secrets and Google credentials remain in memory for the process lifetime; there is no disk cache, environment secret loading, credential-file fallback or secret-bearing CLI option. Each new process fetches a fresh snapshot. A missing/invalid profile fails rather than falling back to ambient AWS credentials.

The dedicated workload profile/bootstrap is provisioned outside the application (approved host IAM Roles Anywhere certificate/key exception); the app never creates or edits AWS configuration, logs in or manages AWS caches. No caller role ARN is hardcoded or checked with STS: deployment must bind this dedicated profile to the approved role, never a personal/Minerva identity. Grant the Google service account edit access to the existing calendar and enable Calendar API. Hermetic tests do not verify live AWS identity, parameter access or Google permissions.

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
| `AwsRegion` | `eu-west-1`, pinned for live operation |
| `AwsProfile` | Dedicated native SDK profile `aeso-calendar-sync`; no ambient fallback |
| `AwsAccountId` | `497087877832`, validated against each parameter ARN |
| `SchoolCredentialsParameter` | `/aeso-calendar-sync/prod/school-credentials` |
| `GoogleServiceAccountParameter` | `/aeso-calendar-sync/prod/google-service-account` |
| `SchoolOrigin` | Fixed allowlisted `https://aeso.inovarmais.com`; other origins rejected |
| `SchoolEnrolmentId` | `43850` |
| `SchoolTipoEnsino` | `1` |
| `SchoolStudentId` | **Backend** `8937`, not school process number `7945` |
| `SchoolYear` | Start year `2026`, meaning 2026-09-01 through 2027-08-31 |
| `SourceInitialLimit` | `30` |
| `SourceLimitStep` | `15` |
| `SourceMaximumLimit` | `3000`; maximum configurable 10000; equality at cap fails |

Unknown configuration properties are rejected, including legacy `GoogleCredentialsPath`, username/password and environment/file secret configuration keys. The five AWS fields are nonsecret and pinned to the approved values for live operation. At/after **2027-09-01**, current default scope refuses to run until the new enrolment/year are explicitly configured. Selected-student enrolment evidence must contain the configured `MatriculaId`, `TipoEnsino`, and `AnoLectivo`.

Read [docs/spec.md](docs/spec.md), [docs/architecture.md](docs/architecture.md), and [docs/setup.md](docs/setup.md) before enabling apply. Scripts, secret setup, notifications, live preview approval, and daily Hermes deployment are deliberately outside this application's scope.
