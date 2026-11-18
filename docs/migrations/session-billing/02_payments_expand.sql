-- REVIEW ONLY. Separate deployment approval required. Never run against an unverified target.
-- EF-generated additive delta; existing migrations and business tables are unchanged.
START TRANSACTION;
SET LOCAL lock_timeout = '3s';
SET LOCAL statement_timeout = '30s';
DO $guard$
BEGIN
 IF current_setting('aicos.bill_expand_approved', true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Explicit expand approval required'; END IF;
 IF current_setting('aicos.expected_database', true) IS NULL OR current_database() <> current_setting('aicos.expected_database', true) THEN RAISE EXCEPTION 'Database identity mismatch'; END IF;
 IF current_setting('aicos.expected_schema', true) IS DISTINCT FROM 'payment' THEN RAISE EXCEPTION 'Service schema mismatch'; END IF;
 IF to_regclass('payment.session_bills') IS NOT NULL THEN RAISE EXCEPTION 'Existing payment.session_bills: compare schema/history; do not recreate'; END IF;
 IF to_regclass('payment.session_bill_orders') IS NOT NULL THEN RAISE EXCEPTION 'Existing payment.session_bill_orders: compare schema/history; do not recreate'; END IF;
 IF to_regclass('payment.session_bill_attempts') IS NOT NULL THEN RAISE EXCEPTION 'Existing payment.session_bill_attempts: compare schema/history; do not recreate'; END IF;
 IF to_regclass('payment.session_bill_events') IS NOT NULL THEN RAISE EXCEPTION 'Existing payment.session_bill_events: compare schema/history; do not recreate'; END IF;
 IF NOT EXISTS (SELECT 1 FROM payment.__ef_migrations_history WHERE migration_id='20261021102642_ConstraintsAndIndexes') THEN RAISE EXCEPTION 'Missing prerequisite migration 20261021102642_ConstraintsAndIndexes'; END IF;
END $guard$;


CREATE TABLE payment.session_bills (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    session_id uuid NOT NULL,
    fingerprint character varying(64) NOT NULL,
    quote_json jsonb NOT NULL,
    status character varying(16) NOT NULL,
    version bigint NOT NULL,
    total numeric(18,0) NOT NULL,
    created_by_user_id uuid NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    paid_payment_id uuid,
    paid_at_utc timestamp with time zone,
    closed_at_utc timestamp with time zone,
    ordering_acknowledged boolean NOT NULL,
    needs_reconciliation boolean NOT NULL,
    CONSTRAINT pk_session_bills PRIMARY KEY (id),
    CONSTRAINT ck_session_bill_status CHECK (status IN ('UNPAID','PAID','CLOSED')),
    CONSTRAINT ck_session_bill_total CHECK (total >= 0)
);

CREATE TABLE payment.session_bill_attempts (
    id uuid NOT NULL,
    bill_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    created_by_user_id uuid NOT NULL,
    key_hash character varying(64) NOT NULL,
    request_hash character varying(64) NOT NULL,
    method character varying(16) NOT NULL,
    provider character varying(32),
    status character varying(16) NOT NULL,
    transaction_ref character varying(64) NOT NULL,
    provider_reference character varying(128),
    checkout_url text,
    amount numeric(18,0) NOT NULL,
    tendered numeric(18,0),
    created_at_utc timestamp with time zone NOT NULL,
    paid_at_utc timestamp with time zone,
    CONSTRAINT pk_session_bill_attempts PRIMARY KEY (id),
    CONSTRAINT ck_bill_attempt_amount CHECK (amount > 0),
    CONSTRAINT ck_bill_attempt_status CHECK (status IN ('PENDING','SUCCEEDED','FAILED')),
    CONSTRAINT fk_session_bill_attempts_session_bills_bill_id FOREIGN KEY (bill_id) REFERENCES payment.session_bills (id) ON DELETE RESTRICT
);

CREATE TABLE payment.session_bill_events (
    id uuid NOT NULL,
    bill_id uuid NOT NULL,
    kind character varying(32) NOT NULL,
    payload jsonb NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    delivered_at_utc timestamp with time zone,
    next_attempt_at_utc timestamp with time zone NOT NULL,
    lease_owner uuid,
    lease_until_utc timestamp with time zone,
    fence bigint NOT NULL,
    CONSTRAINT pk_session_bill_events PRIMARY KEY (id),
    CONSTRAINT fk_session_bill_events_session_bills_bill_id FOREIGN KEY (bill_id) REFERENCES payment.session_bills (id) ON DELETE RESTRICT
);

CREATE TABLE payment.session_bill_orders (
    bill_id uuid NOT NULL,
    order_id uuid NOT NULL,
    amount numeric(18,0) NOT NULL,
    CONSTRAINT pk_session_bill_orders PRIMARY KEY (bill_id, order_id),
    CONSTRAINT fk_session_bill_orders_session_bills_bill_id FOREIGN KEY (bill_id) REFERENCES payment.session_bills (id) ON DELETE RESTRICT
);

CREATE UNIQUE INDEX ix_session_bill_attempts_tenant_id_key_hash ON payment.session_bill_attempts (tenant_id, key_hash);

CREATE UNIQUE INDEX ix_session_bill_attempts_transaction_ref ON payment.session_bill_attempts (transaction_ref);

CREATE UNIQUE INDEX uq_bill_pending_attempt ON payment.session_bill_attempts (bill_id) WHERE status = 'PENDING';

CREATE INDEX ix_session_bill_events_bill_id ON payment.session_bill_events (bill_id);

CREATE INDEX ix_session_bill_events_next_attempt_at_utc_id ON payment.session_bill_events (next_attempt_at_utc, id) WHERE kind = 'PAID' AND delivered_at_utc IS NULL;

CREATE UNIQUE INDEX ix_session_bills_tenant_id_session_id ON payment.session_bills (tenant_id, session_id);

INSERT INTO payment.__ef_migrations_history (migration_id, product_version)
VALUES ('20261113125804_PaymentSessionBills', '8.0.31');

COMMIT;
