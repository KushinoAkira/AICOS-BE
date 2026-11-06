START TRANSACTION;

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

COMMIT;

