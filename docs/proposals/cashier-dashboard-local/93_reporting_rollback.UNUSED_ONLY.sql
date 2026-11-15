-- REVIEW ONLY. Main execution requires explicit approval. No auto migration or feature activation.
BEGIN;
SET LOCAL lock_timeout='3s';
SET LOCAL statement_timeout='60s';
DO $guard$ BEGIN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'reporting' THEN RAISE EXCEPTION 'Database/schema identity mismatch'; END IF;
IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
IF NOT (current_database()='aicos_reporting' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database() IN ('aicos_dashboard_test_reporting','aicos_dashboard_restore_reporting') AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside approved local/scratch scope'; END IF;
IF current_setting('aicos.dashboard_rollback_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Separate stage approval required'; END IF;
IF ARRAY(SELECT migration_id::text FROM reporting.__ef_migrations_history ORDER BY migration_id) IS DISTINCT FROM ARRAY['20261021151155_OrderFacts','20261114125038_ConfirmedCollectionInbox']::text[] THEN RAISE EXCEPTION 'Migration history changed; inspect before proceeding'; END IF;
LOCK TABLE reporting.collection_facts IN ACCESS EXCLUSIVE MODE;
IF EXISTS(SELECT 1 FROM reporting.collection_facts) THEN RAISE EXCEPTION 'New data/audit exists; retain schema and repair forward'; END IF;
END $guard$;
DO $$ BEGIN IF EXISTS (SELECT 1 FROM reporting.collection_facts) THEN RAISE EXCEPTION 'Nonempty reporting rollback refused; retain receipt inbox'; END IF; END $$;

DROP TABLE reporting.collection_facts;

DELETE FROM reporting.__ef_migrations_history
WHERE migration_id = '20261114125038_ConfirmedCollectionInbox';
COMMIT;
