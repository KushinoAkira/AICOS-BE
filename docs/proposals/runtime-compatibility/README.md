# Runtime compatibility package

See ../../CASHIER_DASHBOARD_RUNTIME_SMOKE_EXECUTION.md. Actual smoke stopped before any app start: Menu image_url width and missing Customer HuntVouchers schema. Historical first attempt: infrastructure only was healthy. After separate approval, all three files executed and verified PASS; see ../../RUNTIME_COMPATIBILITY_EXECUTION.md. This package is not to be replayed: exact predecessor history guards now intentionally refuse repeat expansion.

Approval scopes/files:

1. Menu schema: 01_menu_expand.PROPOSAL.sql (existing ProductPictureUpload).
2. Customer schema: 02_customer_expand.PROPOSAL.sql (existing HuntVouchers, zero-backfill guard).
3. Customer permissions: 10_customer_grants.PROPOSAL.sql (separate explicit approval).

All require expected database/schema/cluster settings and action-specific aicos.runtime_compat_expand_approved or aicos.runtime_compat_grants_approved=true, in the same psql connection, with ON_ERROR_STOP=1. Target only aicos_menu/menu and aicos_customer/customer on cluster 7703616372368646182. Wrong identity/history, existing target structures and Customer backfill >0 refuse. No schema movement or cross-service FK.

Upstream Customer migration has historical INSERT from loyalty note text. It remains unchanged; current preview 0 rows and the wrapper requires 0 at execution. A nonzero result requires separate business review. Do not bypass the guard, fabricate migration history or drop existing data. New FK cascades only from customers to member_orders and unique OrderId prevents duplicate membership records.

Rollback files 90/91 contain destructive Down operations only for unused additions and need separate approval: Menu long images refuse narrowing; Customer used table/columns refuse removal. After business writes retain schema and repair forward. Never auto-run rollback or revoke all permissions.

Reproduce on isolated PostgreSQL:

    dotnet run --project tests/AICOS.Billing.StackTools -- runtime-compatibility-scripts <temporary-export-folder>
    node scripts/tests/runtime-compatibility.mjs

Runner consumes the temporary export under TEMP/aicos-runtime-compatibility, creates its own internal/tmpfs/no-host-port PG, synthetic fixtures/credentials, and removes only that scratch container/network. No operating connection. Result: 13 PASS. Full real-data backup/restore is independent: 11 PASS across eight DBs; private dumps never enter Git. SHA256SUMS.txt pins the five proposal files, separate from 17 prior immutable files.
