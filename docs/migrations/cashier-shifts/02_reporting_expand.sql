-- REVIEW ONLY: explicit target, cluster and approval required. No operational execution is authorized by this file.
DO $$ BEGIN
 IF current_setting('aicos.expected_database',true) IS NULL OR current_database() <> current_setting('aicos.expected_database',true) OR current_database()='aicos_db' THEN RAISE EXCEPTION 'Database target guard failed'; END IF;
 IF current_setting('aicos.expected_cluster',true) IS NULL OR (SELECT system_identifier::text FROM pg_control_system()) <> current_setting('aicos.expected_cluster',true) THEN RAISE EXCEPTION 'Cluster guard failed'; END IF;
 IF current_setting('aicos.cashier_expand_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Explicit cashier expand approval required'; END IF;
END $$;
﻿START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM reporting.__ef_migrations_history WHERE "migration_id" = '20261114125038_ConfirmedCollectionInbox') THEN
    CREATE TABLE reporting.collection_facts (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        branch_id uuid NOT NULL,
        method character varying(20) NOT NULL,
        amount numeric(18,0) NOT NULL,
        paid_at_utc timestamp with time zone NOT NULL,
        payload jsonb NOT NULL,
        CONSTRAINT pk_collection_facts PRIMARY KEY (id),
        CONSTRAINT ck_collection_fact_amount CHECK (amount > 0)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM reporting.__ef_migrations_history WHERE "migration_id" = '20261114125038_ConfirmedCollectionInbox') THEN
    CREATE INDEX ix_collection_facts_tenant_id_branch_id_paid_at_utc ON reporting.collection_facts (tenant_id, branch_id, paid_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM reporting.__ef_migrations_history WHERE "migration_id" = '20261114125038_ConfirmedCollectionInbox') THEN
    INSERT INTO reporting.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261114125038_ConfirmedCollectionInbox', '8.0.31');
    END IF;
END $EF$;
COMMIT;

