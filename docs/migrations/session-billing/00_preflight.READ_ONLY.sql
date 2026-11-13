-- Set aicos.expected_database and aicos.expected_schema through session options after target approval.
BEGIN READ ONLY;
SELECT current_database(), current_user, inet_server_addr(), inet_server_port(), version();
DO $guard$
DECLARE service_schema text := current_setting('aicos.expected_schema', true);
BEGIN
 IF current_setting('aicos.expected_database', true) IS NULL OR current_database() <> current_setting('aicos.expected_database', true) THEN RAISE EXCEPTION 'Database identity mismatch'; END IF;
 IF service_schema IS NULL OR service_schema NOT IN ('ordering','payment') THEN RAISE EXCEPTION 'Unknown service schema'; END IF;
 IF to_regnamespace(service_schema) IS NULL THEN RAISE EXCEPTION 'Missing prerequisite schema'; END IF;
 IF to_regclass(service_schema || '.__ef_migrations_history') IS NULL THEN RAISE EXCEPTION 'Missing EF history; compare baseline before proceeding'; END IF;
END $guard$;
SELECT table_schema, table_name FROM information_schema.tables WHERE table_schema = current_setting('aicos.expected_schema') ORDER BY table_name;
SELECT schemaname, tablename, indexname, indexdef FROM pg_indexes WHERE schemaname = current_setting('aicos.expected_schema') ORDER BY tablename,indexname;
SELECT pid, usename, application_name, state, xact_start FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid();
SELECT n.nspname,c.relname,count(a.attnum) columns FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped WHERE c.relkind='r' AND n.nspname=current_setting('aicos.expected_schema') GROUP BY n.nspname,c.relname ORDER BY c.relname;
-- DBA must inspect per-service history and verify predecessor exactly; this report does not assert it.
COMMIT;
