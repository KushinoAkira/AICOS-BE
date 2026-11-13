-- REVIEW ONLY. Separate deployment approval required. Never run against an unverified target.
-- DESTRUCTIVE DOWN; permitted only when ALL new tables are empty. Prefer feature OFF and retain ledger.
START TRANSACTION;
SET LOCAL lock_timeout = '3s';
SET LOCAL statement_timeout = '30s';
DO $guard$
BEGIN
 IF current_setting('aicos.bill_rollback_approved', true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Explicit rollback approval required'; END IF;
 IF current_setting('aicos.expected_database', true) IS NULL OR current_database() <> current_setting('aicos.expected_database', true) THEN RAISE EXCEPTION 'Database identity mismatch'; END IF;
 IF current_setting('aicos.expected_schema', true) IS DISTINCT FROM 'ordering' THEN RAISE EXCEPTION 'Service schema mismatch'; END IF;
 IF EXISTS (SELECT 1 FROM ordering.session_checkouts) THEN RAISE EXCEPTION 'Nonempty ordering.session_checkouts: data-preserving rollback required'; END IF;
END $guard$;


DROP TABLE ordering.session_checkouts;

DELETE FROM ordering.__ef_migrations_history
WHERE migration_id = '20261113125751_OrderingSessionCheckout';

COMMIT;
