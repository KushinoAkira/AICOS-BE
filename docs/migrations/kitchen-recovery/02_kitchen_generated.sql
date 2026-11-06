START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM kitchen.__ef_migrations_history WHERE "migration_id" = '20261106164227_KitchenAdmissionReceipts') THEN
    ALTER TABLE kitchen.kitchen_tickets ADD recovery_hold_reason character varying(200);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM kitchen.__ef_migrations_history WHERE "migration_id" = '20261106164227_KitchenAdmissionReceipts') THEN
    CREATE TABLE kitchen.order_admission_receipts (
        producer_key character varying(80) NOT NULL,
        consumer_key character varying(80) NOT NULL,
        event_id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        branch_id uuid NOT NULL,
        order_id uuid NOT NULL,
        creation_sha256 bytea NOT NULL,
        ticket_id uuid NOT NULL,
        receipt_status character varying(20) NOT NULL,
        recorded_at_utc timestamp with time zone NOT NULL,
        applied_at_utc timestamp with time zone,
        last_verified_at_utc timestamp with time zone NOT NULL,
        last_error_code character varying(100),
        CONSTRAINT pk_order_admission_receipts PRIMARY KEY (producer_key, consumer_key, event_id),
        CONSTRAINT ck_kitchen_admission_receipt_applied CHECK ((receipt_status = 'APPLIED' AND applied_at_utc IS NOT NULL) OR (receipt_status = 'NEEDS_REVIEW' AND applied_at_utc IS NULL)),
        CONSTRAINT ck_kitchen_admission_receipt_hash CHECK (octet_length(creation_sha256) = 32),
        CONSTRAINT fk_order_admission_receipts_tickets_ticket_id FOREIGN KEY (ticket_id) REFERENCES kitchen.kitchen_tickets (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM kitchen.__ef_migrations_history WHERE "migration_id" = '20261106164227_KitchenAdmissionReceipts') THEN
    CREATE INDEX ix_kitchen_admission_receipt_order ON kitchen.order_admission_receipts (tenant_id, order_id, consumer_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM kitchen.__ef_migrations_history WHERE "migration_id" = '20261106164227_KitchenAdmissionReceipts') THEN
    CREATE INDEX ix_kitchen_admission_receipt_scope ON kitchen.order_admission_receipts (tenant_id, branch_id, receipt_status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM kitchen.__ef_migrations_history WHERE "migration_id" = '20261106164227_KitchenAdmissionReceipts') THEN
    CREATE INDEX ix_order_admission_receipts_ticket_id ON kitchen.order_admission_receipts (ticket_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM kitchen.__ef_migrations_history WHERE "migration_id" = '20261106164227_KitchenAdmissionReceipts') THEN
    INSERT INTO kitchen.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261106164227_KitchenAdmissionReceipts', '8.0.31');
    END IF;
END $EF$;
COMMIT;

