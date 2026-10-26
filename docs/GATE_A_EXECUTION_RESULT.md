# Gate A - Final execution result

Completed and independently verified: 2026-10-26 22:10:25 +07.
Target: PostgreSQL aicos_db.public.
**Gate A COMPLETED / PASS. Gate C NOT APPROVED / NOT EXECUTED.**

This completes the owner's conditional approval: eleven existing service roles,
reviewed direct privileges, hidden all-role credential reset, LOGIN only after
all password/permission checks, and actual authentication/owned-table reads.
Neither excluded PUBLIC REVOKE ran. No other role permissions were changed.

## Final per-role results

| Role | State | New credential assigned | Actual login and approved reads |
|---|---|---|---|
| identity_svc | LOGIN | PASS | PASS |
| menu_svc | LOGIN | PASS | PASS |
| table_reservation_svc | LOGIN | PASS | PASS |
| ordering_svc | LOGIN | PASS | PASS |
| payment_svc | LOGIN | PASS | PASS |
| inventory_svc | LOGIN | PASS | PASS (connection only; no business reads granted) |
| kitchen_svc | LOGIN | PASS | PASS |
| workforce_svc | LOGIN | PASS | PASS (connection only; no business reads granted) |
| reporting_svc | LOGIN | PASS | PASS (connection only; no business reads granted) |
| customer_svc | LOGIN | PASS | PASS (connection only; no business reads granted) |
| ai_svc | LOGIN | PASS | PASS (connection only; no business reads granted) |

Before this reset, all eleven roles existed as NOLOGIN and the NOLOGIN privilege
matrix had zero issues. No role was recreated and no GRANT/REVOKE was rerun.

## Hidden-input reset and activation order

The new reset attempt used the unchanged reviewed helper:
Complete-ServiceRoleCredentials.ps1, SHA-256
1EA90A6F24078A3D649135436A4AF8F9FE62C905271BA123659D2925F0E57B13.

- All eleven secrets were assigned in the NEW attempt, including replacement of
  the three forgotten credentials. Password entry used masked native psql
  prompts and the administrator prompt used Read-Host -AsSecureString.
- Roles stayed NOLOGIN during assignment. A monitored checkpoint recorded
  9/11 assignments and LoginEnabled=false.
- The helper checked every approved role for a non-null SCRAM credential and
  required the complete NOLOGIN permission matrix to pass before activating
  LOGIN for the eleven names in one transaction.
- After activation, it required the LOGIN permission matrix to pass, then
  prompted for each actual service password and performed a real connection
  plus approved SELECT LIMIT 0 reads. It did not use admin SET ROLE as a
  substitute for authentication.
- Both credential-progress.json and credential-result.json report Completed,
  eleven PasswordsAssigned, LoginEnabled=true and eleven ConnectionsVerified.
- Independent inspection then verified all eleven LOGIN flags, SCRAM-presence
  booleans and effective permissions.

No plaintext role password or verifier was displayed, written to source/Git/
logs, passed in process arguments or stored in a plaintext credential file by
this workflow. Only role names, booleans and progress are in the evidence.
Secrets were supplied by the user locally; deployment secret injection remains
a later explicitly controlled action, not a source-code configuration change.

## Independent verification

| Check | Result |
|---|---|
| Eleven approved LOGIN roles | PASS |
| Non-null SCRAM credential for each | PASS (presence boolean only) |
| Full effective table/column/schema/database/sequence matrix | SUMMARY issue_count=0 |
| Actual role authentication/owned reads | 11/11 PASS, recorded by the interactive helper |
| Unexpected column grant options | 0 |
| Superuser / CREATEDB / CREATEROLE / REPLICATION / BYPASSRLS | false for all eleven |
| Membership/ownership beyond scope | None; covered by full matrix |
| PUBLIC and non-target ACL digest | Unchanged: 79f61aad9e4d7c8c173987c640992446 |
| Non-target role metadata digest | Unchanged: b2fa7889cb49831d199fa3fd57264a21 |
| Business table schema inventory | 38 tables / 244 columns, unchanged |
| Original business data | All 38 per-table fingerprints match pre-Gate-A backup |
| Other aicos_db backends at final check | 0 |
| Role recreation or grant reapplication during reset | Not performed |
| Gate B migration/rollback or Gate C | Not performed |

PUBLIC still supplies database CONNECT/TEMPORARY and schema USAGE. Effective
TEMPORARY is retained inherited baseline under the owner's conditional approval;
it is not an extra direct role grant or an unreported PUBLIC ACL change.
Permanent schema CREATE and unapproved table/column privileges remain false.

## Evidence

Reset evidence directory:
C:\Users\ASUS\AppData\Local\AICOS\deployments\gate_a_password_reset_20261026_220544

- credential-progress.json;
- credential-result.json;
- verify-login-final.log;
- final-readonly-state.log.

Earlier role creation/scoped grant evidence:
C:\Users\ASUS\AppData\Local\AICOS\deployments\gate_a_20261026_213339

The pre-Gate-A post-Gate-B backup remains:
C:\Users\ASUS\AppData\Local\AICOS\backups\gate_a_20261026_213339

The final verifier returned issue_count=0 and exit code 0. No credential/reset
error was recorded. A process-presence probe returned no process after the
console completed; an absent completed console is not a failed credential test.

## Stop after Gate A

Gate A is complete; no Backend service, Gateway, Docker workload, API login or
Payment mutation was started by this reset. Services were not configured with
plaintext secrets in Git. Gate C requires its own explicit approval, selected
startup/smoke scope and a protected way to supply the new runtime credentials.

No Mobile edit, commit or push occurred. Do not rerun the password helper unless
a new credential rotation is requested; that would replace the working secrets.
