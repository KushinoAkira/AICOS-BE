-- REVIEW ONLY. Main execution requires explicit approval. No auto migration or feature activation.
BEGIN;
SET LOCAL lock_timeout='3s';
SET LOCAL statement_timeout='60s';
DO $guard$ BEGIN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'ordering' THEN RAISE EXCEPTION 'Database/schema identity mismatch'; END IF;
IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
IF NOT (current_database()='aicos_ordering' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database() IN ('aicos_dashboard_test_ordering','aicos_dashboard_restore_ordering') AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside approved local/scratch scope'; END IF;
IF current_setting('aicos.dashboard_grants_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Separate stage approval required'; END IF;
IF ARRAY(SELECT migration_id::text FROM ordering.__ef_migrations_history ORDER BY migration_id) IS DISTINCT FROM ARRAY['20261020192345_PickupSlotSettings','20261021102649_ConstraintsAndIndexes','20261021145456_OrdersAndItems','20261024121125_PickupOrdersAndFeedback','20261024140713_OrderItemOptions','20261024151542_TableSessions','20261024160203_NoShowUnfulfillableRemake','20261101173630_TableBillRequest','20261106164220_KitchenAdmissionOutbox','20261106183340_OrderingCupFulfillmentFoundation','20261113125751_OrderingSessionCheckout','20261113151937_FeedbackReply','20261114164841_DurableTableTransfer']::text[] THEN RAISE EXCEPTION 'Migration history changed; inspect before proceeding'; END IF;
END $guard$;
GRANT USAGE ON SCHEMA ordering TO ordering_svc;
GRANT SELECT,INSERT ON ordering.kitchen_admission_outbox,ordering.kitchen_admission_reconcile_state,ordering.fulfillment_receipts,ordering.order_fulfillment_projection,ordering.session_checkouts,ordering.table_transfer_operations TO ordering_svc;
GRANT UPDATE(state,next_attempt_at_utc,attempt_count,lease_token,lease_until_utc,acknowledged_at_utc,acknowledged_ticket_id,last_error_code) ON ordering.kitchen_admission_outbox TO ordering_svc;
GRANT UPDATE(cycle_id,upper_order_id,last_order_id,cycle_started_at_utc,last_completed_at_utc,lease_token,lease_until_utc) ON ordering.kitchen_admission_reconcile_state TO ordering_svc;
GRANT UPDATE(source_version,snapshot_hash,total,served,review,snapshot_json,fulfilled_at_utc,last_verified_at_utc) ON ordering.order_fulfillment_projection TO ordering_svc;
GRANT UPDATE(state,payment_id) ON ordering.session_checkouts TO ordering_svc;
GRANT UPDATE(synced_at_utc) ON ordering.table_transfer_operations TO ordering_svc;
GRANT SELECT(fulfillment_mode),UPDATE(fulfillment_mode) ON ordering.orders TO ordering_svc;
GRANT SELECT(replied_at_utc,replied_by_user_id,reply),UPDATE(replied_at_utc,replied_by_user_id,reply) ON ordering.order_feedbacks TO ordering_svc;
COMMIT;
