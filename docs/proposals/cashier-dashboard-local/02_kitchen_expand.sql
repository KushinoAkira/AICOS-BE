-- REVIEW ONLY. Main execution requires explicit approval. No auto migration or feature activation.
BEGIN;
SET LOCAL lock_timeout='3s';
SET LOCAL statement_timeout='60s';
DO $guard$ BEGIN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'kitchen' THEN RAISE EXCEPTION 'Database/schema identity mismatch'; END IF;
IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
IF NOT (current_database()='aicos_kitchen' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database() IN ('aicos_dashboard_test_kitchen','aicos_dashboard_restore_kitchen') AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside approved local/scratch scope'; END IF;
IF current_setting('aicos.dashboard_expand_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Separate stage approval required'; END IF;
IF ARRAY(SELECT migration_id::text FROM kitchen.__ef_migrations_history ORDER BY migration_id) IS DISTINCT FROM ARRAY['20261021145859_KitchenTickets','20261024155943_TicketRemake','20261106164227_KitchenAdmissionReceipts','20261106183333_KitchenCupServingFoundation','20261108104113_QrOrdersPayFirst','20261108121518_KitchenEarlyPayments','20261114041318_KitchenBaristaWorkflow']::text[] THEN RAISE EXCEPTION 'Migration history changed; inspect before proceeding'; END IF;
IF EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='kitchen' AND table_name='kitchen_tickets' AND column_name='table_transfer_version') THEN RAISE EXCEPTION 'Existing target column requires review'; END IF;
END $guard$;
ALTER TABLE kitchen.kitchen_tickets ADD table_transfer_version bigint NOT NULL DEFAULT 0;

INSERT INTO kitchen.__ef_migrations_history (migration_id, product_version)
VALUES ('20261114164849_TableTransferRoutingVersion', '8.0.31');
COMMIT;
