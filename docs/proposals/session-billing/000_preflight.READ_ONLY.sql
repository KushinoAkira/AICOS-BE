-- READ ONLY. Run only after verifying database/system_identifier with the DBA.
BEGIN TRANSACTION READ ONLY;
SELECT current_database(), current_user, current_schema();
SELECT schema_name FROM information_schema.schemata WHERE schema_name IN ('payment','ordering');
SELECT table_schema, table_name, column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_schema = 'payment'
  AND table_name IN ('payments','session_bills','session_bill_orders','session_bill_attempts','session_bill_events')
ORDER BY table_name, ordinal_position;
SELECT schemaname, tablename, indexname, indexdef
FROM pg_indexes WHERE schemaname = 'payment'
ORDER BY tablename, indexname;
COMMIT;
