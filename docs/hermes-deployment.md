# Hermes deployment

The process runs on the **meireles** Hermes host, not Lambda. Application secrets are stored in AWS SSM Parameter Store; AWS IAM Roles Anywhere supplies the host's temporary workload credentials. No shared NuGet or Guito migration is part of this change.

## Secret storage

Account `497087877832`, region `eu-west-1`, encryption with AWS-managed key `alias/aws/ssm`:

- `/aeso-calendar-sync/prod/school-credentials`: SecureString JSON containing `Username` and `Password`.
- `/aeso-calendar-sync/prod/google-service-account`: SecureString containing the complete Google service-account JSON.

The owner provisions values outside chat through an approved AWS workflow. Do not paste secrets into chat, put them in process arguments or source, create school.env/Google private-key files, or decrypt the browser vault for reuse. SSM values are fetched once per process, validated completely, and used in memory only. No persistent secret cache or local/env fallback is allowed. Offline source validation does not require AWS access.

The runtime AWS identity must obtain temporary credentials through a dedicated Roles Anywhere profile, proposed name `aeso-calendar-sync`, restricted to reading the two parameters. Roles Anywhere requires a host certificate and access to its private key: this workload-identity bootstrap key is the explicitly approved exception to SSM-only application-secret storage. Protect it outside the repository, separate from an offline CA root; renew certificates before expiry. Automatic temporary-credential renewal does not renew the certificate itself.

The application must select the dedicated profile explicitly, not silently inherit the operator's `minerva-agent` profile or fall back to an IAM user. Never edit/create/delete under `~/.aws`: required profile configuration is José's manual step. IAM Roles Anywhere resources, profile settings, trust and permissions must be provisioned and verified before activation.

## Installed paths

- Source: `/d/srv/projects/aeso-calendar-sync`.
- Hermes entry: `/home/xungaman/.hermes/profiles/meireles/scripts/run-aeso-calendar-sync.py` delegates to source-controlled `scripts/run_sync.py`.
- Private runtime: `/home/xungaman/.hermes/profiles/meireles/aeso-calendar-sync`.
- Published assembly: `app/Aeso.CalendarSync.dll` under the runtime directory.
- Nonsecret settings: `config.json` under the runtime directory; holds calendar/source identifiers, AWS profile/account/region and SSM parameter references, not values.
- Reconciliation result and recovery state: `last-result.json`, `status.json`; owner-only. `last-failure.json` retains allowlisted sanitized diagnostics and exit status; unknown stderr/provider content is discarded. Reports use exclusive temporary files explicitly set to0600 before atomic replacement.
- Apply permission: `apply-enabled` file. Absent by default; do not create before the live preview is explicitly approved.

## Verification and publication

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
cd /d/srv/projects/aeso-calendar-sync
dotnet test Aeso.CalendarSync.slnx --configuration Release
python3 scripts/test_runner.py
dotnet publish src/Aeso.CalendarSync --configuration Release --output /home/xungaman/.hermes/profiles/meireles/aeso-calendar-sync/app
python3 scripts/run_sync.py --preview
```

The final command requires the dedicated workload identity, provisioned SSM secrets, Calendar API enablement and Google calendar sharing with the service account. It performs live AWS/school/Google reads, never calendar writes. Offline `--validate-source` proves only downloaded-source parsing, not Google reconciliation or SSM access.

## Scheduler

Job `4dda4ecf1a1c`, name `aeso-calendar-sync`, remains **paused** pending live verification:

- `0 6 * * *`, profile timezone explicitly `Europe/Lisbon`.
- Script-only `no-agent`; no LLM calls.
- Change/recovery stdout goes to Discord `1479872936230781052` (`#meireles`). Empty stdout is silent.
- Failure delivery goes to the same channel; launcher stderr is sanitized.
- Launcher timeout660 seconds, allowing the application's ten-minute cancellation deadline to expire before watchdog termination; exclusive lock prevents overlapping local runs.

Use Hermes CLI/tools, never hand-edit cron storage. Do not unpause until workload identity and secrets are verified, a live preview is approved, and first apply is verified through exact Google readbacks. A running gateway/ticker and host are required; an unavailable host cannot notify Discord. Calendar writes are not transactional: an interrupted Google apply can leave a partial plan; deterministic IDs reconcile it on retry.

## Deployment gates

School browser login and downloaded-list parsing have been verified. Unit tests use no network or real credentials. Roles Anywhere setup, SSM access, unattended school login, Google permissions/live preview, calendar writes and Discord delivery must be exercised separately. The configured school year fails closed at rollover.
