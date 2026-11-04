# Local Cashier account setup

Date: 2026-11-04 (Asia/Saigon).

Additional named email Cashiers were subsequently created for the three branches:
[Named local Cashier fixtures](LOCAL_CASHIER_STAFF_FIXTURES.md). This original
account's password and assignment were not modified by that follow-up.

Follow-up: [Local branch and floor-plan fixtures](LOCAL_BRANCH_FLOOR_PLAN_SETUP.md)
records the subsequent three-branch setup. The original branch ID was retained
and renamed to Hồ Con Rùa (Q.3); Cashier assignment remains unchanged. Runtime
and verification statements below describe the original account setup step.

## Runtime and scope

- Project/container prefix: `aicos-main`.
- Identity database: `aicos_identity`, schema `identity`, in `aicos-main-postgres`.
- Identity: `http://127.0.0.1:5201`; Gateway: `http://127.0.0.1:8081`.
- Started the existing PostgreSQL, Redis, Identity and Gateway containers.
- Verified Identity `Database__MigrateOnStartup=false` and `Seed__DemoPlans=false`.
- This continuation did not run migrations or modify application code/schema.
- Other service containers remain stopped; this verifies account login, not the complete ordering/payment flow.

## Local fixtures

- Reused plan `AICOS Local Test`.
- Tenant: `aicos-cashier-local` / `AICOS Cashier Local`.
- Branch: `Chi nhanh Local` (the actual API name uses Vietnamese accents).
- Username: `cashier`.
- Email: `cashier@aicos-cashier-local.test`.
- Role: `Cashier`; assigned to the local branch and tenant.
- `MustChangePassword=false` after changing the generated temporary password through the API.
- Passwords and tokens are intentionally excluded from this document and source control.
- The local BrandAdmin recovery credential is encrypted with current-user Windows DPAPI in the local temporary directory, outside this repository. Only that Windows user can decrypt it; do not commit or share the credential artifact.

## Cleanup and API handling

The user approved cleanup of the failed local provisioning fixtures. Removed only
the failed `aicos-local` / `aicos-cashier-local` tenants and their test users,
sessions and subscriptions. Retained append-only audit records. An attempted
audit deletion was blocked by the database trigger and the transaction rolled
back; no audit trigger was disabled or bypassed. Subsequent cleanup committed
only after checking that no branches or subscription payments existed.

Recreated one local tenant and branch using Identity APIs. Earlier branch
requests returned HTTP 400; the successful request used explicit UTF-8 JSON
bytes and the minimal branch payload (`name`). The exact earlier validation
reason was not captured, so a time-format root cause is not claimed. Future
PowerShell requests containing Vietnamese text should send explicit UTF-8 JSON.

## Verification actually performed

- Identity API: create tenant, create branch, create Cashier user: PASS.
- Identity API: temporary-password login, password change, final-password login: PASS.
- Identity `GET /api/v1/identity/auth/me`: PASS; Cashier role, tenant/branch scope and password-change flag checked.
- Gateway `GET /health/live`: HTTP 200.
- Gateway login and `GET /api/v1/identity/auth/me`: PASS using the real local account and PostgreSQL-backed Identity API.
- Verification sessions logged out after each successful check.
- Gateway full readiness did not pass with the remaining services stopped; no full-stack readiness claim.
- Flutter/device login, table/menu/order/payment flows: NOT TESTED in this task.
- No unit-test suite rerun: this task provisioned local runtime data and documentation, without application code changes.
- No commit or push performed.
