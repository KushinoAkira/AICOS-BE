-- REVIEW ONLY: do not execute against an operating database without separate approval.
DO $$ BEGIN IF current_setting('aicos.expected_database',true) IS NULL OR current_database()<>current_setting('aicos.expected_database',true) OR current_database()='aicos_db' OR current_setting('aicos.expected_cluster',true) IS NULL OR (SELECT system_identifier::text FROM pg_control_system())<>current_setting('aicos.expected_cluster',true) OR current_setting('aicos.cashier_rollback_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Cashier rollback target/cluster/approval guard failed'; END IF; END $$;
﻿START TRANSACTION;

DO $$ BEGIN IF EXISTS (SELECT 1 FROM payment.collection_outbox) OR EXISTS (SELECT 1 FROM payment.cashier_shifts) THEN RAISE EXCEPTION 'Nonempty financial rollback refused; retain ledger and roll code forward'; END IF; END $$;

DROP TABLE payment.collection_outbox;

DROP TABLE payment.cashier_shifts;

DELETE FROM payment.__ef_migrations_history
WHERE migration_id = '20261114125030_CashierMoneyShifts';

COMMIT;

