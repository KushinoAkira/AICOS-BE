BEGIN READ ONLY;
SELECT current_database(), current_user, system_identifier::text FROM pg_control_system();
DO $$ BEGIN
  IF current_database() <> 'aicos_ordering' THEN RAISE EXCEPTION 'Wrong deployment database'; END IF;
  IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN
    RAISE EXCEPTION 'Expected cluster must be supplied and verified before proceeding';
  END IF;
  IF to_regclass('ordering.orders') IS NULL OR to_regclass('ordering.table_sessions') IS NULL
    OR to_regclass('ordering.session_checkouts') IS NULL
    OR to_regclass('ordering.__ef_migrations_history') IS NULL THEN
    RAISE EXCEPTION 'Required Ordering/SessionBilling baseline missing';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM ordering.__ef_migrations_history WHERE migration_id = '20261114164841_DurableTableTransfer') THEN
    RAISE EXCEPTION 'Review the actual migration baseline before proceeding';
  END IF;
  IF to_regclass('ordering.session_discount_events') IS NOT NULL AND NOT EXISTS
    (SELECT 1 FROM ordering.__ef_migrations_history WHERE migration_id = '20261118143317_OrderingSessionManualDiscount') THEN
    RAISE EXCEPTION 'Existing table without matching migration history; do not recreate';
  END IF;
END $$;
SELECT migration_id FROM ordering.__ef_migrations_history ORDER BY migration_id;
SELECT table_name,column_name,data_type,is_nullable FROM information_schema.columns
WHERE table_schema='ordering' AND table_name IN ('session_discount_events','orders') ORDER BY table_name,ordinal_position;
COMMIT;
