-- REVIEW ONLY. Main execution requires explicit approval. No auto migration or feature activation.
BEGIN;
SET LOCAL lock_timeout='3s';
SET LOCAL statement_timeout='60s';
DO $guard$ BEGIN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'payment' THEN RAISE EXCEPTION 'Database/schema identity mismatch'; END IF;
IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
IF NOT (current_database()='aicos_payment' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database() IN ('aicos_dashboard_test_payment','aicos_dashboard_restore_payment') AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside approved local/scratch scope'; END IF;
IF current_setting('aicos.dashboard_expand_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Separate stage approval required'; END IF;
IF ARRAY(SELECT migration_id::text FROM payment.__ef_migrations_history ORDER BY migration_id) IS DISTINCT FROM ARRAY['20261020180751_AddPaymentProviders','20261020192200_CancellationPolicyAndMerchantSettings','20261021094418_PaymentsRefundsWebhooks','20261021102642_ConstraintsAndIndexes']::text[] THEN RAISE EXCEPTION 'Migration history changed; inspect before proceeding'; END IF;
IF to_regclass('payment.bill_split_operations') IS NOT NULL THEN RAISE EXCEPTION 'Existing target table requires review'; END IF;
IF to_regclass('payment.bill_split_plans') IS NOT NULL THEN RAISE EXCEPTION 'Existing target table requires review'; END IF;
IF to_regclass('payment.cashier_shifts') IS NOT NULL THEN RAISE EXCEPTION 'Existing target table requires review'; END IF;
IF to_regclass('payment.collection_outbox') IS NOT NULL THEN RAISE EXCEPTION 'Existing target table requires review'; END IF;
IF to_regclass('payment.session_bill_attempts') IS NOT NULL THEN RAISE EXCEPTION 'Existing target table requires review'; END IF;
IF to_regclass('payment.session_bill_events') IS NOT NULL THEN RAISE EXCEPTION 'Existing target table requires review'; END IF;
IF to_regclass('payment.session_bill_orders') IS NOT NULL THEN RAISE EXCEPTION 'Existing target table requires review'; END IF;
IF to_regclass('payment.session_bills') IS NOT NULL THEN RAISE EXCEPTION 'Existing target table requires review'; END IF;
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

CREATE TABLE payment.cashier_shifts (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    cashier_id uuid NOT NULL,
    open_key character varying(64) NOT NULL,
    opening_cash numeric(18,0) NOT NULL,
    note character varying(500),
    opened_at_utc timestamp with time zone NOT NULL,
    closed_at_utc timestamp with time zone,
    counted_cash numeric(18,0),
    expected_cash numeric(18,0),
    closing_note character varying(500),
    version bigint NOT NULL,
    CONSTRAINT pk_cashier_shifts PRIMARY KEY (id),
    CONSTRAINT ck_cashier_shift_money CHECK (opening_cash >= 0 AND (counted_cash IS NULL OR counted_cash >= 0))
);

CREATE TABLE payment.collection_outbox (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    cashier_id uuid,
    shift_id uuid,
    amount numeric(18,0) NOT NULL,
    method character varying(20) NOT NULL,
    payload jsonb NOT NULL,
    paid_at_utc timestamp with time zone NOT NULL,
    delivered_at_utc timestamp with time zone,
    CONSTRAINT pk_collection_outbox PRIMARY KEY (id),
    CONSTRAINT ck_collection_amount CHECK (amount > 0),
    CONSTRAINT fk_collection_outbox_cashier_shifts_shift_id FOREIGN KEY (shift_id) REFERENCES payment.cashier_shifts (id) ON DELETE RESTRICT
);

CREATE UNIQUE INDEX ix_cashier_shifts_tenant_id_cashier_id ON payment.cashier_shifts (tenant_id, cashier_id) WHERE closed_at_utc IS NULL;

CREATE UNIQUE INDEX ix_cashier_shifts_tenant_id_cashier_id_open_key ON payment.cashier_shifts (tenant_id, cashier_id, open_key);

CREATE INDEX ix_collection_outbox_paid_at_utc ON payment.collection_outbox (paid_at_utc) WHERE delivered_at_utc IS NULL;

CREATE INDEX ix_collection_outbox_shift_id ON payment.collection_outbox (shift_id);

INSERT INTO payment.__ef_migrations_history (migration_id, product_version)
VALUES ('20261114125030_CashierMoneyShifts', '8.0.31');

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

CREATE INDEX ix_bill_split_operations_plan_id ON payment.bill_split_operations (plan_id);

CREATE UNIQUE INDEX ix_bill_split_plans_tenant_id_session_id ON payment.bill_split_plans (tenant_id, session_id);

INSERT INTO payment.__ef_migrations_history (migration_id, product_version)
VALUES ('20261114164856_BillSplitPlans', '8.0.31');
COMMIT;
