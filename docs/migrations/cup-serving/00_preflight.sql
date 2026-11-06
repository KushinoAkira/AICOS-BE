-- REVIEW ONLY. Must be run by an approved DBA/isolated-test operator, never by application startup.
-- Required psql variables: expected_database, expected_schema, expected_system_identifier.
\set ON_ERROR_STOP on
\if :{?expected_database}
\else
  \echo 'STOP: expected_database is required.'
  \quit 3
\endif
\if :{?expected_schema}
\else
  \echo 'STOP: expected_schema is required.'
  \quit 3
\endif
\if :{?expected_system_identifier}
\else
  \echo 'STOP: expected_system_identifier is required.'
  \quit 3
\endif

BEGIN TRANSACTION READ ONLY;
SELECT current_database(), current_user, inet_server_addr(), inet_server_port(),
       current_setting('transaction_read_only') AS read_only;
SELECT current_database() = :'expected_database'
   AND :'expected_schema' IN ('kitchen', 'ordering')
   AND (SELECT system_identifier::text FROM pg_control_system()) = :'expected_system_identifier'
   AND NOT pg_is_in_recovery()
   AND to_regclass(format('%I.__ef_migrations_history', :'expected_schema')) IS NOT NULL AS target_ok \gset
\if :target_ok
\else
  ROLLBACK;
  \echo 'STOP: target/cluster/schema/history mismatch.'
  \quit 3
\endif

SELECT EXISTS (
    SELECT 1 FROM :"expected_schema".__ef_migrations_history
    WHERE migration_id = CASE :'expected_schema'
      WHEN 'kitchen' THEN '20261106164227_KitchenAdmissionReceipts'
      ELSE '20261106164220_KitchenAdmissionOutbox' END
) AND NOT EXISTS (
    SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = :'expected_schema'
      AND c.relname = ANY (CASE :'expected_schema'
        WHEN 'kitchen' THEN ARRAY['cup_tasks','cup_claims','cup_operations','fulfillment_outbox']
        ELSE ARRAY['order_fulfillment_projection','fulfillment_receipts'] END)
) AND NOT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = :'expected_schema' AND column_name = 'fulfillment_mode'
      AND table_name = CASE :'expected_schema' WHEN 'kitchen' THEN 'kitchen_tickets' ELSE 'orders' END
) AS baseline_ok \gset
\if :baseline_ok
\else
  ROLLBACK;
  \echo 'STOP: baseline missing, already applied or partially applied. Reconcile manually; do not rerun DDL.'
  \quit 3
\endif
SELECT 'PREFLIGHT PASS: metadata only; migration is NOT executed.' AS result;
ROLLBACK;
