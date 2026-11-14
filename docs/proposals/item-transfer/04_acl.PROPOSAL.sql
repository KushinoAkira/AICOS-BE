-- Test role names only. No operating role creation, password or PUBLIC REVOKE.
DO $$ BEGIN
 IF current_database() NOT IN ('aicos_item_transfer_design_ordering','aicos_item_transfer_design_kitchen','aicos_item_transfer_design_payment')
 OR current_setting('aicos.design_approved',true) IS DISTINCT FROM 'true'
 OR current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system())
 THEN RAISE EXCEPTION 'Isolated design target/approval guard failed'; END IF;
END $$;

DO $$ BEGIN
 IF current_database()='aicos_item_transfer_design_ordering' THEN
  GRANT USAGE ON SCHEMA ordering TO design_ordering_svc;
  GRANT SELECT ON ordering.orders,ordering.order_items,ordering.table_sessions,ordering.session_checkouts TO design_ordering_svc;
  GRANT SELECT,INSERT ON ordering.unit_session_allocations,ordering.item_transfer_operations,ordering.unit_settlement_receipts TO design_ordering_svc;
  GRANT UPDATE(current_session_id,allocation_version,active_transfer_id,settled_bill_id,settled_payment_id,settlement_hash,service_status,fulfillment_version,fulfillment_route_version,served_at_utc) ON ordering.unit_session_allocations TO design_ordering_svc;
  GRANT UPDATE(state,decision_version,next_attempt_at_utc,lease_token,lease_fence,lease_until_utc,audit) ON ordering.item_transfer_operations TO design_ordering_svc;
 ELSIF current_database()='aicos_item_transfer_design_kitchen' THEN
  GRANT USAGE ON SCHEMA kitchen TO design_kitchen_svc;
  GRANT SELECT ON kitchen.cup_tasks,kitchen.cup_claims,kitchen.kitchen_tickets TO design_kitchen_svc;
  GRANT SELECT,INSERT ON kitchen.cup_delivery_routes,kitchen.cup_transfer_receipts TO design_kitchen_svc;
  GRANT UPDATE(current_session_id,current_table_id,current_table_code,route_version,transfer_operation_id,last_finalized_operation_id) ON kitchen.cup_delivery_routes TO design_kitchen_svc;
  GRANT UPDATE(state,decision_version,audit) ON kitchen.cup_transfer_receipts TO design_kitchen_svc;
 ELSIF current_database()='aicos_item_transfer_design_payment' THEN
  GRANT USAGE ON SCHEMA payment TO design_payment_svc;
  GRANT SELECT ON payment.session_bills TO design_payment_svc;
  GRANT SELECT,INSERT ON payment.session_bill_units TO design_payment_svc;
 END IF;
END $$;
