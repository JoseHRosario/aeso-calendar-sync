# Architecture and API evidence

## Modules

- `src/Aeso.CalendarSync/Program.cs`: minimal entry point; `Command.cs`: CLI/config validation, offline branch and live startup barrier, secure composition and sanitized process contract.
- `AwsSsmClientFactory.cs`: explicit native named-profile resolution and region, bounded SDK retries, client ownership at composition. No ambient credential fallback or AWS config mutation.
- `SsmSecretProvider.cs`: injected AWS SDK GetParameters, complete response validation and one memory-only snapshot task. `SecretPayloadParser.cs`, `SchoolCredentials.cs`, `SecretSnapshot.cs`: strict JSON/type/endpoint validation and typed in-memory secrets/Google credential.
- `Domain.cs`: source interpretation, Lisbon times, immutable calendar projection, canonical multiset identity and deterministic plan. No HTTP or filesystem dependencies.
- `SchoolClient.cs`, `ISchoolSource.cs`, `Transport.cs`, `SyncConfig.cs`: fixed-origin school HTTP adapter, bounded retry helper and nonsecret configuration. Acquires parent token, selects the backend student on the server, validates enrolment evidence, and establishes complete/stable source coverage.
- `Google.cs`: paginated Calendar HTTP adapter, exact GET readbacks, ETag race protection and apply runner. `Google.Apis.Auth` is used only to obtain service-account access tokens; Calendar requests use injected `HttpClient`.
- `tst/Aeso.CalendarSync.Tests`: public parser/planner, HTTP transport, runner and CLI seams. Fixed clock and fake transports; no network, live accounts or test secret prerequisites.

The runner receives `ISchoolSource`, `ICalendar`, config and `TimeProvider`. Its source read is an all-or-fail barrier before Google listing or writing. No state database, ownership marker, token cache, random insert IDs, external vault decryption, frontend or cloud deployment is required.

## School login and selected student context

The public vendor JavaScript is available at:

`https://aeso.inovarmais.com/consulta/app/all.min.js`

Read-only inspection of the deployed public bundle established:

1. `authFactory.isUserBurned(sessionId)` posts `{UserEmail:sessionId}` to `/consulta/api/login/isUserBurned`. Preserve the capitalized property name. A true `BurnedUser` stops the application; there is no CAPTCHA bypass.
2. `authFactory.validCredentials` sets Basic authorization using UTF-8 base64 of `username:password`; posts UTF-8 base64 `username`, `password`, and `sessionId` to `/consulta/api/loginFU/`.
3. Request interceptor adds `X-FESTMANI` and sets authorization to **`Bearer` + token with no intervening space**. The public daily client header uses base64 HMAC-SHA256 with public client key `0c24b08e-d78b-4d53-96a6-68db2bf2611f`, UTC `yyyyMMdd` input. This public key is protocol compatibility, not a secret user credential.
4. `sPickerController.studentClick` calls `studentPickerFactory.setAluno(id)`; that factory GETs `/consulta/api/studentPicked/{id}`. It updates `authFactory.setUser` and identification/enrolment data. It does **not** invoke `setCredentials` or install a replacement token. The selection GET establishes student context on the server while preserving login token/cookies.
5. Selected response `Matriculas` must authorize configured enrolment, teaching type, and year. The application intentionally does not mandate the separately observed 401 endpoint `/aluno/matriculas/{id}`.

Backend student ID `8937` was provided from read-only live browser inspection; process number `7945` must never be substituted for it. Runtime selected-student authentication and service-account calendar access still require real secure preview validation; no hermetic fake proves real credentials work.

The source path is `/consulta/api/agendaProf/{SchoolEnrolmentId}/{SchoolTipoEnsino}/{limit}`. Live browser discovery reported 30 rows at limit 30, 44 at 45, and 44 at 60. Original downloaded list parsing was exercised separately from hermetic tests; that does not replace a live source+Google preview.

## Operational failure behavior

Unknown source or Google shape fails closed, as do empty source, unstable batches, pagination loops, year rollover and CAPTCHA. Sanitized errors identify operation/status without remote response contents. The launcher should retain only necessary private diagnostics, notify failures/recovery, and serialize runs with a lock. Overlapping independent reconciliations cannot form a transaction; deterministic IDs and ETags reduce, but do not eliminate, concurrent-run races.

API retries are three attempts for transient reads and idempotent Google writes, with 200/400 ms delays. Each production HTTP request has a 30-second timeout and live run has a ten-minute deadline. Cancellation is propagated. Tokens are only in process memory, not cached to disk. Credential construction uses `JsonDocument` validation followed by `CredentialFactory.FromJsonParameters<ServiceAccountCredential>` from allowlisted in-memory fields, with Calendar events scope. No credential file or environment secret reader remains. Offline tests cover native SDK process-profile construction without executing the credential process, injected SDK responses/errors, strict payload validation and the production CLI-to-school bootstrap with synthetic credentials; no real AWS, OAuth or calendar request is made.
