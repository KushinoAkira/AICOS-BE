-- REVIEW ONLY. Main execution requires explicit approval. No auto migration or feature activation.
BEGIN READ ONLY;
SET LOCAL lock_timeout='3s';
SET LOCAL statement_timeout='60s';
DO $guard$ BEGIN
CASE current_setting('aicos.expected_schema',true)
WHEN 'ordering' THEN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'ordering' THEN RAISE EXCEPTION 'Database/schema identity mismatch'; END IF;
IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
IF NOT (current_database()='aicos_ordering' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database() IN ('aicos_dashboard_test_ordering','aicos_dashboard_restore_ordering') AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside approved local/scratch scope'; END IF;
WHEN 'kitchen' THEN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'kitchen' THEN RAISE EXCEPTION 'Database/schema identity mismatch'; END IF;
IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
IF NOT (current_database()='aicos_kitchen' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database() IN ('aicos_dashboard_test_kitchen','aicos_dashboard_restore_kitchen') AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside approved local/scratch scope'; END IF;
WHEN 'payment' THEN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'payment' THEN RAISE EXCEPTION 'Database/schema identity mismatch'; END IF;
IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
IF NOT (current_database()='aicos_payment' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database() IN ('aicos_dashboard_test_payment','aicos_dashboard_restore_payment') AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside approved local/scratch scope'; END IF;
WHEN 'reporting' THEN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'reporting' THEN RAISE EXCEPTION 'Database/schema identity mismatch'; END IF;
IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
IF NOT (current_database()='aicos_reporting' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database() IN ('aicos_dashboard_test_reporting','aicos_dashboard_restore_reporting') AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside approved local/scratch scope'; END IF;
ELSE RAISE EXCEPTION 'Unsupported schema'; END CASE;
END $guard$;
SELECT current_database(),system_identifier::text,current_setting('transaction_read_only') FROM pg_control_system();
SELECT n.nspname,c.relname,a.attname,format_type(a.atttypid,a.atttypmod),a.attnotnull FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped WHERE c.relkind='r' AND n.nspname=current_setting('aicos.expected_schema') ORDER BY c.relname,a.attnum;
SELECT c.relname,k.conname,pg_get_constraintdef(k.oid) FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=current_setting('aicos.expected_schema') ORDER BY c.relname,k.conname;
SELECT tablename,indexname,indexdef FROM pg_indexes WHERE schemaname=current_setting('aicos.expected_schema') ORDER BY tablename,indexname;
SELECT pid,usename,application_name,state,xact_start FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid();
COMMIT;
