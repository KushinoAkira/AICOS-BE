-- PROPOSAL: verified target/cluster and separate approval required.
DO $$ BEGIN
 IF current_setting('aicos.expected_database',true) IS NULL OR current_database()<>current_setting('aicos.expected_database',true) OR current_database()='aicos_db' THEN RAISE EXCEPTION 'Operations database guard failed'; END IF;
 IF current_setting('aicos.expected_cluster',true) IS NULL OR (SELECT system_identifier::text FROM pg_control_system())<>current_setting('aicos.expected_cluster',true) THEN RAISE EXCEPTION 'Operations cluster guard failed'; END IF;
 IF current_setting('aicos.operations_rollback_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Operations rollback approval required'; END IF;
END $$;
﻿START TRANSACTION;

DO $$ BEGIN IF EXISTS (SELECT 1 FROM payment.bill_split_plans) OR EXISTS (SELECT 1 FROM payment.bill_split_operations) THEN RAISE EXCEPTION 'Split audit is nonempty; rollback refused'; END IF; END $$;

DROP TABLE payment.bill_split_operations;

DROP TABLE payment.bill_split_plans;

DELETE FROM payment.__ef_migrations_history
WHERE migration_id = '20261114164856_BillSplitPlans';

COMMIT;

