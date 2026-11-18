-- Proposal only. Operational execution requires approval, backup and preflight.
SET lock_timeout = '5s';
SET statement_timeout = '60s';
DO $$ BEGIN
  IF current_database() <> 'aicos_ordering' THEN RAISE EXCEPTION 'Wrong deployment database'; END IF;
  IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN
    RAISE EXCEPTION 'Expected cluster must be supplied and verified before proceeding';
  END IF;
END $$;

START TRANSACTION;

LOCK TABLE ordering.session_discount_events IN ACCESS EXCLUSIVE MODE;
DO $$ BEGIN
  IF EXISTS (SELECT 1 FROM ordering.session_discount_events) THEN
    RAISE EXCEPTION 'Discount audit contains data; disable feature and reconcile. Destructive rollback refused.';
  END IF;
END $$;

DROP TABLE ordering.session_discount_events;

DELETE FROM ordering.__ef_migrations_history
WHERE migration_id = '20261118143317_OrderingSessionManualDiscount';

COMMIT;
