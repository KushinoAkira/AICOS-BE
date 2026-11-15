-- DESIGN PROTOTYPE ONLY. Not an approved EF/operating migration.
DO $$ BEGIN
 IF current_database() <> 'aicos_item_transfer_design_kitchen' OR current_setting('aicos.design_approved', true) IS DISTINCT FROM 'true'
 OR current_setting('aicos.expected_cluster', true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system())
 THEN RAISE EXCEPTION 'Isolated design target/approval guard failed'; END IF;
END $$;

BEGIN;
CREATE TABLE kitchen.cup_transfer_receipts (
 operation_id uuid PRIMARY KEY, tenant_id uuid NOT NULL, branch_id uuid NOT NULL, actor_id uuid NOT NULL,
 payload_hash varchar(64) NOT NULL CHECK(length(payload_hash)=64), source_session_id uuid NOT NULL, target_session_id uuid NOT NULL,
 unit_ids jsonb NOT NULL CHECK(jsonb_typeof(unit_ids)='array' AND jsonb_array_length(unit_ids) BETWEEN 1 AND 100),
 state varchar(16) NOT NULL CHECK(state IN ('PREPARED','FINALIZED','ABORTED','REVIEW')),
 decision_version bigint NOT NULL DEFAULT 0 CHECK(decision_version>=0), recorded_at_utc timestamptz NOT NULL,
 audit jsonb NOT NULL DEFAULT '[]' CHECK(jsonb_typeof(audit)='array'), CHECK(source_session_id<>target_session_id)
);
CREATE TABLE kitchen.cup_delivery_routes (
 cup_id uuid PRIMARY KEY REFERENCES kitchen.cup_tasks(id) ON DELETE RESTRICT, tenant_id uuid NOT NULL, branch_id uuid NOT NULL,
 current_session_id uuid NOT NULL, current_table_id uuid NOT NULL, current_table_code varchar(20) NOT NULL CHECK(length(current_table_code)>0),
 route_version bigint NOT NULL DEFAULT 1 CHECK(route_version>0),
 transfer_operation_id uuid REFERENCES kitchen.cup_transfer_receipts(operation_id) ON DELETE RESTRICT,
 last_finalized_operation_id uuid REFERENCES kitchen.cup_transfer_receipts(operation_id) ON DELETE RESTRICT
);
CREATE INDEX ix_cup_current_session ON kitchen.cup_delivery_routes(tenant_id,current_session_id);
COMMIT;
