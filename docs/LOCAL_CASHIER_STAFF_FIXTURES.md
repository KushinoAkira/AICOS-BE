# Named local Cashier fixtures

Date: 2026-11-04 (Asia/Saigon).

The user authorized adding named Cashier test accounts and approved a password
that satisfies the existing Identity policy. The initial suggestion did not
satisfy minimum length/letter rules; the policy was not changed. The approved
password is communicated privately and intentionally omitted from source/docs.

## Accounts created through the Identity API

| Full name | Email | Username | Branch |
| --- | --- | --- | --- |
| Nguyễn Thùy Linh | linh.cashier@aicos-local.test | linh.cashier | Hồ Con Rùa (Q.3) |
| Trần Minh Anh | anh.cashier@aicos-local.test | anh.cashier | Nguyễn Huệ (Q.1) |
| Lê Hoàng Nam | nam.cashier@aicos-local.test | nam.cashier | Phan Xích Long (Phú Nhuận) |

All three have role Cashier, belong to tenant `aicos-cashier-local`, have an
active branch assignment, and have `MustChangePassword=false` after password
change through the existing API. The original `cashier` account is unchanged.
Identity persists these records in `aicos_identity.identity.users` within
`aicos-main-postgres`. No legacy Windows database was used.

## Execution and verification

1. Load existing local BrandAdmin credential using current-user Windows DPAPI.
2. Login through Gateway and verify the local tenant/admin scope.
3. Resolve the three active branches by name; check for existing accounts by email.
4. POST `/api/v1/identity/users` with a random temporary password and correct branch.
5. Login the new user and POST `/api/v1/identity/auth/change-password` to the
   user-approved local test password. Checkpoints are DPAPI-encrypted outside Git.
6. Login using each email, then GET `/identity/auth/me` and `/tables/floor-plan`.
   Verified full name, role, tenant, branch, password-change flag, and 3 areas /
   30 tables for each account. All three PASS against real PostgreSQL-backed APIs.
7. Logout verification/admin sessions; password change revoked temporary sessions.

Mobile Home now displays the full name returned by GET me, with branch/role;
the account page also shows name/email. UI changes are in the separate Mobile
repository. Flutter analyzer: 0 issues; full suite with Demo Mode: 113 PASS.
Widget tests use HTTP MockClient and cover 360/393/430px. These are separate from
the real Identity/Gateway checks above; no new physical-device build was run.

Email/username login is reused unchanged. Phone login was not implemented or
claimed in this step. No new endpoints, Backend source changes, migrations,
schema/ACL changes, deletion, order/payment fixtures or commit/push were performed.
