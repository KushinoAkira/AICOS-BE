START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261106164220_KitchenAdmissionOutbox') THEN
    CREATE TABLE ordering.kitchen_admission_outbox (
        event_id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        branch_id uuid NOT NULL,
        order_id uuid NOT NULL,
        consumer_key character varying(80) NOT NULL,
        schema_version integer NOT NULL,
        creation_payload jsonb NOT NULL,
        creation_sha256 bytea NOT NULL,
        origin character varying(20) NOT NULL,
        state character varying(20) NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        next_attempt_at_utc timestamp with time zone NOT NULL,
        attempt_count integer NOT NULL,
        lease_token uuid,
        lease_until_utc timestamp with time zone,
        acknowledged_at_utc timestamp with time zone,
        acknowledged_ticket_id uuid,
        last_error_code character varying(100),
        CONSTRAINT pk_kitchen_admission_outbox PRIMARY KEY (event_id),
        CONSTRAINT ck_kitchen_admission_ack CHECK ((state = 'ACKED' AND acknowledged_at_utc IS NOT NULL AND acknowledged_ticket_id IS NOT NULL) OR (state <> 'ACKED' AND acknowledged_at_utc IS NULL AND acknowledged_ticket_id IS NULL)),
        CONSTRAINT ck_kitchen_admission_lease CHECK ((state = 'IN_FLIGHT' AND lease_token IS NOT NULL AND lease_until_utc IS NOT NULL) OR (state <> 'IN_FLIGHT' AND lease_token IS NULL AND lease_until_utc IS NULL)),
        CONSTRAINT ck_kitchen_admission_payload CHECK (jsonb_typeof(creation_payload) = 'object' AND octet_length(creation_sha256) = 32 AND schema_version > 0 AND attempt_count >= 0 AND origin IN ('CAPTURE','RECONCILE')),
        CONSTRAINT ck_kitchen_admission_state CHECK (state IN ('PENDING','IN_FLIGHT','ACKED','NEEDS_REVIEW'))
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261106164220_KitchenAdmissionOutbox') THEN
    CREATE TABLE ordering.kitchen_admission_reconcile_state (
        consumer_key character varying(80) NOT NULL,
        cycle_id uuid NOT NULL,
        upper_order_id uuid,
        last_order_id uuid,
        cycle_started_at_utc timestamp with time zone NOT NULL,
        last_completed_at_utc timestamp with time zone,
        lease_token uuid,
        lease_until_utc timestamp with time zone,
        CONSTRAINT pk_kitchen_admission_reconcile_state PRIMARY KEY (consumer_key),
        CONSTRAINT ck_kitchen_reconcile_cursor CHECK (last_order_id IS NULL OR (upper_order_id IS NOT NULL AND last_order_id <= upper_order_id)),
        CONSTRAINT ck_kitchen_reconcile_lease CHECK ((lease_token IS NULL AND lease_until_utc IS NULL) OR (lease_token IS NOT NULL AND lease_until_utc IS NOT NULL))
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261106164220_KitchenAdmissionOutbox') THEN
    CREATE INDEX ix_kitchen_admission_due ON ordering.kitchen_admission_outbox (next_attempt_at_utc, lease_until_utc, event_id) WHERE state IN ('PENDING','IN_FLIGHT');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261106164220_KitchenAdmissionOutbox') THEN
    CREATE INDEX ix_kitchen_admission_scope_status ON ordering.kitchen_admission_outbox (tenant_id, branch_id, state, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261106164220_KitchenAdmissionOutbox') THEN
    CREATE UNIQUE INDEX uq_kitchen_admission_order ON ordering.kitchen_admission_outbox (tenant_id, order_id, consumer_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261106164220_KitchenAdmissionOutbox') THEN
    INSERT INTO ordering.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261106164220_KitchenAdmissionOutbox', '8.0.31');
    END IF;
END $EF$;
COMMIT;

