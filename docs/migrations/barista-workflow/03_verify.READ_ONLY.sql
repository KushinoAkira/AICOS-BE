BEGIN READ ONLY;
DO $guard$
BEGIN
 IF current_setting('aicos.expected_database',true) IS NULL OR current_database() <> current_setting('aicos.expected_database',true) THEN RAISE EXCEPTION 'Database identity mismatch'; END IF;
 IF current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'kitchen' THEN RAISE EXCEPTION 'Kitchen schema required'; END IF;
 IF current_setting('aicos.expected_cluster',true) IS NULL OR (SELECT system_identifier::text FROM pg_control_system()) <> current_setting('aicos.expected_cluster',true) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
END $guard$;

DO $verify$ BEGIN
 IF NOT EXISTS (SELECT 1 FROM kitchen.__ef_migrations_history WHERE migration_id='20261114041318_KitchenBaristaWorkflow') THEN RAISE EXCEPTION 'Migration missing'; END IF;
 IF (SELECT count(*) FROM information_schema.columns WHERE table_schema='kitchen' AND table_name IN ('barista_acceptances','kitchen_print_jobs'))<>33 THEN RAISE EXCEPTION 'Unexpected columns'; END IF;
 IF (SELECT count(*) FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid JOIN pg_namespace n ON n.oid=t.relnamespace WHERE n.nspname='kitchen' AND t.relname IN ('barista_acceptances','kitchen_print_jobs') AND c.contype='f')<>3 THEN RAISE EXCEPTION 'Unexpected foreign keys'; END IF;
 IF has_table_privilege('kitchen_svc','kitchen.kitchen_print_jobs','DELETE') OR has_column_privilege('kitchen_svc','kitchen.kitchen_print_jobs','payload','UPDATE') OR has_column_privilege('kitchen_svc','kitchen.barista_acceptances','accepted_by','UPDATE') THEN RAISE EXCEPTION 'Service audit permission too broad'; END IF;
END $verify$;
SELECT tablename,indexname,indexdef FROM pg_indexes WHERE schemaname='kitchen' AND tablename IN ('barista_acceptances','kitchen_print_jobs') ORDER BY tablename,indexname;
COMMIT;
