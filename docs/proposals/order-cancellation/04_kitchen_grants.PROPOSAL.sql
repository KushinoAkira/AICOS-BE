BEGIN;
DO $$ BEGIN
  IF current_database()<>'aicos_kitchen' OR current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system())
    OR to_regclass('kitchen.order_cancellation_receipts') IS NULL OR NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='kitchen_svc') THEN
    RAISE EXCEPTION 'Wrong database/cluster or missing existing table/role'; END IF;
END $$;
GRANT USAGE ON SCHEMA kitchen TO kitchen_svc;
GRANT SELECT,INSERT ON kitchen.order_cancellation_receipts TO kitchen_svc;
GRANT UPDATE(state,reason_code,completed_at_utc) ON kitchen.order_cancellation_receipts TO kitchen_svc;
-- Existing baseline SELECT + UPDATE rights on tickets are prerequisites. No new ownership, sequence, DELETE/TRUNCATE or PUBLIC changes.
COMMIT;
