-- DESIGN PROTOTYPE ONLY. Not an approved EF/operating migration.
DO $$ BEGIN
 IF current_database() <> 'aicos_item_transfer_design_ordering' OR current_setting('aicos.design_approved', true) IS DISTINCT FROM 'true'
 OR current_setting('aicos.expected_cluster', true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system())
 THEN RAISE EXCEPTION 'Isolated design target/approval guard failed'; END IF;
END $$;

BEGIN;
CREATE TABLE ordering.item_transfer_operations (
 id uuid PRIMARY KEY, tenant_id uuid NOT NULL, branch_id uuid NOT NULL, actor_id uuid NOT NULL,
 request_key varchar(80) NOT NULL CHECK (length(request_key) BETWEEN 8 AND 80), request_hash varchar(64) NOT NULL CHECK(length(request_hash)=64),
 source_session_id uuid NOT NULL REFERENCES ordering.table_sessions(id) ON DELETE RESTRICT,
 target_session_id uuid NOT NULL REFERENCES ordering.table_sessions(id) ON DELETE RESTRICT,
 unit_ids jsonb NOT NULL CHECK(jsonb_typeof(unit_ids)='array' AND jsonb_array_length(unit_ids) BETWEEN 1 AND 100),
 amount numeric(18,0) NOT NULL CHECK(amount>0),
 state varchar(24) NOT NULL CHECK(state IN ('PREPARING','PREPARED','COMMIT_DECIDED','COMPLETED','ABORTING','CANCELLED','REVIEW')),
 decision_version bigint NOT NULL DEFAULT 0 CHECK(decision_version>=0),
 created_at_utc timestamptz NOT NULL, next_attempt_at_utc timestamptz NOT NULL,
 lease_token uuid, lease_fence bigint NOT NULL DEFAULT 0 CHECK(lease_fence>=0), lease_until_utc timestamptz,
 audit jsonb NOT NULL DEFAULT '[]' CHECK(jsonb_typeof(audit)='array'),
 UNIQUE(tenant_id,actor_id,request_key), CHECK(source_session_id<>target_session_id)
);
CREATE INDEX ix_item_transfer_retry ON ordering.item_transfer_operations(next_attempt_at_utc,id)
 WHERE state NOT IN ('COMPLETED','CANCELLED');
CREATE TABLE ordering.unit_session_allocations (
 tenant_id uuid NOT NULL, cup_id uuid NOT NULL, branch_id uuid NOT NULL,
 source_order_id uuid NOT NULL REFERENCES ordering.orders(id) ON DELETE RESTRICT,
 source_order_item_id uuid NOT NULL REFERENCES ordering.order_items(id) ON DELETE RESTRICT,
 unit_index integer NOT NULL CHECK(unit_index>0),
 origin_session_id uuid NOT NULL REFERENCES ordering.table_sessions(id) ON DELETE RESTRICT,
 current_session_id uuid NOT NULL REFERENCES ordering.table_sessions(id) ON DELETE RESTRICT,
 unit_amount numeric(18,0) NOT NULL CHECK(unit_amount>0), allocation_version bigint NOT NULL DEFAULT 1 CHECK(allocation_version>0),
 service_status varchar(16) NOT NULL DEFAULT 'Unknown' CHECK(service_status IN ('Unknown','Waiting','Available','Reserved','Carrying','Served','Review')),
 fulfillment_version bigint NOT NULL DEFAULT 0 CHECK(fulfillment_version>=0),
 fulfillment_route_version bigint NOT NULL DEFAULT 0 CHECK(fulfillment_route_version>=0), served_at_utc timestamptz,
 CHECK((service_status='Served') = (served_at_utc IS NOT NULL)),
 active_transfer_id uuid REFERENCES ordering.item_transfer_operations(id) ON DELETE RESTRICT,
 settled_bill_id uuid, settled_payment_id uuid, settlement_hash varchar(64),
 PRIMARY KEY(tenant_id,cup_id), UNIQUE(tenant_id,source_order_item_id,unit_index),
 CHECK((settled_bill_id IS NULL AND settled_payment_id IS NULL AND settlement_hash IS NULL) OR
 (settled_bill_id IS NOT NULL AND settled_payment_id IS NOT NULL AND settlement_hash IS NOT NULL AND length(settlement_hash)=64))
);
CREATE INDEX ix_unit_current_session ON ordering.unit_session_allocations(tenant_id,current_session_id);
CREATE TABLE ordering.unit_settlement_receipts (
 tenant_id uuid NOT NULL, bill_id uuid NOT NULL, payment_id uuid NOT NULL, payload_hash varchar(64) NOT NULL CHECK(length(payload_hash)=64),
 allocation_manifest_hash varchar(64) NOT NULL CHECK(length(allocation_manifest_hash)=64), recorded_at_utc timestamptz NOT NULL,
 PRIMARY KEY(tenant_id,bill_id), UNIQUE(tenant_id,payment_id)
);
COMMIT;
