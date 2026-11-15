-- PROPOSAL: verified target/cluster and separate approval required.
DO $$ BEGIN
 IF current_setting('aicos.expected_database',true) IS NULL OR current_database()<>current_setting('aicos.expected_database',true) OR current_database()='aicos_db' THEN RAISE EXCEPTION 'Operations database guard failed'; END IF;
 IF current_setting('aicos.expected_cluster',true) IS NULL OR (SELECT system_identifier::text FROM pg_control_system())<>current_setting('aicos.expected_cluster',true) THEN RAISE EXCEPTION 'Operations cluster guard failed'; END IF;
 IF current_setting('aicos.operations_expand_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Operations expand approval required'; END IF;
END $$;
﻿START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM payment.__ef_migrations_history WHERE "migration_id" = '20261114164856_BillSplitPlans') THEN
    CREATE TABLE payment.bill_split_plans (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        branch_id uuid NOT NULL,
        session_id uuid NOT NULL,
        version bigint NOT NULL,
        fingerprint character varying(64) NOT NULL,
        quote_json jsonb NOT NULL,
        groups_json jsonb NOT NULL,
        updated_by uuid NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT pk_bill_split_plans PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM payment.__ef_migrations_history WHERE "migration_id" = '20261114164856_BillSplitPlans') THEN
    CREATE TABLE payment.bill_split_operations (
        id uuid NOT NULL,
        plan_id uuid NOT NULL,
        actor_id uuid NOT NULL,
        request_hash character varying(64) NOT NULL,
        before_json jsonb NOT NULL,
        after_json jsonb NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT pk_bill_split_operations PRIMARY KEY (id),
        CONSTRAINT fk_bill_split_operations_bill_split_plans_plan_id FOREIGN KEY (plan_id) REFERENCES payment.bill_split_plans (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM payment.__ef_migrations_history WHERE "migration_id" = '20261114164856_BillSplitPlans') THEN
    CREATE INDEX ix_bill_split_operations_plan_id ON payment.bill_split_operations (plan_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM payment.__ef_migrations_history WHERE "migration_id" = '20261114164856_BillSplitPlans') THEN
    CREATE UNIQUE INDEX ix_bill_split_plans_tenant_id_session_id ON payment.bill_split_plans (tenant_id, session_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM payment.__ef_migrations_history WHERE "migration_id" = '20261114164856_BillSplitPlans') THEN
    INSERT INTO payment.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261114164856_BillSplitPlans', '8.0.31');
    END IF;
END $EF$;
COMMIT;

