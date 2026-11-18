-- Proposal only. Operational execution requires approval, backup and preflight.
SET lock_timeout = '5s';
SET statement_timeout = '60s';
DO $$ BEGIN
  IF current_database() <> 'aicos_ordering' THEN RAISE EXCEPTION 'Wrong deployment database'; END IF;
  IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN
    RAISE EXCEPTION 'Expected cluster must be supplied and verified before proceeding';
  END IF;
END $$;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261118143317_OrderingSessionManualDiscount') THEN
    CREATE TABLE ordering.session_discount_events (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        branch_id uuid NOT NULL,
        session_id uuid NOT NULL,
        actor_id uuid NOT NULL,
        revision bigint NOT NULL,
        request_hash character varying(64) NOT NULL,
        reason character varying(250) NOT NULL,
        mode character varying(10) NOT NULL,
        value numeric(18,2) NOT NULL,
        allocations_json jsonb NOT NULL,
        result_json jsonb NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT pk_session_discount_events PRIMARY KEY (id),
        CONSTRAINT ck_session_discount_input CHECK (mode IN ('AMOUNT','PERCENT') AND value >= 0 AND revision > 0),
        CONSTRAINT fk_session_discount_events_table_sessions_session_id FOREIGN KEY (session_id) REFERENCES ordering.table_sessions (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261118143317_OrderingSessionManualDiscount') THEN
    CREATE INDEX ix_session_discount_events_session_id ON ordering.session_discount_events (session_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261118143317_OrderingSessionManualDiscount') THEN
    CREATE UNIQUE INDEX ix_session_discount_events_tenant_id_session_id_revision ON ordering.session_discount_events (tenant_id, session_id, revision);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261118143317_OrderingSessionManualDiscount') THEN
    INSERT INTO ordering.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261118143317_OrderingSessionManualDiscount', '8.0.31');
    END IF;
END $EF$;
COMMIT;
