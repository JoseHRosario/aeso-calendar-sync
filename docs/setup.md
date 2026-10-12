# Secure setup and deployment gates

1. Install .NET 10 SDK for build/test or .NET 10 runtime for a published framework-dependent app. Linux requires timezone data containing `Europe/Lisbon`. Here the SDK is `$HOME/.dotnet`; prepend it to PATH and export DOTNET_ROOT.
2. Create a Google service account outside this repository, enable Calendar API, and share the **existing** target calendar with edit rights. Store service-account JSON at an external absolute path under a private directory. Never copy it into source or a committed appsettings file. Restrict files to owner read/write and directories to owner access.
3. A local masked launcher setup should collect school credentials outside chat and write them into an external owner-only store. It injects `AESO_SCHOOL_USERNAME`/`AESO_SCHOOL_PASSWORD` into the child environment. The app deliberately does not implement vault-file parsing, password prompts, or secret-store decryption.
4. Write an external runtime configuration using the exact documented properties in `appsettings.example.json`. Preserve the exact target Calendar ID. Do not confuse backend student `8937` with process `7945`. Current enrolment is `43850`, TipoEnsino `1`, school-year start `2026`.
5. Build and run the hermetic suite; publish into a deployment directory outside tracked source. Neither build/test nor publication creates a schedule or performs school/calendar writes.
6. Validate an actual downloaded Events-list JSON offline if available. A source-only report is not a Google dry-run and does not approve apply.
7. Run **live dry-run** with securely injected credentials. Confirm selected-student auth succeeds, `Matriculas` contains the configured enrolment/year, complete source coverage is established, and Google listing works. Review exact names, separate time slots, today/past boundaries, summer/winter offsets, calendar-default reminders, and prefix-only proposed deletions.
8. Obtain explicit approval of that live Google preview before any apply or schedule activation. Manually created future `[AESO]` entries are eligible for deletion: move them elsewhere or remove the prefix if they should be protected.
9. Parent deployment may arrange a daily Hermes script-only run at 06:00 **Europe/Lisbon**, with explicit timezone/DST support, a single-run lock and notifications for changes/failures/recovery. Successful unchanged results should remain silent. The scheduler should stay paused until the live preview is approved. Scheduling, scripts and notifications are owned by the parent deployment, not this application.
10. At rollover, default config stops at 2027-09-01. Inspect the new selected enrolment and update the approved config; do not extend the date horizon while retaining stale enrolment IDs.

## Remediation

- **CAPTCHA / school 401**: stop unattended attempts. Complete normal manual authentication and inspect the vendor flow; do not invent a bypass or submit credentials repeatedly.
- **Empty, capped, malformed or unstable source**: all reconciliation is blocked. Inspect current API response privately and re-establish coverage semantics. Never force deletion from an unexplained empty list.
- **Google race / ETag conflict**: rerun the live preview. A prefix removed after planning must protect the entry, not be restored blindly.
- **Partial apply**: earlier verified creates/patches may exist. Rerun dry-run/apply; deterministic IDs and conflict readbacks prevent duplicate retry insertions. Never report a failed run as success.
- **Google cancelled-ID collision**: deterministic collision handling is built in and bounded; failure is explicit if all candidates are occupied or responses cannot prove identity.
- **Unauthenticated service account**: inspect external credentials, API enablement and calendar sharing privately. Error output intentionally omits credential/remote response details.

No real credentials, calendar mutations, schedule creation, commits, or pushes are part of the implementation verification.
