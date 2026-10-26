# Gate A - Password reset readiness and status

## Approved reset attempt completed

Final update: 2026-10-26 22:10:25 +07. The new attempt completed all eleven
password replacements, LOGIN activation after required checks, and eleven real
role connection/read probes. Independent effective privilege issue_count=0;
PUBLIC/non-target ACLs and business data are unchanged. See
GATE_A_EXECUTION_RESULT.md. The progress snapshots below are historical.

The owner subsequently explicitly approved opening the new all-eleven reset
console. Prechecks again confirmed eleven NOLOGIN roles and zero effective
permission issues, with no surviving old credential console.

New evidence directory:
`C:\Users\ASUS\AppData\Local\AICOS\deployments\gate_a_password_reset_20261026_220544`.

The unchanged reviewed helper was opened once in a local interactive console.
Its current progress is read only from sanitized status/count metadata. The
initial observed state was AssigningPasswords, zero new assignments,
LoginEnabled=false and zero actual connections verified. This new attempt will
replace all eleven secrets, including the three previously assigned values.

Gate A is not complete until the result is Completed, all eleven new password
steps and real connections are recorded, and the final READ ONLY privilege/
role checks pass. Old snapshots and proposed commands below are historical
preparation evidence, not a second console-launch instruction.

Checked: 2026-10-26 22:01:54 +07. Target: aicos_db.public.
Scope of this turn: READ ONLY status inspection and preparation.
**No password reset, LOGIN toggle, role recreation or GRANT/REVOKE executed.**

## Current confirmed state

| Role | State | Password assigned |
|---|---|---|
| identity_svc | NOLOGIN | Yes |
| menu_svc | NOLOGIN | Yes |
| table_reservation_svc | NOLOGIN | Yes |
| ordering_svc | NOLOGIN | No |
| payment_svc | NOLOGIN | No |
| inventory_svc | NOLOGIN | No |
| kitchen_svc | NOLOGIN | No |
| workforce_svc | NOLOGIN | No |
| reporting_svc | NOLOGIN | No |
| customer_svc | NOLOGIN | No |
| ai_svc | NOLOGIN | No |

Only password-presence booleans were selected. No password or stored verifier
was read into output or logged. Forgotten plaintext cannot be recovered from
the server; administrator-assisted replacement does not require the old role
password.

The full effective privilege matrix with expect_login=false reports
SUMMARY issue_count=0. Roles and grants therefore need no recreation/reapply.

Old progress file:
C:\Users\ASUS\AppData\Local\AICOS\deployments\gate_a_20261026_213339\credential-progress.json

It records AssigningPasswords, three successful assignments
(identity_svc/menu_svc/table_reservation_svc), LoginEnabled=false and zero
ConnectionsVerified. There is no credential-result.json. Live database state
agrees with that progress.

An initial unprivileged process query was denied; it was not accepted as proof
that the console was closed. The subsequent privileged READ ONLY process
inspection succeeded: no credential-helper process exists, and aicos_db has no
other client/backend. The old console is currently closed.

## Safe handling if an old console appears before the next attempt

Do not run two credential consoles concurrently. At an idle hidden password
prompt, press Ctrl+C to cancel, wait for the command/helper to exit, then close
the window. Do not submit an empty password as a cancellation mechanism.
Recheck both helper and child psql processes and database state before opening
a new console. Earlier successful password assignments are NOT rolled back
when a console closes.

If any approved role is LOGIN on that recheck, stop and report which roles
changed. Propose a separately reviewed transition of those exact roles to
NOLOGIN before resetting, rather than disabling accounts silently.

## Prepared all-eleven reset workflow (not started)

1. Recheck eleven roles present, all NOLOGIN, no old helper/child psql process,
   and effective matrix with expect_login=false has zero issues.
2. Preserve old progress/evidence. Use a NEW evidence directory so the reset
   attempt is not confused with the three earlier assignments.
3. Prepare eleven unique secrets in a local password manager. Enter them only
   into masked prompts; do not send them through chat, command arguments,
   source files, tickets, plaintext logs or environment dumps.
4. Use the unchanged Complete-ServiceRoleCredentials.ps1 helper. It contains no
   CREATE ROLE or GRANT/REVOKE step. Enter the existing administrator password
   at Read-Host -AsSecureString, then use each psql password prompt twice.
5. Re-enter ALL eleven secrets, including replacing the three forgotten values.
   Roles stay NOLOGIN throughout this loop. No old password is required.
6. Before enabling LOGIN, require:
   - eleven successful password-assignment steps in this NEW attempt;
   - all eleven roles have a non-null SCRAM credential, checked without
     outputting passwords/verifiers;
   - the complete approved privilege matrix still has zero issues;
   - no elevated attributes, ownership, membership or unexpected rights.
7. Only after these checks pass, the existing helper activates LOGIN for the
   exact eleven roles in one transaction. PostgreSQL cannot authenticate an
   external connection for a NOLOGIN role, so actual authentication is tested
   AFTER this activation, never falsely claimed as passing beforehand.
8. Verify each real role connection and its approved SELECT LIMIT 0 reads via
   masked password prompts. Require eleven successful real connections and the
   LOGIN-state permission matrix to pass before declaring Gate A complete.
9. Keep Backend/services stopped; no Gate C, migration, application data writes,
   account provisioning or API login is part of this credential reset.

The helper writes only progress/status/role names. It temporarily holds the
administrator credential in process memory for its approved SQL connections and
restores/clears that environment when it exits; no plaintext credential file is
created. Input values are not printed by this workflow.

If the reset is interrupted before all passwords are accepted, roles remain
NOLOGIN. If interrupted after LOGIN activation, report the actual role flags and
verification progress before proposing recovery; do not recreate roles or reset
all privileges as a shortcut.

## Proposed local command, after status report and confirmation

Do not execute this inventory yet. The next reset attempt will use a new
timestamped directory:

```powershell
$resetEvidence = Join-Path $env:LOCALAPPDATA ('AICOS/deployments/gate_a_password_reset_' + (Get-Date -Format yyyyMMdd_HHmmss))
New-Item -ItemType Directory -Path $resetEvidence | Out-Null
powershell -NoProfile -ExecutionPolicy Bypass -File D:\ki9\sep490\Sep490\AICOS-BE\infra\postgres\security\Complete-ServiceRoleCredentials.ps1 -EvidenceDirectory $resetEvidence
```

The reviewed helper hash remains:
1EA90A6F24078A3D649135436A4AF8F9FE62C905271BA123659D2925F0E57B13.

No console was reopened in this preparation. Gate A is incomplete: roles/ACLs
are ready, all-eleven password reset and real connection verification are
pending. This is not Gate C authorization.
