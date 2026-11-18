-- PROPOSAL: verified target/cluster and separate approval required.
DO $$ BEGIN
 IF current_setting('aicos.expected_database',true) IS NULL OR current_database()<>current_setting('aicos.expected_database',true) OR current_database()='aicos_db' THEN RAISE EXCEPTION 'Operations database guard failed'; END IF;
 IF current_setting('aicos.expected_cluster',true) IS NULL OR (SELECT system_identifier::text FROM pg_control_system())<>current_setting('aicos.expected_cluster',true) THEN RAISE EXCEPTION 'Operations cluster guard failed'; END IF;
 IF current_setting('aicos.operations_expand_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Operations expand approval required'; END IF;
END $$;
﻿START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261113151937_FeedbackReply') THEN
    ALTER TABLE ordering.order_feedbacks ADD replied_at_utc timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261113151937_FeedbackReply') THEN
    ALTER TABLE ordering.order_feedbacks ADD replied_by_user_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261113151937_FeedbackReply') THEN
    ALTER TABLE ordering.order_feedbacks ADD reply character varying(500);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261113151937_FeedbackReply') THEN
    INSERT INTO ordering.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261113151937_FeedbackReply', '8.0.31');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261114164841_DurableTableTransfer') THEN
    CREATE TABLE ordering.table_transfer_operations (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        branch_id uuid NOT NULL,
        session_id uuid NOT NULL,
        actor_id uuid NOT NULL,
        source_table_id uuid NOT NULL,
        target_table_id uuid NOT NULL,
        target_code character varying(20) NOT NULL,
        version bigint NOT NULL,
        request_hash character varying(64) NOT NULL,
        order_ids_json jsonb NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        synced_at_utc timestamp with time zone,
        CONSTRAINT pk_table_transfer_operations PRIMARY KEY (id),
        CONSTRAINT fk_table_transfer_operations_table_sessions_session_id FOREIGN KEY (session_id) REFERENCES ordering.table_sessions (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261114164841_DurableTableTransfer') THEN
    CREATE INDEX ix_table_transfer_operations_created_at_utc ON ordering.table_transfer_operations (created_at_utc) WHERE synced_at_utc IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261114164841_DurableTableTransfer') THEN
    CREATE INDEX ix_table_transfer_operations_session_id ON ordering.table_transfer_operations (session_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261114164841_DurableTableTransfer') THEN
    CREATE UNIQUE INDEX ix_table_transfer_operations_tenant_id_session_id_version ON ordering.table_transfer_operations (tenant_id, session_id, version);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261114164841_DurableTableTransfer') THEN
    INSERT INTO ordering.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261114164841_DurableTableTransfer', '8.0.31');
    END IF;
END $EF$;
COMMIT;

