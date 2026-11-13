-- REVIEW ONLY. Separate deployment approval required. Never run against an unverified target.
-- DESTRUCTIVE DOWN; permitted only when ALL new tables are empty. Prefer feature OFF and retain ledger.
START TRANSACTION;
SET LOCAL lock_timeout = '3s';
SET LOCAL statement_timeout = '30s';
DO $guard$
BEGIN
 IF current_setting('aicos.bill_rollback_approved', true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Explicit rollback approval required'; END IF;
 IF current_setting('aicos.expected_database', true) IS NULL OR current_database() <> current_setting('aicos.expected_database', true) THEN RAISE EXCEPTION 'Database identity mismatch'; END IF;
 IF current_setting('aicos.expected_schema', true) IS DISTINCT FROM 'payment' THEN RAISE EXCEPTION 'Service schema mismatch'; END IF;
 IF EXISTS (SELECT 1 FROM payment.session_bills) THEN RAISE EXCEPTION 'Nonempty payment.session_bills: data-preserving rollback required'; END IF;
 IF EXISTS (SELECT 1 FROM payment.session_bill_orders) THEN RAISE EXCEPTION 'Nonempty payment.session_bill_orders: data-preserving rollback required'; END IF;
 IF EXISTS (SELECT 1 FROM payment.session_bill_attempts) THEN RAISE EXCEPTION 'Nonempty payment.session_bill_attempts: data-preserving rollback required'; END IF;
 IF EXISTS (SELECT 1 FROM payment.session_bill_events) THEN RAISE EXCEPTION 'Nonempty payment.session_bill_events: data-preserving rollback required'; END IF;
END $guard$;


DROP TABLE payment.session_bill_attempts;

DROP TABLE payment.session_bill_events;

DROP TABLE payment.session_bill_orders;

DROP TABLE payment.session_bills;

DELETE FROM payment.__ef_migrations_history
WHERE migration_id = '20261113125804_PaymentSessionBills';

COMMIT;
