START TRANSACTION;
DO $$ BEGIN
  IF current_database()<>'aicos_kitchen' OR current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Wrong or unverified deployment target'; END IF;
  IF NOT EXISTS(SELECT 1 FROM kitchen.__ef_migrations_history WHERE migration_id='20261114164849_TableTransferRoutingVersion') THEN RAISE EXCEPTION 'Review/apply the separately approved baseline first'; END IF;
  IF (to_regclass('kitchen.order_cancellation_receipts') IS NOT NULL) <> EXISTS(SELECT 1 FROM kitchen.__ef_migrations_history WHERE migration_id='20261118161652_KitchenSafeOrderCancellation') THEN RAISE EXCEPTION 'Existing table/history mismatch'; END IF;
END $$;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM kitchen.__ef_migrations_history WHERE "migration_id" = '20261118161652_KitchenSafeOrderCancellation') THEN
    CREATE TABLE kitchen.order_cancellation_receipts (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        branch_id uuid NOT NULL,
        order_id uuid NOT NULL,
        ticket_id uuid,
        request_hash character varying(64) NOT NULL,
        state character varying(16) NOT NULL,
        reason_code character varying(100),
        created_at_utc timestamp with time zone NOT NULL,
        completed_at_utc timestamp with time zone,
        CONSTRAINT pk_order_cancellation_receipts PRIMARY KEY (id),
        CONSTRAINT ck_kitchen_cancellation_state CHECK (state IN ('HELD','CANCELLED','ABORTED','REJECTED') AND ((state = 'HELD') = (completed_at_utc IS NULL))),
        CONSTRAINT fk_order_cancellation_receipts_tickets_ticket_id FOREIGN KEY (ticket_id) REFERENCES kitchen.kitchen_tickets (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM kitchen.__ef_migrations_history WHERE "migration_id" = '20261118161652_KitchenSafeOrderCancellation') THEN
    CREATE UNIQUE INDEX ix_order_cancellation_receipts_tenant_id_order_id ON kitchen.order_cancellation_receipts (tenant_id, order_id) WHERE state = 'HELD';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM kitchen.__ef_migrations_history WHERE "migration_id" = '20261118161652_KitchenSafeOrderCancellation') THEN
    CREATE INDEX ix_order_cancellation_receipts_ticket_id ON kitchen.order_cancellation_receipts (ticket_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM kitchen.__ef_migrations_history WHERE "migration_id" = '20261118161652_KitchenSafeOrderCancellation') THEN
    INSERT INTO kitchen.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261118161652_KitchenSafeOrderCancellation', '8.0.31');
    END IF;
END $EF$;
COMMIT;

