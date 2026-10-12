# Dedicated host identity: IAM Roles Anywhere

## Approval and current facts

Use temporary AWS credentials for the Hermes host. The only host-held secret permitted by this design is the workload certificate's private key used to bootstrap AWS authentication; school/Google secrets stay in SSM.

Read-only discovery found a physical Debian amd64 desktop, openssl installed, no signing helper on PATH, and no local AWS profile named `aeso-calendar-sync`. TPM devices exist but are inaccessible to the runtime user; do not assume hardware key storage is configured. AWS-side resources/permissions have not been checked. No resources below are asserted to exist.

## Proposed resources

- IAM role: `AesoCalendarSync`.
- Roles Anywhere profile and local AWS SDK profile: `aeso-calendar-sync`.
- Dedicated external-CA trust anchor in account497087877832, eu-west-1.
- Certificate subject CN: `aeso-calendar-sync-azw`; a dedicated CA issuer identity.

Create/manage the CA off this host and retain its encrypted root key offline. Generate the leaf key and CSR on this host; take only the CSR to the CA and return the signed certificate. Leaf requires CA:false and digital-signature usage; CA requires CA:true and certificate-signing usage. Keep host key outside Git and ~/.aws in an owner-only directory0700 and file0600. Same-user processes/root remain able to access file-based private keys; hardware-backed storage is separate work.

Install the official Linux x86-64 `aws_signing_helper`, verifying the official checksum and Debian compatibility. Use `credential-process`, not `update` (which writes credentials to disk). Never run the helper directly and expose its credential JSON.

## Permissions and trust acceptance criteria

Trust must authorize the Roles Anywhere service principal for sts:AssumeRole, sts:TagSession and sts:SetSourceIdentity, scoped to the exact trust-anchor ARN via aws:SourceArn, account497087877832 via aws:SourceAccount, and the approved subject/issuer certificate attributes. The trust-anchor ARN is not the Roles Anywhere profile ARN. Preserve attribute mappings used in trust conditions. The Roles Anywhere profile should authorize only the runtime role; avoid unnecessary custom role-session names.

Runtime source uses only `ssm:GetParameters` with decryption and exactly these resource ARNs:

- `arn:aws:ssm:eu-west-1:497087877832:parameter/aeso-calendar-sync/prod/school-credentials`
- `arn:aws:ssm:eu-west-1:497087877832:parameter/aeso-calendar-sync/prod/google-service-account`

Do not grant path enumeration/history, writes, IAM administration or Roles Anywhere administration. Retain alias/aws/ssm; do not invent a customer-managed key ID or grant broad KMS permissions. Final policy/trust and operator permissions must be reviewed against official service authorization before provisioning; this document is not a ready-deployed policy. The bootstrap operator's permissions are distinct from runtime permissions and are not yet verified.

## Manual AWS profile configuration

José, not the agent, adds the following only after the helper, files and AWS resources exist. Replace every placeholder; credential_process is one line:

```ini
[profile aeso-calendar-sync]
region = eu-west-1
credential_process = /ABSOLUTE/PATH/aws_signing_helper credential-process --certificate /ABSOLUTE/PRIVATE/DIR/host.crt --private-key /ABSOLUTE/PRIVATE/DIR/host.key --region eu-west-1 --trust-anchor-arn arn:aws:rolesanywhere:eu-west-1:497087877832:trust-anchor/REPLACE_TRUST_ANCHOR_ID --profile-arn arn:aws:rolesanywhere:eu-west-1:497087877832:profile/REPLACE_PROFILE_ID --role-arn arn:aws:iam::497087877832:role/AesoCalendarSync
```

No source_profile, interactive login session or static access keys belong here. The app resolves this explicit native SDK profile with no ambient fallback. Verify the exact dedicated assumed-role identity before granting activation; profile name alone does not prove caller identity. Never edit/create/delete ~/.aws through agent tools.

## Rotation, recovery and activation

The SDK renews temporary sessions through credential_process; it does not renew the X.509 certificate. Establish expiry monitoring and renew the leaf via the offline CA before expiry, safely replace matching cert/key, and preserve approved subject/issuer attributes. Same-CA renewal generally retains trust; replacing CA requires an explicit trust migration. Imported CRLs provide revocation; do not assume remote CDP/OCSP URLs are fetched or that disabling a profile instantly invalidates previously issued sessions.

After identity is configured, the owner provisions the two SecureString values directly through an approved AWS flow (no secrets in chat/argv). Validate live SSM retrieval, school context/coverage and Google calendar permissions with `python3 scripts/run_sync.py --preview`. Only after explicit preview approval and verified apply may cron be enabled. Do not relax IAM permissions or fall back to the operator identity to make a failing run pass.

## Official references

- https://docs.aws.amazon.com/rolesanywhere/latest/userguide/getting-started.html
- https://docs.aws.amazon.com/rolesanywhere/latest/userguide/trust-model.html
- https://docs.aws.amazon.com/rolesanywhere/latest/userguide/credential-helper.html
- https://docs.aws.amazon.com/rolesanywhere/latest/userguide/authentication-create-session.html
- https://docs.aws.amazon.com/systems-manager/latest/APIReference/API_GetParameters.html
- https://docs.aws.amazon.com/systems-manager/latest/userguide/secure-string-parameter-kms-encryption.html
