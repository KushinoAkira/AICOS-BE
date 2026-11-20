# Local schema review — 19/11/2026

Scope: catalog/history only for the five service databases affected by the merged main/local features. This is not a full data-integrity or service-role ACL audit.

Target verified before catalogs: `aicos-main-postgres`, PostgreSQL cluster **7703616372368646182**. Every psql catalog query used `BEGIN READ ONLY`, and `transaction_read_only=on` was verified. No passwords were read, no data rows selected, no migration/GRANT/DDL/DML or runtime container change performed.

| Database/schema | Tables including EF history | Columns | Pending source migration |
|---|---:|---:|---|
| aicos_ordering / ordering | 13 | 167 | 20261113093202_OrderBasketsAssociationRules; 20261118143317_OrderingSessionManualDiscount; 20261118161648_OrderingSafeOrderCancellation |
| aicos_kitchen / kitchen | 12 | 161 | 20261118161652_KitchenSafeOrderCancellation |
| aicos_payment / payment | 15 | 180 | None |
| aicos_customer / customer | 9 | 106 | 20261113093517_CrmAndLastPaid |
| aicos_workforce / workforce | 10 | 134 | 20261113011305_PayrollPeriods; 20261118013000_ManagerPayroll |

Applied history entries all exist in source; no applied migration was edited or deleted. There are **7 pending migrations**, not seven automatically approved changes. Ordering/Customer/Workforce include upstream main features beyond the QR adapter. New main Ordering paid-event handling and Apriori/CRM background workers may write their new tables: MigrateOnStartup=false does not make missing schema safe.

## Proposed deployment order — separate approval required

1. Keep current runtime images and finance/background feature flags unchanged. Preserve the staged merge and current WIP; do not reset/clean.
2. Inventory dependencies/ACL and review the exact SQL for each pending migration, including upstream Apriori, CRM and Payroll. Separate unrelated feature rollout if appropriate; no blanket `database update` on main.
3. Back up affected service databases consistently and restore-drill into an isolated cluster. Re-run cluster/history/shape/ACL preflight and checksum review immediately before an approved write gate.
4. Apply only the individually approved expansions. Cancellation/discount SQL and rollback guards are in `docs/proposals/`; upstream EF migrations need their own review before execution. No migration is applied by this report.
5. Review/apply least-privilege grants separately; preserve audit/receipt tables. Existing runtime roles' actual effective ACL was **not** tested by this catalog-only pass.
6. Recreate only approved services with MigrateOnStartup=false; keep unapproved workers/features OFF. Do not replace the whole stack merely because the combined solution builds.

The Takeaway ZaloPay sandbox adapter adds **no table/column/migration** and reuses Payments' existing ledger. This does not approve a Payments deployment, provider configuration or network transaction. It also does not approve deploying new Ordering/Customer/Workforce code against old schema.

Before financial activation: live provider secrets through an approved local secret mechanism (never chat/Git/logs), public callback configuration, old pending-attempt review and isolated end-to-end evidence are required. Bank production, Refund extensions and Session Bill provider QR remain outside this step.
