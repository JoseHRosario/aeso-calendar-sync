# Secure setup and deployment gates

1. Install .NET 10 SDK for build/test or .NET 10 runtime for a published framework-dependent app. Linux requires timezone data containing `Europe/Lisbon`. Here the SDK is `$HOME/.dotnet`; prepend it to PATH and export DOTNET_ROOT.
2. Create a Google service account outside this repository, enable Calendar API, and share the **existing** target calendar with edit rights. Provision its entire JSON object directly into SecureString `/aeso-calendar-sync/prod/google-service-account`, account `497087877832`, region `eu-west-1`, encryption key `alias/aws/ssm`. Do not retain a runtime application private-key file, or put secret values into chat, command arguments or configuration.
3. Provision SecureString `/aeso-calendar-sync/prod/school-credentials` in the same region/account/key, JSON object with required string `Username` and `Password` (case-insensitive). Owner secret provisioning is outside the application. Remove the legacy school environment injection and Google credentials-path configuration; the application has no file/environment secret fallback. Establish dedicated native AWS SDK profile `aeso-calendar-sync` separately, bound to the approved workload role. Host IAM Roles Anywhere certificate/key bootstrap is an approved infrastructure exception, not an application secret store. The application never writes `~/.aws`, logs in or manages credential caches. Missing profile fails closed without using ambient personal/Minerva credentials. Role selection and least-privilege IAM for these exact two parameters remain deployment responsibilities; the application validates response ARNs, not caller identity through STS.
4. Write an external runtime configuration using the exact documented properties in `appsettings.example.json`. Preserve the exact target Calendar ID. Do not confuse backend student `8937` with process `7945`. Current enrolment is `43850`, TipoEnsino `1`, school-year start `2026`.
5. Build and run the hermetic suite; publish into a deployment directory outside tracked source. Neither build/test nor publication creates a schedule or performs school/calendar writes.
6. Validate an actual downloaded Events-list JSON offline if available. A source-only report is not a Google dry-run and does not approve apply.
7. Run **live dry-run** with the dedicated workload profile and provisioned parameters. Confirm selected-student auth succeeds, `Matriculas` contains the configured enrolment/year, complete source coverage is established, and Google listing works. Review exact names, separate time slots, today/past boundaries, summer/winter offsets, calendar-default reminders, and prefix-only proposed deletions.
8. Obtain explicit approval of that live Google preview before any apply or schedule activation. Manually created future `[AESO]` entries are eligible for deletion: move them elsewhere or remove the prefix if they should be protected.
9. Parent deployment may arrange a daily Hermes script-only run at 06:00 **Europe/Lisbon**, with explicit timezone/DST support, a single-run lock and notifications for changes/failures/recovery. Successful unchanged results should remain silent. The scheduler should stay paused until the live preview is approved. Scheduling, scripts and notifications are owned by the parent deployment, not this application.
10. At rollover, default config stops at 2027-09-01. Inspect the new selected enrolment and update the approved config; do not extend the date horizon while retaining stale enrolment IDs.

## Application startup contract

`appsettings.example.json` contains only nonsecrets: `AwsRegion=eu-west-1`, `AwsProfile=aeso-calendar-sync`, `AwsAccountId=497087877832`, `SchoolCredentialsParameter=/aeso-calendar-sync/prod/school-credentials`, `GoogleServiceAccountParameter=/aeso-calendar-sync/prod/google-service-account`. These are pinned approved values; school IDs/calendar rules remain unchanged. No additional secret CLI options exist. Default live mode remains read-only; only `--apply` enables writes.

SSM is fetched once into memory with decryption. Every parameter and both payloads validate before school or Google requests; startup failure emits sanitized stderr and no success JSON. There is no stale-cache recovery. Offline `--validate-source`/`--source-file` are AWS-free and cannot apply. Use the next process to pick up secret rotation. The application does not verify the configured KMS alias from a GetParameters response; provisioning enforces `alias/aws/ssm`.

## Remediation

- **AWS profile / SSM failure**: inspect workload bootstrap, role permissions, region/account, exact parameter names/type and JSON privately. Do not replace the profile with a personal identity or restore plaintext fallback. No school/calendar requests occur before a valid complete snapshot.

- **CAPTCHA / school 401**: stop unattended attempts. Complete normal manual authentication and inspect the vendor flow; do not invent a bypass or submit credentials repeatedly.
- **Empty, capped, malformed or unstable source**: all reconciliation is blocked. Inspect current API response privately and re-establish coverage semantics. Never force deletion from an unexplained empty list.
- **Google race / ETag conflict**: rerun the live preview. A prefix removed after planning must protect the entry, not be restored blindly.
- **Partial apply**: earlier verified creates/patches may exist. Rerun dry-run/apply; deterministic IDs and conflict readbacks prevent duplicate retry insertions. Never report a failed run as success.
- **Google cancelled-ID collision**: deterministic collision handling is built in and bounded; failure is explicit if all candidates are occupied or responses cannot prove identity.
- **Unauthenticated service account**: inspect the SSM service-account value, API enablement and calendar sharing privately. Error output intentionally omits credential/remote response details.

No real credentials, calendar mutations, schedule creation, commits, or pushes are part of the implementation verification.
