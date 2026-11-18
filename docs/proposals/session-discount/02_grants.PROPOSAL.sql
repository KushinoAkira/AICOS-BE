-- Proposal only. DBA applies after separate approval and preflight/backup.
BEGIN;
DO $$ BEGIN
  IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN
    RAISE EXCEPTION 'Expected cluster must be supplied and verified before proceeding';
  END IF;
  IF current_database() <> 'aicos_ordering' OR to_regclass('ordering.session_discount_events') IS NULL
    OR NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='ordering_svc') THEN
    RAISE EXCEPTION 'Wrong database, missing table, or missing existing service role';
  END IF;
END $$;
GRANT USAGE ON SCHEMA ordering TO ordering_svc;
GRANT SELECT,INSERT ON ordering.session_discount_events TO ordering_svc;
GRANT UPDATE(discount_amount,updated_at_utc,updated_by) ON ordering.orders TO ordering_svc;
-- No UPDATE/DELETE/TRUNCATE audit permissions, new login, credentials, ownership or PUBLIC changes.
COMMIT;
