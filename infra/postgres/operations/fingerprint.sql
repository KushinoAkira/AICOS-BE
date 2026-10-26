\set ON_ERROR_STOP on
BEGIN READ ONLY;
SET LOCAL timezone = 'UTC';
-- Small-database drill; hashes only, sorted independently of physical order.
SELECT format(
    'SELECT %L, count(*), md5(coalesce(string_agg(md5(row_to_json(t)::text), '''' ORDER BY md5(row_to_json(t)::text)), '''')) FROM %I.%I t;',
    schemaname || '.' || tablename, schemaname, tablename)
FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename
\gexec
COMMIT;
