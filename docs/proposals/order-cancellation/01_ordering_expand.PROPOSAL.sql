START TRANSACTION;
DO $$ BEGIN
  IF current_database()<>'aicos_ordering' OR current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Wrong or unverified deployment target'; END IF;
  IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE migration_id='20261118143317_OrderingSessionManualDiscount') THEN RAISE EXCEPTION 'Review/apply the separately approved baseline first'; END IF;
  IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE migration_id='20261113093202_OrderBasketsAssociationRules') THEN RAISE EXCEPTION 'Merged main Apriori baseline requires separate review first'; END IF;
  IF (to_regclass('ordering.order_cancellation_operations') IS NOT NULL) <> EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE migration_id='20261118161648_OrderingSafeOrderCancellation') THEN RAISE EXCEPTION 'Existing table/history mismatch'; END IF;
END $$;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261118161648_OrderingSafeOrderCancellation') THEN
    CREATE TABLE ordering.order_cancellation_operations (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        branch_id uuid NOT NULL,
        session_id uuid NOT NULL,
        table_id uuid NOT NULL,
        order_id uuid NOT NULL,
        actor_id uuid NOT NULL,
        request_hash character varying(64) NOT NULL,
        fingerprint character varying(64) NOT NULL,
        order_code character varying(40) NOT NULL,
        table_code character varying(20) NOT NULL,
        reason character varying(250) NOT NULL,
        state character varying(16) NOT NULL,
        reason_code character varying(100),
        before_json jsonb NOT NULL,
        after_json jsonb,
        created_at_utc timestamp with time zone NOT NULL,
        next_attempt_at_utc timestamp with time zone NOT NULL,
        completed_at_utc timestamp with time zone,
        legacy_published_at_utc timestamp with time zone,
        attempts integer NOT NULL,
        version bigint NOT NULL,
        CONSTRAINT pk_order_cancellation_operations PRIMARY KEY (id),
        CONSTRAINT ck_order_cancellation_state CHECK (state IN ('QUEUED','RESERVED','APPLIED','ABORTING','COMPLETED','REJECTED','ABORTED') AND attempts >= 0 AND version >= 0 AND ((state IN ('COMPLETED','REJECTED','ABORTED')) = (completed_at_utc IS NOT NULL))),
        CONSTRAINT fk_order_cancellation_operations_orders_order_id FOREIGN KEY (order_id) REFERENCES ordering.orders (id) ON DELETE RESTRICT,
        CONSTRAINT fk_order_cancellation_operations_table_sessions_session_id FOREIGN KEY (session_id) REFERENCES ordering.table_sessions (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261118161648_OrderingSafeOrderCancellation') THEN
    CREATE INDEX ix_order_cancellation_operations_next_attempt_at_utc ON ordering.order_cancellation_operations (next_attempt_at_utc) WHERE completed_at_utc IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261118161648_OrderingSafeOrderCancellation') THEN
    CREATE INDEX ix_order_cancellation_operations_order_id ON ordering.order_cancellation_operations (order_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261118161648_OrderingSafeOrderCancellation') THEN
    CREATE INDEX ix_order_cancellation_operations_session_id ON ordering.order_cancellation_operations (session_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261118161648_OrderingSafeOrderCancellation') THEN
    CREATE UNIQUE INDEX ix_order_cancellation_operations_tenant_id_order_id ON ordering.order_cancellation_operations (tenant_id, order_id) WHERE completed_at_utc IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261118161648_OrderingSafeOrderCancellation') THEN
    CREATE INDEX ix_order_cancellation_operations_tenant_id_session_id ON ordering.order_cancellation_operations (tenant_id, session_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE "migration_id" = '20261118161648_OrderingSafeOrderCancellation') THEN
    INSERT INTO ordering.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261118161648_OrderingSafeOrderCancellation', '8.0.31');
    END IF;
END $EF$;
COMMIT;

