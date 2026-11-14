BEGIN READ ONLY;
SELECT current_database(),current_user,system_identifier FROM pg_control_system();
SELECT table_schema,table_name FROM information_schema.tables WHERE table_schema IN ('payment','reporting') ORDER BY 1,2;
SELECT schemaname,tablename,indexname,indexdef FROM pg_indexes WHERE tablename IN ('cashier_shifts','collection_outbox','collection_facts');
SELECT rolname,rolcanlogin,rolsuper,rolcreatedb,rolcreaterole FROM pg_roles WHERE rolname IN ('payment_svc','reporting_svc');
SELECT current_setting('aicos.expected_database',true) AS expected_database,current_setting('aicos.expected_cluster',true) AS expected_cluster;
COMMIT;
