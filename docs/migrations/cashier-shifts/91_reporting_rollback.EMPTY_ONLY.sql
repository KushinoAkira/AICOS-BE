-- REVIEW ONLY: do not execute against an operating database without separate approval.
DO $$ BEGIN IF current_setting('aicos.expected_database',true) IS NULL OR current_database()<>current_setting('aicos.expected_database',true) OR current_database()='aicos_db' OR current_setting('aicos.expected_cluster',true) IS NULL OR (SELECT system_identifier::text FROM pg_control_system())<>current_setting('aicos.expected_cluster',true) OR current_setting('aicos.cashier_rollback_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Cashier rollback target/cluster/approval guard failed'; END IF; END $$;
﻿START TRANSACTION;

DO $$ BEGIN IF EXISTS (SELECT 1 FROM reporting.collection_facts) THEN RAISE EXCEPTION 'Nonempty reporting rollback refused; retain receipt inbox'; END IF; END $$;

DROP TABLE reporting.collection_facts;

DELETE FROM reporting.__ef_migrations_history
WHERE migration_id = '20261114125038_ConfirmedCollectionInbox';

COMMIT;

