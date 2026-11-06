-- REVIEW ONLY. NOT EXECUTED. Proposed additive Kitchen migration.
-- Required approved session settings: aicos.approved_database and aicos.approved_service=kitchen.
-- Verify the existing unique kitchen_tickets(tenant_id, order_id) index in preflight; do not recreate it.
BEGIN;
SET LOCAL lock_timeout = '3s';
SET LOCAL statement_timeout = '30s';
DO $$
BEGIN
    IF current_database() IS DISTINCT FROM current_setting('aicos.approved_database', true)
       OR current_setting('aicos.approved_service', true) IS DISTINCT FROM 'kitchen' THEN
        RAISE EXCEPTION 'Explicit database/service approval settings are required';
    END IF;
    IF to_regclass('kitchen.kitchen_tickets') IS NULL THEN
        RAISE EXCEPTION 'Expected Kitchen baseline is absent';
    END IF;
    IF to_regclass('kitchen.order_admission_receipts') IS NOT NULL
       OR EXISTS (SELECT 1 FROM information_schema.columns
          WHERE table_schema = 'kitchen' AND table_name = 'kitchen_tickets' AND column_name = 'recovery_hold_reason') THEN
        RAISE EXCEPTION 'Proposed objects already exist; compare structure instead of recreating';
    END IF;
END $$;
ALTER TABLE kitchen.kitchen_tickets ADD COLUMN recovery_hold_reason varchar(200) NULL;
-- Nullable metadata-only expansion; queue/batch filtering must precede recovery backfill.
CREATE TABLE kitchen.order_admission_receipts (
    producer_key varchar(80) NOT NULL,
    consumer_key varchar(80) NOT NULL,
    event_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    order_id uuid NOT NULL,
    creation_sha256 bytea NOT NULL CHECK (octet_length(creation_sha256) = 32),
    ticket_id uuid NOT NULL REFERENCES kitchen.kitchen_tickets(id) ON DELETE RESTRICT,
    receipt_status varchar(20) NOT NULL CHECK (receipt_status IN ('APPLIED', 'NEEDS_REVIEW')),
    recorded_at_utc timestamptz NOT NULL,
    applied_at_utc timestamptz,
    last_verified_at_utc timestamptz NOT NULL,
    last_error_code varchar(100),
    PRIMARY KEY (producer_key, consumer_key, event_id),
    CONSTRAINT ck_kitchen_admission_receipt_applied CHECK (
        (receipt_status = 'APPLIED' AND applied_at_utc IS NOT NULL)
        OR (receipt_status = 'NEEDS_REVIEW' AND applied_at_utc IS NULL))
);
-- Different legacy event IDs can refer to one order: existing ticket unique key remains business dedupe.
CREATE INDEX ix_kitchen_admission_receipt_order
    ON kitchen.order_admission_receipts (tenant_id, order_id, consumer_key);
CREATE INDEX ix_kitchen_admission_receipt_scope
    ON kitchen.order_admission_receipts (tenant_id, branch_id, receipt_status);
COMMIT;
