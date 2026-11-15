BEGIN READ ONLY;
DO $guard$
BEGIN
 IF current_setting('aicos.expected_database',true) IS NULL OR current_database() <> current_setting('aicos.expected_database',true) THEN RAISE EXCEPTION 'Database identity mismatch'; END IF;
 IF current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'kitchen' THEN RAISE EXCEPTION 'Kitchen schema required'; END IF;
 IF current_setting('aicos.expected_cluster',true) IS NULL OR (SELECT system_identifier::text FROM pg_control_system()) <> current_setting('aicos.expected_cluster',true) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
END $guard$;
DO $check$ BEGIN
 IF to_regclass('kitchen.kitchen_tickets') IS NULL THEN RAISE EXCEPTION 'Missing Kitchen baseline'; END IF;
 IF NOT EXISTS (SELECT 1 FROM kitchen.__ef_migrations_history WHERE migration_id='20261108121518_KitchenEarlyPayments') THEN RAISE EXCEPTION 'Missing predecessor'; END IF;
 IF EXISTS (SELECT 1 FROM kitchen.__ef_migrations_history WHERE migration_id='20261114041318_KitchenBaristaWorkflow') OR to_regclass('kitchen.barista_acceptances') IS NOT NULL OR to_regclass('kitchen.kitchen_print_jobs') IS NOT NULL THEN RAISE EXCEPTION 'Already applied or partial schema; inspect instead of recreating'; END IF;
END $check$;
SELECT current_database(), current_user, system_identifier FROM pg_control_system();
COMMIT;
