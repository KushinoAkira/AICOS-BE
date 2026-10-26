# Gate A - Conditional approval execution

Date: 2026-10-26. Target: aicos_db.public.
Owner approved Gate A conditionally; Gate C and Gate B reapplication remain prohibited.

## Scope adjustment reported before execution

The following SQL was removed, not merely disabled by an option:
- REVOKE CREATE ON SCHEMA public FROM PUBLIC;
- REVOKE TEMPORARY ON DATABASE aicos_db FROM PUBLIC;

The remaining REVOKE/GRANT targets are exactly the eleven reviewed service roles.
The column ACL loop is restricted to the same eleven names. No PUBLIC, other-role
ACL, ownership, membership or default privilege change is in the execution set.

PUBLIC retains CONNECT/TEMPORARY on aicos_db and USAGE on public. Effective
TEMPORARY=true inherited from PUBLIC is an approved retained baseline, not an
extra grant. Permanent schema CREATE must remain false. The verifier now checks
this conditional baseline rather than expecting PUBLIC TEMPORARY to be removed.

## Reviewed hashes before mutation

| File | SHA-256 |
|---|---|
| create-service-roles.sql | 01F5846EAF8DFF4612F62923CB06A6D26BE53F2F9602FE26843953F7ACE837A4 |
| public-service-grants.sql | 25051F06DADF71C065C8DCBB49D1FD874B043047A1B595C5B6664EC691BF4404 |
| verify-service-permissions.sql | 89A050D0D14E3B2A76D5BF20BC83AD0632D6A91B6D7256F3615BDFA982D6BEE4 |
| Complete-ServiceRoleCredentials.ps1 | 1EA90A6F24078A3D649135436A4AF8F9FE62C905271BA123659D2925F0E57B13 |

These hashes supersede the earlier Gate A draft that proposed PUBLIC revokes.
Payment migration files are unchanged.

## Pre-execution evidence

Post-Gate-B backup:
`C:\Users\ASUS\AppData\Local\AICOS\backups\gate_a_20261026_213339`.

Evidence directory:
`C:\Users\ASUS\AppData\Local\AICOS\deployments\gate_a_20261026_213339`.

Read-only baseline: 38 tables, 244 columns, zero service roles, zero other backends.
All nine Gate B verification counts remain zero. A query formatting error in
the initial read-only ACL snapshot was corrected before any privilege mutation.

Non-target ACL digest (includes PUBLIC, excludes only eleven approved grantees):
`79f61aad9e4d7c8c173987c640992446`.

Non-target role metadata digest:
`b2fa7889cb49831d199fa3fd57264a21`.

The digests will be compared after applying roles/grants. Data fingerprints are
captured by the post-Gate-B backup and will also be rechecked.

## Execution sequence

1. Apply unchanged create-service-roles.sql: new roles must all be NOLOGIN.
2. Apply conditional public-service-grants.sql: only reviewed role ACLs.
3. Require verify-service-permissions.sql with expect_login=false to report zero.
4. Compare non-target ACL/role digests, PUBLIC privileges and business data.
5. Supply individual secrets through a local console with masked Read-Host and
   psql \password prompts. No plaintext password file, command argument, source
   literal, transcript or password output is produced.
6. Only after all eleven secrets are assigned and ACL checks pass, activate LOGIN.
7. Prompt each actual role's password and verify actual login + owned SELECT
   LIMIT 0, then require the full effective matrix with expect_login=true.
8. Report completion, or clearly report pending hidden input. Never claim a
   password/login test passed before the user enters its secret.

The console helper stores only role names and success/progress booleans under
the evidence directory. It starts no Backend service. If input is unavailable
or cancelled, stop safely and report actual NOLOGIN/LOGIN progress; no random
secret is silently substituted.

## Current execution status

