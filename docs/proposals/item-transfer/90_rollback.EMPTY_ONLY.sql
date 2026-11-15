-- Disposable design target only. No CASCADE; refuses persisted financial/transfer audit.
DO $$ BEGIN
 IF current_database() NOT IN ('aicos_item_transfer_design_ordering','aicos_item_transfer_design_kitchen','aicos_item_transfer_design_payment')
 OR current_setting('aicos.design_approved',true) IS DISTINCT FROM 'true'
 OR current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system())
 THEN RAISE EXCEPTION 'Isolated design target/approval guard failed'; END IF;
END $$;

BEGIN;
DO $$ BEGIN
 IF current_database()='aicos_item_transfer_design_ordering' THEN
  LOCK TABLE ordering.unit_session_allocations,ordering.item_transfer_operations,ordering.unit_settlement_receipts IN ACCESS EXCLUSIVE MODE;
  IF EXISTS(SELECT 1 FROM ordering.unit_session_allocations) OR EXISTS(SELECT 1 FROM ordering.item_transfer_operations) OR EXISTS(SELECT 1 FROM ordering.unit_settlement_receipts) THEN RAISE EXCEPTION 'Nonempty design rollback refused'; END IF;
  DROP TABLE ordering.unit_session_allocations,ordering.unit_settlement_receipts,ordering.item_transfer_operations;
 ELSIF current_database()='aicos_item_transfer_design_kitchen' THEN
  LOCK TABLE kitchen.cup_delivery_routes,kitchen.cup_transfer_receipts IN ACCESS EXCLUSIVE MODE;
  IF EXISTS(SELECT 1 FROM kitchen.cup_delivery_routes) OR EXISTS(SELECT 1 FROM kitchen.cup_transfer_receipts) THEN RAISE EXCEPTION 'Nonempty design rollback refused'; END IF;
  DROP TABLE kitchen.cup_delivery_routes,kitchen.cup_transfer_receipts;
 ELSIF current_database()='aicos_item_transfer_design_payment' THEN
  LOCK TABLE payment.session_bill_units IN ACCESS EXCLUSIVE MODE;
  IF EXISTS(SELECT 1 FROM payment.session_bill_units) THEN RAISE EXCEPTION 'Nonempty design rollback refused'; END IF;
  DROP TABLE payment.session_bill_units;
 END IF;
END $$;
COMMIT;
