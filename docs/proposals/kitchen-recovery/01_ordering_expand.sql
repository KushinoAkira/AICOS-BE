-- REVIEW ONLY. NOT EXECUTED. Proposed additive Ordering migration.
-- Required approved session settings: aicos.approved_database and aicos.approved_service=ordering.
-- No backfill, permission or rollback operations are included.
BEGIN;
SET LOCAL lock_timeout = '3s';
SET LOCAL statement_timeout = '30s';
DO $$
BEGIN
    IF current_database() IS DISTINCT FROM current_setting('aicos.approved_database', true)
       OR current_setting('aicos.approved_service', true) IS DISTINCT FROM 'ordering' THEN
        RAISE EXCEPTION 'Explicit database/service approval settings are required';
    END IF;
    IF to_regclass('ordering.orders') IS NULL THEN
        RAISE EXCEPTION 'Expected Ordering baseline is absent';
    END IF;
    IF to_regclass('ordering.kitchen_admission_outbox') IS NOT NULL
       OR to_regclass('ordering.kitchen_admission_reconcile_state') IS NOT NULL THEN
        RAISE EXCEPTION 'Proposed objects already exist; compare structure instead of recreating';
    END IF;
END $$;
CREATE TABLE ordering.kitchen_admission_outbox (
    event_id uuid PRIMARY KEY, -- application-generated once; stable across retries
    tenant_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    order_id uuid NOT NULL,
    consumer_key varchar(80) NOT NULL,
    schema_version integer NOT NULL DEFAULT 1 CHECK (schema_version > 0),
    creation_payload jsonb NOT NULL CHECK (jsonb_typeof(creation_payload) = 'object'),
    creation_sha256 bytea NOT NULL CHECK (octet_length(creation_sha256) = 32),
    origin varchar(20) NOT NULL CHECK (origin IN ('CAPTURE', 'RECONCILE')),
    state varchar(20) NOT NULL DEFAULT 'PENDING'
        CHECK (state IN ('PENDING', 'IN_FLIGHT', 'ACKED', 'NEEDS_REVIEW')),
    created_at_utc timestamptz NOT NULL,
    next_attempt_at_utc timestamptz NOT NULL,
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    lease_token uuid,
    lease_until_utc timestamptz,
    acknowledged_at_utc timestamptz,
    acknowledged_ticket_id uuid, -- remote ID: no cross-service FK
    last_error_code varchar(100), -- safe classification only
    CONSTRAINT uq_kitchen_admission_order UNIQUE (tenant_id, order_id, consumer_key),
    CONSTRAINT ck_kitchen_admission_lease CHECK (
        (state = 'IN_FLIGHT' AND lease_token IS NOT NULL AND lease_until_utc IS NOT NULL)
        OR (state <> 'IN_FLIGHT' AND lease_token IS NULL AND lease_until_utc IS NULL)),
    CONSTRAINT ck_kitchen_admission_ack CHECK (
        (state = 'ACKED' AND acknowledged_at_utc IS NOT NULL AND acknowledged_ticket_id IS NOT NULL)
        OR (state <> 'ACKED' AND acknowledged_at_utc IS NULL AND acknowledged_ticket_id IS NULL))
);
-- Logical Order reference validated locally; do not implicitly change Order deletion policy.
CREATE INDEX ix_kitchen_admission_due
    ON ordering.kitchen_admission_outbox (next_attempt_at_utc, lease_until_utc, event_id)
    WHERE state IN ('PENDING', 'IN_FLIGHT');
CREATE INDEX ix_kitchen_admission_scope_status
    ON ordering.kitchen_admission_outbox (tenant_id, branch_id, state, created_at_utc);
CREATE TABLE ordering.kitchen_admission_reconcile_state (
    consumer_key varchar(80) PRIMARY KEY,
    cycle_id uuid NOT NULL,
    upper_order_id uuid,
    last_order_id uuid,
    cycle_started_at_utc timestamptz NOT NULL,
    last_completed_at_utc timestamptz,
    lease_token uuid,
    lease_until_utc timestamptz,
    CONSTRAINT ck_kitchen_reconcile_lease CHECK (
        (lease_token IS NULL AND lease_until_utc IS NULL)
        OR (lease_token IS NOT NULL AND lease_until_utc IS NOT NULL)),
    CONSTRAINT ck_kitchen_reconcile_cursor CHECK (
        last_order_id IS NULL OR (upper_order_id IS NOT NULL AND last_order_id <= upper_order_id))
);
COMMIT;
