-- PROPOSAL: verified target/cluster and separate approval required.
DO $$ BEGIN
 IF current_setting('aicos.expected_database',true) IS NULL OR current_database()<>current_setting('aicos.expected_database',true) OR current_database()='aicos_db' THEN RAISE EXCEPTION 'Operations database guard failed'; END IF;
 IF current_setting('aicos.expected_cluster',true) IS NULL OR (SELECT system_identifier::text FROM pg_control_system())<>current_setting('aicos.expected_cluster',true) THEN RAISE EXCEPTION 'Operations cluster guard failed'; END IF;
 IF current_setting('aicos.operations_expand_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Operations expand approval required'; END IF;
END $$;
﻿START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM kitchen.__ef_migrations_history WHERE "migration_id" = '20261114164849_TableTransferRoutingVersion') THEN
    ALTER TABLE kitchen.kitchen_tickets ADD table_transfer_version bigint NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM kitchen.__ef_migrations_history WHERE "migration_id" = '20261114164849_TableTransferRoutingVersion') THEN
    INSERT INTO kitchen.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261114164849_TableTransferRoutingVersion', '8.0.31');
    END IF;
END $EF$;
COMMIT;

