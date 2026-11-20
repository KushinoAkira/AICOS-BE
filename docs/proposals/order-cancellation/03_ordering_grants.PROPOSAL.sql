BEGIN;
DO $$ BEGIN
  IF current_database()<>'aicos_ordering' OR current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system())
    OR to_regclass('ordering.order_cancellation_operations') IS NULL OR NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='ordering_svc') THEN
    RAISE EXCEPTION 'Wrong database/cluster or missing existing table/role'; END IF;
END $$;
GRANT USAGE ON SCHEMA ordering TO ordering_svc;
GRANT SELECT,INSERT ON ordering.order_cancellation_operations TO ordering_svc;
GRANT UPDATE(state,reason_code,after_json,next_attempt_at_utc,completed_at_utc,legacy_published_at_utc,attempts,version) ON ordering.order_cancellation_operations TO ordering_svc;
-- Existing baseline SELECT + UPDATE rights on orders are prerequisites. No new ownership, sequence, DELETE/TRUNCATE or PUBLIC changes.
COMMIT;
