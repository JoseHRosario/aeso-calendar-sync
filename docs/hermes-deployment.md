# Hermes deployment

This project has no AWS resources. Runtime is the **meireles** Hermes profile.

## Installed paths

- Source: `/d/srv/projects/aeso-calendar-sync`.
- Hermes entry: `/home/xungaman/.hermes/profiles/meireles/scripts/run-aeso-calendar-sync.py` delegates to the source-controlled `scripts/run_sync.py`.
- Private runtime: `/home/xungaman/.hermes/profiles/meireles/aeso-calendar-sync`.
- Published assembly: `app/Aeso.CalendarSync.dll` under the runtime directory.
- Nonsecret settings: `config.json` under the runtime directory.
- Google key expected at `google-service-account.json` under the runtime directory, or update `GoogleCredentialsPath` to the actual external absolute path.
- School credentials: `school.env`, JSON-quoted values for `AESO_SCHOOL_USERNAME` and `AESO_SCHOOL_PASSWORD`, mode 0600, never committed or printed.
- Reconciliation result and recovery state: `last-result.json`, `status.json`; both owner-only.
- Apply permission: `apply-enabled` file. Absent by default. Never create before the live preview is explicitly approved.

Credentials should be entered by the owner in their own interactive terminal:

```bash
python3 /d/srv/projects/aeso-calendar-sync/scripts/setup_school.py
```

Do not put passwords or private keys in chat, process arguments or source. The browser vault is not decrypted or repurposed by this program. The external school file is owner-only plaintext; protect the host and rotate/remove the file if compromised.

## Verification and publication

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
cd /d/srv/projects/aeso-calendar-sync
dotnet test Aeso.CalendarSync.slnx --configuration Release
python3 scripts/test_runner.py
dotnet publish src/Aeso.CalendarSync --configuration Release --output /home/xungaman/.hermes/profiles/meireles/aeso-calendar-sync/app
python3 scripts/run_sync.py --preview
```

The last command requires both credential setups and performs live school and Google reads, never calendar writes. Offline `--validate-source` only proves parsing of downloaded data; it is not calendar verification.

## Scheduler

Job `4dda4ecf1a1c`, name `aeso-calendar-sync`, was created **paused**:

- `0 6 * * *`, profile timezone explicitly `Europe/Lisbon`.
- Script-only `no-agent`; no LLM calls.
- Change/recovery stdout goes to Discord `1479872936230781052` (`#meireles`). Empty stdout is silent.
- Failure delivery goes to the same channel; launcher stderr is sanitized.
- Launcher timeout 180 seconds; exclusive file lock prevents overlapping local runs.

Use Hermes CLI/tools, never hand-edit cron storage. Do not unpause until credential setup, live preview, explicit approval and first verified apply have succeeded. A running gateway/ticker and host are required; an unavailable host cannot notify Discord. Calendar writes are not transactional: an interrupted Google apply can leave a partial plan, which the next run reconciles using deterministic IDs.

## Current deployment gates

School browser login and downloaded-list parsing have been verified. Default unit tests do not use credentials or network. Programmatic school login, Google permissions/live preview, calendar writes and Discord delivery are not verified until credentials are supplied and the paused job is exercised. The school-year configuration fails closed at rollover.
