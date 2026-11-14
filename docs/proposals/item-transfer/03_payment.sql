-- DESIGN PROTOTYPE ONLY. Not an approved EF/operating migration.
DO $$ BEGIN
 IF current_database() <> 'aicos_item_transfer_design_payment' OR current_setting('aicos.design_approved', true) IS DISTINCT FROM 'true'
 OR current_setting('aicos.expected_cluster', true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system())
 THEN RAISE EXCEPTION 'Isolated design target/approval guard failed'; END IF;
END $$;

BEGIN;
CREATE TABLE payment.session_bill_units (
 tenant_id uuid NOT NULL, cup_id uuid NOT NULL,
 bill_id uuid NOT NULL REFERENCES payment.session_bills(id) ON DELETE RESTRICT,
 source_order_id uuid NOT NULL, source_order_item_id uuid NOT NULL, unit_index integer NOT NULL CHECK(unit_index>0),
 allocation_version bigint NOT NULL CHECK(allocation_version>0), unit_amount numeric(18,0) NOT NULL CHECK(unit_amount>0),
 PRIMARY KEY(tenant_id,cup_id), UNIQUE(tenant_id,source_order_item_id,unit_index)
);
CREATE INDEX ix_bill_unit_manifest ON payment.session_bill_units(bill_id,cup_id);
COMMIT;