Final update: Gate A COMPLETED/PASS at 2026-10-26 22:10:25 +07, after the owner
approved a new all-role reset. All eleven roles are LOGIN with new credentials
and real connection probes passed. Full privileges have zero issues; PUBLIC,
non-target roles and data are unchanged. See GATE_A_EXECUTION_RESULT.md. The
remaining pending-input notes below are historical checkpoints.

Latest password status recheck: all eleven roles remain NOLOGIN, three have
assigned but forgotten secrets, and the old console is closed. No reset has
started. See GATE_A_PASSWORD_RESET_PLAN.md for the prepared all-eleven reset
workflow; older waiting-console observations below are historical snapshots.

Update: the owner then approved the new masked reset attempt. It was opened
after eleven NOLOGIN/zero-permission-issue prechecks, with separate evidence at
gate_a_password_reset_20261026_220544. Completion/LOGIN/connection results are
pending the local input workflow; see GATE_A_PASSWORD_RESET_PLAN.md.

Creation and scoped ACLs applied successfully. Credential/LOGIN completion is
waiting for hidden local input; Gate A must not yet be reported fully complete.

| Step | Actual result |
|---|---|
| Post-Gate-B backup / read-only baseline | PASS |
| Revised scripts/checksums reported before execution | PASS |
| CREATE ROLE | Exit 0; eleven NOLOGIN roles, all elevated flags false |
| Conditional GRANT/REVOKE | Exit 0; only named-role ACL targets |
| Full effective matrix, expect_login=false | SUMMARY issue_count=0 |
| PUBLIC/non-target ACL digest | Unchanged: 79f61aad9e4d7c8c173987c640992446 |
| Non-target role metadata digest | Unchanged: b2fa7889cb49831d199fa3fd57264a21 |
| All 38 table data fingerprints | Unchanged |
| Password/LOGIN / actual role connection | Pending hidden input, not claimed passing |

PUBLIC still has database CONNECT/TEMPORARY and schema USAGE, exactly as before.
No role has been granted permanent-schema CREATE, superuser, ownership,
membership, DELETE, TRUNCATE, REFERENCES, TRIGGER, MAINTAIN or sequence rights.

An interactive console was opened with Complete-ServiceRoleCredentials.ps1.
Its first prompt is the existing PostgreSQL administrator password. Then psql
asks twice for each unique service secret; actual role-login verification asks
for each service secret again. Passwords are not shown, saved to plaintext files
or passed in process arguments. Backend services are not started.

Only status/timestamps/role names are recorded in credential-progress.json and
credential-result.json under the evidence directory. Until that console
completes, retain the NOLOGIN state and report this step as pending.

Logs: create-roles.log, grants.log, verify-nologin.log, before-scope.txt,
after-grants-scope.txt and gate_b_readonly_verify.txt. The protected backup and
roles-before.sql preserve pre-change state (the latter excludes passwords).

If the console is not visible, run the following locally in an interactive
PowerShell window; do not start a second instance while the first is open:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\ki9\sep490\Sep490\AICOS-BE\infra\postgres\security\Complete-ServiceRoleCredentials.ps1 -EvidenceDirectory C:\Users\ASUS\AppData\Local\AICOS\deployments\gate_a_20261026_213339
```

No Gate C, Backend startup, Gate B migration/rollback, Mobile edit, commit or
push was performed.

## Recheck after the credential console was closed

On 2026-10-26 at 21:56:50 +07, READ ONLY inspection confirmed all eleven roles
exist, all are NOLOGIN, and none has a password assigned (only boolean presence
was queried; no password/verifier was displayed). The effective permission
matrix with expect_login=false still reports SUMMARY issue_count=0.

The saved progress was AwaitingHiddenInput, with empty PasswordsAssigned and
ConnectionsVerified lists and LoginEnabled=false. No result file or surviving
credential-helper process existed. Thus no role password/LOGIN step had
completed. The existing, checksum-verified masked-input script was reopened
without modification, role recreation, GRANT/REVOKE reapplication, migration
or Backend startup. Gate A remains incomplete pending local hidden input.
