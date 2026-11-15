-- REVIEW ONLY. Separate deployment approval required. Never run against an unverified target.
-- EF-generated additive delta; existing migrations and business tables are unchanged.
START TRANSACTION;
SET LOCAL lock_timeout = '3s';
SET LOCAL statement_timeout = '30s';
DO $guard$
BEGIN
 IF current_setting('aicos.bill_expand_approved', true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Explicit expand approval required'; END IF;
 IF current_setting('aicos.expected_database', true) IS NULL OR current_database() <> current_setting('aicos.expected_database', true) THEN RAISE EXCEPTION 'Database identity mismatch'; END IF;
 IF current_setting('aicos.expected_schema', true) IS DISTINCT FROM 'ordering' THEN RAISE EXCEPTION 'Service schema mismatch'; END IF;
 IF to_regclass('ordering.session_checkouts') IS NOT NULL THEN RAISE EXCEPTION 'Existing ordering.session_checkouts: compare schema/history; do not recreate'; END IF;
 IF NOT EXISTS (SELECT 1 FROM ordering.__ef_migrations_history WHERE migration_id='20261106183340_OrderingCupFulfillmentFoundation') THEN RAISE EXCEPTION 'Missing prerequisite migration 20261106183340_OrderingCupFulfillmentFoundation'; END IF;
END $guard$;


CREATE TABLE ordering.session_checkouts (
    session_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    bill_id uuid NOT NULL,
    fingerprint character varying(64) NOT NULL,
    quote_json jsonb NOT NULL,
    state character varying(16) NOT NULL,
    payment_id uuid,
    created_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_session_checkouts PRIMARY KEY (session_id),
    CONSTRAINT ck_session_checkout_state CHECK (state IN ('Frozen','Paid','Closed'))
);

CREATE UNIQUE INDEX ix_session_checkouts_tenant_id_bill_id ON ordering.session_checkouts (tenant_id, bill_id);

INSERT INTO ordering.__ef_migrations_history (migration_id, product_version)
VALUES ('20261113125751_OrderingSessionCheckout', '8.0.31');

COMMIT;
