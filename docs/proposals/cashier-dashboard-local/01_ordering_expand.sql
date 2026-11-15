-- REVIEW ONLY. Main execution requires explicit approval. No auto migration or feature activation.
BEGIN;
SET LOCAL lock_timeout='3s';
SET LOCAL statement_timeout='60s';
DO $guard$ BEGIN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'ordering' THEN RAISE EXCEPTION 'Database/schema identity mismatch'; END IF;
IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
IF NOT (current_database()='aicos_ordering' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database() IN ('aicos_dashboard_test_ordering','aicos_dashboard_restore_ordering') AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside approved local/scratch scope'; END IF;
IF current_setting('aicos.dashboard_expand_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Separate stage approval required'; END IF;
IF ARRAY(SELECT migration_id::text FROM ordering.__ef_migrations_history ORDER BY migration_id) IS DISTINCT FROM ARRAY['20261020192345_PickupSlotSettings','20261021102649_ConstraintsAndIndexes','20261021145456_OrdersAndItems','20261024121125_PickupOrdersAndFeedback','20261024140713_OrderItemOptions','20261024151542_TableSessions','20261024160203_NoShowUnfulfillableRemake','20261101173630_TableBillRequest']::text[] THEN RAISE EXCEPTION 'Migration history changed; inspect before proceeding'; END IF;
IF to_regclass('ordering.fulfillment_receipts') IS NOT NULL THEN RAISE EXCEPTION 'Existing target table requires review'; END IF;
IF to_regclass('ordering.kitchen_admission_outbox') IS NOT NULL THEN RAISE EXCEPTION 'Existing target table requires review'; END IF;
IF to_regclass('ordering.kitchen_admission_reconcile_state') IS NOT NULL THEN RAISE EXCEPTION 'Existing target table requires review'; END IF;
IF to_regclass('ordering.order_fulfillment_projection') IS NOT NULL THEN RAISE EXCEPTION 'Existing target table requires review'; END IF;
IF to_regclass('ordering.session_checkouts') IS NOT NULL THEN RAISE EXCEPTION 'Existing target table requires review'; END IF;
IF to_regclass('ordering.table_transfer_operations') IS NOT NULL THEN RAISE EXCEPTION 'Existing target table requires review'; END IF;
IF EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ordering' AND table_name='order_feedbacks' AND column_name='replied_at_utc') THEN RAISE EXCEPTION 'Existing target column requires review'; END IF;
IF EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ordering' AND table_name='order_feedbacks' AND column_name='replied_by_user_id') THEN RAISE EXCEPTION 'Existing target column requires review'; END IF;
IF EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ordering' AND table_name='order_feedbacks' AND column_name='reply') THEN RAISE EXCEPTION 'Existing target column requires review'; END IF;
IF EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ordering' AND table_name='orders' AND column_name='fulfillment_mode') THEN RAISE EXCEPTION 'Existing target column requires review'; END IF;
END $guard$;
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

CREATE INDEX ix_kitchen_admission_due ON ordering.kitchen_admission_outbox (next_attempt_at_utc, lease_until_utc, event_id) WHERE state IN ('PENDING','IN_FLIGHT');

CREATE INDEX ix_kitchen_admission_scope_status ON ordering.kitchen_admission_outbox (tenant_id, branch_id, state, created_at_utc);

CREATE UNIQUE INDEX uq_kitchen_admission_order ON ordering.kitchen_admission_outbox (tenant_id, order_id, consumer_key);

INSERT INTO ordering.__ef_migrations_history (migration_id, product_version)
VALUES ('20261106164220_KitchenAdmissionOutbox', '8.0.31');

ALTER TABLE ordering.orders ADD fulfillment_mode character varying(20) NOT NULL DEFAULT 'WholeOrder';

CREATE TABLE ordering.fulfillment_receipts (
    event_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    order_id uuid NOT NULL,
    source_version bigint NOT NULL,
    payload_hash character varying(64) NOT NULL,
    result character varying(20) NOT NULL,
    applied_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_fulfillment_receipts PRIMARY KEY (event_id),
    CONSTRAINT ck_fulfillment_receipt_payload CHECK (source_version > 0 AND length(payload_hash) = 64 AND result IN ('Applied','Duplicate','Superseded')),
    CONSTRAINT fk_fulfillment_receipts_orders_order_id FOREIGN KEY (order_id) REFERENCES ordering.orders (id) ON DELETE RESTRICT
);

CREATE TABLE ordering.order_fulfillment_projection (
    order_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    ticket_id uuid NOT NULL,
    manifest_hash character varying(64) NOT NULL,
    source_version bigint NOT NULL,
    snapshot_hash character varying(64) NOT NULL,
    total integer NOT NULL,
    served integer NOT NULL,
    review integer NOT NULL,
    snapshot_json jsonb NOT NULL,
    fulfilled_at_utc timestamp with time zone,
    last_verified_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_order_fulfillment_projection PRIMARY KEY (order_id),
    CONSTRAINT ck_order_fulfillment_completed CHECK ((served = total) = (fulfilled_at_utc IS NOT NULL)),
    CONSTRAINT ck_order_fulfillment_hash CHECK (length(manifest_hash) = 64 AND length(snapshot_hash) = 64 AND jsonb_typeof(snapshot_json) = 'object'),
    CONSTRAINT ck_order_fulfillment_summary CHECK (source_version > 0 AND total BETWEEN 1 AND 4950 AND served BETWEEN 0 AND total AND review BETWEEN 0 AND total - served),
    CONSTRAINT fk_order_fulfillment_projection_orders_order_id FOREIGN KEY (order_id) REFERENCES ordering.orders (id) ON DELETE RESTRICT
);

ALTER TABLE ordering.orders ADD CONSTRAINT ck_order_fulfillment_mode CHECK (fulfillment_mode IN ('WholeOrder','CupV1'));

CREATE INDEX ix_fulfillment_receipts_order_id ON ordering.fulfillment_receipts (order_id);

CREATE INDEX ix_fulfillment_receipts_tenant_id_order_id_source_version ON ordering.fulfillment_receipts (tenant_id, order_id, source_version);

CREATE INDEX ix_order_fulfillment_projection_tenant_id_branch_id_last_verif ON ordering.order_fulfillment_projection (tenant_id, branch_id, last_verified_at_utc);

INSERT INTO ordering.__ef_migrations_history (migration_id, product_version)
VALUES ('20261106183340_OrderingCupFulfillmentFoundation', '8.0.31');

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

ALTER TABLE ordering.order_feedbacks ADD replied_at_utc timestamp with time zone;

ALTER TABLE ordering.order_feedbacks ADD replied_by_user_id uuid;

ALTER TABLE ordering.order_feedbacks ADD reply character varying(500);

INSERT INTO ordering.__ef_migrations_history (migration_id, product_version)
VALUES ('20261113151937_FeedbackReply', '8.0.31');

CREATE TABLE ordering.table_transfer_operations (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    session_id uuid NOT NULL,
    actor_id uuid NOT NULL,
    source_table_id uuid NOT NULL,
    target_table_id uuid NOT NULL,
    target_code character varying(20) NOT NULL,
    version bigint NOT NULL,
    request_hash character varying(64) NOT NULL,
    order_ids_json jsonb NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    synced_at_utc timestamp with time zone,
    CONSTRAINT pk_table_transfer_operations PRIMARY KEY (id),
    CONSTRAINT fk_table_transfer_operations_table_sessions_session_id FOREIGN KEY (session_id) REFERENCES ordering.table_sessions (id) ON DELETE RESTRICT
);

CREATE INDEX ix_table_transfer_operations_created_at_utc ON ordering.table_transfer_operations (created_at_utc) WHERE synced_at_utc IS NULL;

CREATE INDEX ix_table_transfer_operations_session_id ON ordering.table_transfer_operations (session_id);

CREATE UNIQUE INDEX ix_table_transfer_operations_tenant_id_session_id_version ON ordering.table_transfer_operations (tenant_id, session_id, version);

INSERT INTO ordering.__ef_migrations_history (migration_id, product_version)
VALUES ('20261114164841_DurableTableTransfer', '8.0.31');
COMMIT;
