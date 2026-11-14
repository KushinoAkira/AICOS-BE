START TRANSACTION;
DO $guard$
BEGIN
 IF current_setting('aicos.expected_database',true) IS NULL OR current_database() <> current_setting('aicos.expected_database',true) THEN RAISE EXCEPTION 'Database identity mismatch'; END IF;
 IF current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'kitchen' THEN RAISE EXCEPTION 'Kitchen schema required'; END IF;
 IF current_setting('aicos.expected_cluster',true) IS NULL OR (SELECT system_identifier::text FROM pg_control_system()) <> current_setting('aicos.expected_cluster',true) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
END $guard$;
DO $approval$ BEGIN IF current_setting('aicos.barista_rollback_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Explicit rollback approval required'; END IF; END $approval$;
DO $empty$ BEGIN
 IF EXISTS (SELECT 1 FROM kitchen.barista_acceptances) OR EXISTS (SELECT 1 FROM kitchen.kitchen_print_jobs) THEN RAISE EXCEPTION 'Nonempty audit; preserve schema and disable feature instead'; END IF;
END $empty$;


DROP TABLE kitchen.barista_acceptances;

DROP TABLE kitchen.kitchen_print_jobs;

DELETE FROM kitchen.__ef_migrations_history
WHERE migration_id = '20261114041318_KitchenBaristaWorkflow';

COMMIT;
