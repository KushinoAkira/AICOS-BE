-- REVIEW ONLY. Main execution requires explicit approval. No auto migration or feature activation.
BEGIN;
SET LOCAL lock_timeout='3s';
SET LOCAL statement_timeout='60s';
DO $guard$ BEGIN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'kitchen' THEN RAISE EXCEPTION 'Database/schema identity mismatch'; END IF;
IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
IF NOT (current_database()='aicos_kitchen' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database() IN ('aicos_dashboard_test_kitchen','aicos_dashboard_restore_kitchen') AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside approved local/scratch scope'; END IF;
IF current_setting('aicos.dashboard_rollback_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Separate stage approval required'; END IF;
IF ARRAY(SELECT migration_id::text FROM kitchen.__ef_migrations_history ORDER BY migration_id) IS DISTINCT FROM ARRAY['20261021145859_KitchenTickets','20261024155943_TicketRemake','20261106164227_KitchenAdmissionReceipts','20261106183333_KitchenCupServingFoundation','20261108104113_QrOrdersPayFirst','20261108121518_KitchenEarlyPayments','20261114041318_KitchenBaristaWorkflow','20261114164849_TableTransferRoutingVersion']::text[] THEN RAISE EXCEPTION 'Migration history changed; inspect before proceeding'; END IF;
LOCK TABLE kitchen.kitchen_tickets IN ACCESS EXCLUSIVE MODE;
IF EXISTS(SELECT 1 FROM kitchen.kitchen_tickets WHERE table_transfer_version<>0) THEN RAISE EXCEPTION 'Routing history exists'; END IF;
END $guard$;
DO $$ BEGIN IF EXISTS (SELECT 1 FROM kitchen.kitchen_tickets WHERE table_transfer_version <> 0) THEN RAISE EXCEPTION 'Routing history exists; rollback refused'; END IF; END $$;

ALTER TABLE kitchen.kitchen_tickets DROP COLUMN table_transfer_version;

DELETE FROM kitchen.__ef_migrations_history
WHERE migration_id = '20261114164849_TableTransferRoutingVersion';
COMMIT;
