-- PROPOSAL: verified target/cluster and separate approval required.
DO $$ BEGIN
 IF current_setting('aicos.expected_database',true) IS NULL OR current_database()<>current_setting('aicos.expected_database',true) OR current_database()='aicos_db' THEN RAISE EXCEPTION 'Operations database guard failed'; END IF;
 IF current_setting('aicos.expected_cluster',true) IS NULL OR (SELECT system_identifier::text FROM pg_control_system())<>current_setting('aicos.expected_cluster',true) THEN RAISE EXCEPTION 'Operations cluster guard failed'; END IF;
 IF current_setting('aicos.operations_rollback_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Operations rollback approval required'; END IF;
END $$;
﻿START TRANSACTION;

DO $$ BEGIN IF EXISTS (SELECT 1 FROM ordering.table_transfer_operations) THEN RAISE EXCEPTION 'Transfer history is nonempty; rollback refused'; END IF; END $$;

DROP TABLE ordering.table_transfer_operations;

DELETE FROM ordering.__ef_migrations_history
WHERE migration_id = '20261114164841_DurableTableTransfer';

COMMIT;

START TRANSACTION;

ALTER TABLE ordering.order_feedbacks DROP COLUMN replied_at_utc;

ALTER TABLE ordering.order_feedbacks DROP COLUMN replied_by_user_id;

ALTER TABLE ordering.order_feedbacks DROP COLUMN reply;

DELETE FROM ordering.__ef_migrations_history
WHERE migration_id = '20261113151937_FeedbackReply';

COMMIT;

