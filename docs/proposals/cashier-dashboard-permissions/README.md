# Dashboard permissions revision ? proposal only

Read ../../CASHIER_DASHBOARD_PERMISSIONS_REVIEW.md before execution. Current status: real PostgreSQL rehearsal 26 PASS / 1 N/A; local GRANT NOT APPROVED OR APPLIED.

This folder replaces only the old Payments history guard. GRANT statements/permission scope are unchanged. Current Payments history must include RefundToBankAccount. Keep both older SQL packages frozen; check this folder's independent SHA256SUMS.txt.

The recommended local gate consists of Ordering's original 10_ordering_grants, this revised 12_payment_grants and Reporting's original 13_reporting_grants. Skip Kitchen because current owner rights already cover its one required column. Never run both old and revised Payments grant files.

Reproduce on isolated PostgreSQL only, from the Backend review checkout:

    dotnet run --project tests/AICOS.Billing.StackTools -- dashboard-permissions-scripts <temporary-export-directory>
    node scripts/tests/dashboard-permissions.mjs <temporary-export-directory>

Exporter opens no database. Runner creates synthetic schemas/roles, applies the reviewed grants only in a fresh internal/tmpfs/no-host-port container, tests real password authentication and allowed/forbidden permissions, and removes only its own container/network. It never connects to local operating PostgreSQL or copies business data/secrets. Tests use zero-row INSERT/UPDATE for ACL, not fake financial E2E.

Application: only after separate approval, pin container c014190eed2d / cluster 7703616372368646182, stop apps and capture existing ACLs; pass expected target settings and dashboard_grants_approved in the same psql session as each file. Stop after each file and verify; do not start apps, change flags or execute rollback automatically. No local GRANT/REVOKE is executed by this document or by generating this proposal.
