-- REVIEW ONLY. Main execution requires explicit approval. No auto migration or feature activation.
BEGIN;
SET LOCAL lock_timeout='3s';
SET LOCAL statement_timeout='60s';
DO $guard$ BEGIN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'ordering' THEN RAISE EXCEPTION 'Database/schema identity mismatch'; END IF;
IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
IF NOT (current_database()='aicos_ordering' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database() IN ('aicos_dashboard_test_ordering','aicos_dashboard_restore_ordering') AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside approved local/scratch scope'; END IF;
IF current_setting('aicos.dashboard_rollback_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Separate stage approval required'; END IF;
IF ARRAY(SELECT migration_id::text FROM ordering.__ef_migrations_history ORDER BY migration_id) IS DISTINCT FROM ARRAY['20261020192345_PickupSlotSettings','20261021102649_ConstraintsAndIndexes','20261021145456_OrdersAndItems','20261024121125_PickupOrdersAndFeedback','20261024140713_OrderItemOptions','20261024151542_TableSessions','20261024160203_NoShowUnfulfillableRemake','20261101173630_TableBillRequest','20261106164220_KitchenAdmissionOutbox','20261106183340_OrderingCupFulfillmentFoundation','20261113125751_OrderingSessionCheckout','20261113151937_FeedbackReply','20261114164841_DurableTableTransfer']::text[] THEN RAISE EXCEPTION 'Migration history changed; inspect before proceeding'; END IF;
LOCK TABLE ordering.fulfillment_receipts,ordering.kitchen_admission_outbox,ordering.kitchen_admission_reconcile_state,ordering.order_fulfillment_projection,ordering.session_checkouts,ordering.table_transfer_operations,ordering.order_feedbacks,ordering.orders IN ACCESS EXCLUSIVE MODE;
IF EXISTS(SELECT 1 FROM ordering.fulfillment_receipts) THEN RAISE EXCEPTION 'New data/audit exists; retain schema and repair forward'; END IF;
IF EXISTS(SELECT 1 FROM ordering.kitchen_admission_outbox) THEN RAISE EXCEPTION 'New data/audit exists; retain schema and repair forward'; END IF;
IF EXISTS(SELECT 1 FROM ordering.kitchen_admission_reconcile_state) THEN RAISE EXCEPTION 'New data/audit exists; retain schema and repair forward'; END IF;
IF EXISTS(SELECT 1 FROM ordering.order_fulfillment_projection) THEN RAISE EXCEPTION 'New data/audit exists; retain schema and repair forward'; END IF;
IF EXISTS(SELECT 1 FROM ordering.session_checkouts) THEN RAISE EXCEPTION 'New data/audit exists; retain schema and repair forward'; END IF;
IF EXISTS(SELECT 1 FROM ordering.table_transfer_operations) THEN RAISE EXCEPTION 'New data/audit exists; retain schema and repair forward'; END IF;
IF EXISTS(SELECT 1 FROM ordering.orders WHERE fulfillment_mode<>'WholeOrder') OR EXISTS(SELECT 1 FROM ordering.order_feedbacks WHERE reply IS NOT NULL OR replied_at_utc IS NOT NULL OR replied_by_user_id IS NOT NULL) THEN RAISE EXCEPTION 'Added columns contain new business data'; END IF;
END $guard$;
DO $$ BEGIN IF EXISTS (SELECT 1 FROM ordering.table_transfer_operations) THEN RAISE EXCEPTION 'Transfer history is nonempty; rollback refused'; END IF; END $$;

DROP TABLE ordering.table_transfer_operations;

DELETE FROM ordering.__ef_migrations_history
WHERE migration_id = '20261114164841_DurableTableTransfer';

ALTER TABLE ordering.order_feedbacks DROP COLUMN replied_at_utc;

ALTER TABLE ordering.order_feedbacks DROP COLUMN replied_by_user_id;

ALTER TABLE ordering.order_feedbacks DROP COLUMN reply;

DELETE FROM ordering.__ef_migrations_history
WHERE migration_id = '20261113151937_FeedbackReply';

DROP TABLE ordering.session_checkouts;

DELETE FROM ordering.__ef_migrations_history
WHERE migration_id = '20261113125751_OrderingSessionCheckout';

DROP TABLE ordering.fulfillment_receipts;

DROP TABLE ordering.order_fulfillment_projection;

ALTER TABLE ordering.orders DROP CONSTRAINT ck_order_fulfillment_mode;

ALTER TABLE ordering.orders DROP COLUMN fulfillment_mode;

DELETE FROM ordering.__ef_migrations_history
WHERE migration_id = '20261106183340_OrderingCupFulfillmentFoundation';

DROP TABLE ordering.kitchen_admission_outbox;

DROP TABLE ordering.kitchen_admission_reconcile_state;

DELETE FROM ordering.__ef_migrations_history
WHERE migration_id = '20261106164220_KitchenAdmissionOutbox';
COMMIT;
