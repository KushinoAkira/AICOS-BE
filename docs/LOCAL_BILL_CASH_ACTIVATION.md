# Approved local Bill/Cash activation — 2026-11-17

User explicitly approved local Bill/Cash activation after backup/preflight, with separate DPAPI key, only Ordering/Payments/Kitchen/Table updates, QR OFF, MigrateOnStartup=false, no migration or real payment/close tests.

Executed `node scripts/Enable-LocalBillCash.mjs --approved-local-bill-cash`. Result PASS: `LOCAL_BILL_CASH_ACTIVATION_RESULT.json`.

- Cluster 7703616372368646182, exact existing Docker main service databases. Five existing Bill/checkout tables were empty and their column privileges verified; no schema/GRANT/history changes.
- Six open sessions contain one legacy paid Order at S01. Preserve it: existing billing.legacy_payment guard blocks conversion/double collection. Do not reset its payment status or auto-close.
- Paused four target writers, captured fresh custom-format backups of Ordering/Kitchen/TableReservation/Payments to restricted private LocalAppData storage outside Git, with SHA256 and PGDMP/pg_dump verification. No new restore drill was run; never restore over operating DB without separate approval.
- Created a separate machine key under CurrentUser DPAPI. Plain key exists only in process/runtime environment; no source/log/Git plaintext, no JWT or DB credential reset.
- Recreated exactly four services on identical images with all unrelated environment/ports preserved. Billing ON, Paid dispatcher ON in Payments, test QR OFF, migration OFF; Barista API ON and printer worker OFF retained. Other workers/Collections dispatcher retain prior state.
- Machine-auth read-only occupancy GET works, unauthenticated calls denied; Gateway/health PASS. All table row fingerprints in all four databases and all other container snapshots unchanged. No login, user JWT fabrication, payment POST or session close was executed as smoke.
- Only old stopped containers with zero mounts were removed after verification; no volume removed. Key/backups private and persistent; paths/checksums (no secrets) in result JSON.

Global/base Compose still defaults features OFF. Docker start/restart of the current containers retains the approved runtime configuration. Do not blindly recreate from the default local Compose/tag: the existing `:local` tags differ from the reviewed running images, and baseline templates do not load the new DPAPI key automatically. Preserve exact images/credentials/key/flags under a separately reviewed recreate procedure. The activation helper intentionally refuses once Bill tables are nonempty; it is not a financial data backfill or automatic recovery tool.

Mobile routing fix and tests: AICOS-Mobile/docs/BILL_CHECKOUT_LOCAL_FIX.md. Physical user payment is NOT RUN by the agent; QR banking, Refund, Split Bill, transfer V2 and Reporting dispatcher activation are not included. No commit/push.