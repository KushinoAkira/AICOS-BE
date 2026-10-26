-- READ ONLY. Check effective privileges against the reviewed API matrix.
-- Before credential activation: -v expect_login=false. Final check: true.
-- No trial INSERT/UPDATE/DELETE, DDL, SET ROLE, GRANT or role creation.
\set ON_ERROR_STOP on
\if :{?expect_login}
\else
\set expect_login true
\endif
BEGIN READ ONLY;
DO $$ BEGIN
    IF current_database() <> 'aicos_db' OR current_schema() <> 'public' THEN
        RAISE EXCEPTION 'Expected aicos_db.public';
    END IF;
END $$;
WITH matrix(role_name, read_tables, insert_tables) AS (
    VALUES
    ('identity_svc', ARRAY['tenants','branches','users','roles','tenant_users','staff_assignments','refresh_sessions']::text[], ARRAY['users','roles','tenant_users','staff_assignments','refresh_sessions']::text[]),
    ('menu_svc', ARRAY['categories','products','product_variants','product_option_groups','product_options','branch_products']::text[], ARRAY[]::text[]),
    ('table_reservation_svc', ARRAY['table_areas','cafe_tables','table_sessions','table_session_tables']::text[], ARRAY['table_sessions','table_session_tables']::text[]),
    ('ordering_svc', ARRAY['orders','order_items','order_item_options','order_item_events']::text[], ARRAY['orders','order_items','order_item_options','order_item_events']::text[]),
    ('payment_svc', ARRAY['bills','bill_items','payments','payment_events']::text[], ARRAY['bills','bill_items','payments','payment_events']::text[]),
    ('kitchen_svc', ARRAY['orders','order_items','order_item_events']::text[], ARRAY['order_item_events']::text[]),
    ('inventory_svc', ARRAY[]::text[], ARRAY[]::text[]),
    ('workforce_svc', ARRAY[]::text[], ARRAY[]::text[]),
    ('reporting_svc', ARRAY[]::text[], ARRAY[]::text[]),
    ('customer_svc', ARRAY[]::text[], ARRAY[]::text[]),
    ('ai_svc', ARRAY[]::text[], ARRAY[]::text[])
), allowed_updates(role_name, table_name, columns) AS (
    VALUES
    ('identity_svc', 'refresh_sessions', ARRAY['revoked_at_utc','replaced_by_session_id','version']::text[]),
    ('table_reservation_svc', 'cafe_tables', ARRAY['status']::text[]),
    ('table_reservation_svc', 'table_sessions', ARRAY['status','closed_at']::text[]),
    ('table_reservation_svc', 'table_session_tables', ARRAY['left_at']::text[]),
    ('kitchen_svc', 'order_items', ARRAY['status']::text[]),
    ('payment_svc', 'bills', ARRAY['status','closed_at','closed_by_user_id','version']::text[]),
    ('payment_svc', 'payments', ARRAY['status','confirmed_by_user_id','confirmed_at','evidence_reference','paid_at','version']::text[])
), runtime_roles AS (
    SELECT r.*, m.read_tables, m.insert_tables
    FROM matrix m JOIN pg_roles r ON r.rolname=m.role_name
), objects AS (
    SELECT c.oid, c.relname FROM pg_class c
    JOIN pg_namespace n ON n.oid=c.relnamespace
    WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','f')
), table_checks AS (
    SELECT r.rolname::text AS role_name,'TABLE'::text AS kind,o.relname::text AS object_name,p.privilege,
           CASE p.privilege WHEN 'SELECT' THEN o.relname=ANY(r.read_tables)
                            WHEN 'INSERT' THEN o.relname=ANY(r.insert_tables)
                            ELSE false END AS expected,
           has_table_privilege(r.oid,o.oid,p.privilege) AS actual
    FROM runtime_roles r CROSS JOIN objects o
    CROSS JOIN (VALUES ('SELECT'),('INSERT'),('UPDATE'),('DELETE'),('TRUNCATE'),('REFERENCES'),('TRIGGER'),('MAINTAIN')) p(privilege)
), column_checks AS (
    SELECT r.rolname::text AS role_name,'COLUMN'::text AS kind,
           (o.relname||'.'||a.attname)::text AS object_name,p.privilege,
           CASE p.privilege WHEN 'SELECT' THEN o.relname=ANY(r.read_tables)
                WHEN 'INSERT' THEN o.relname=ANY(r.insert_tables)
                WHEN 'UPDATE' THEN EXISTS (
                    SELECT 1 FROM allowed_updates u WHERE u.role_name=r.rolname
                      AND u.table_name=o.relname AND a.attname=ANY(u.columns))
                ELSE false END AS expected,
           has_column_privilege(r.oid,o.oid,a.attnum,p.privilege) AS actual
    FROM runtime_roles r CROSS JOIN objects o
    JOIN pg_attribute a ON a.attrelid=o.oid AND a.attnum>0 AND NOT a.attisdropped
    CROSS JOIN (VALUES ('SELECT'),('INSERT'),('UPDATE'),('REFERENCES')) p(privilege)
), database_checks AS (
    SELECT r.rolname::text AS role_name,'DATABASE'::text AS kind,current_database()::text AS object_name,
           -- TEMPORARY remains inherited from PUBLIC under conditional approval;
           -- no runtime-role TEMPORARY grant is added by this deployment.
           p.privilege,p.privilege IN ('CONNECT','TEMPORARY') AS expected,
           has_database_privilege(r.oid,current_database(),p.privilege) AS actual
    FROM runtime_roles r CROSS JOIN (VALUES ('CONNECT'),('CREATE'),('TEMPORARY')) p(privilege)
), schema_checks AS (
    SELECT r.rolname::text AS role_name,'SCHEMA'::text AS kind,n.nspname::text AS object_name,
           p.privilege,n.nspname='public' AND p.privilege='USAGE' AS expected,
           has_schema_privilege(r.oid,n.oid,p.privilege) AS actual
    FROM runtime_roles r CROSS JOIN pg_namespace n CROSS JOIN (VALUES ('USAGE'),('CREATE')) p(privilege)
    WHERE n.nspname NOT LIKE 'pg_%' AND n.nspname <> 'information_schema'
), sequence_checks AS (
    SELECT r.rolname::text AS role_name,'SEQUENCE'::text AS kind,c.relname::text AS object_name,p.privilege,
           false AS expected,has_sequence_privilege(r.oid,c.oid,p.privilege) AS actual
    FROM runtime_roles r CROSS JOIN pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
    CROSS JOIN (VALUES ('SELECT'),('USAGE'),('UPDATE')) p(privilege)
    WHERE n.nspname='public' AND c.relkind='S'
), checks AS (
    SELECT * FROM table_checks UNION ALL SELECT * FROM column_checks
    UNION ALL SELECT * FROM database_checks UNION ALL SELECT * FROM schema_checks
    UNION ALL SELECT * FROM sequence_checks
), failures AS (
    SELECT * FROM checks WHERE actual IS DISTINCT FROM expected
    UNION ALL
    SELECT m.role_name,'ROLE','exists','EXISTS',true,false
    FROM matrix m LEFT JOIN pg_roles r ON r.rolname=m.role_name WHERE r.oid IS NULL
    UNION ALL
    SELECT r.rolname::text,'ROLE','attributes','SAFE_FLAGS',true,
           NOT (r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls)
    FROM runtime_roles r WHERE r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls
    UNION ALL
    SELECT r.rolname::text,'ROLE','login','LOGIN',:'expect_login'::boolean,r.rolcanlogin
    FROM runtime_roles r WHERE r.rolcanlogin IS DISTINCT FROM :'expect_login'::boolean
    UNION ALL
    SELECT r.rolname::text,'ROLE','membership','NO_MEMBERSHIP',true,false
    FROM runtime_roles r WHERE EXISTS(SELECT 1 FROM pg_auth_members WHERE member=r.oid)
    UNION ALL
    SELECT r.rolname::text,'ROLE','ownership','NO_OWNERSHIP',true,false
    FROM runtime_roles r WHERE EXISTS(SELECT 1 FROM pg_class WHERE relowner=r.oid)
       OR EXISTS(SELECT 1 FROM pg_namespace WHERE nspowner=r.oid)
       OR EXISTS(SELECT 1 FROM pg_database WHERE datdba=r.oid)
       OR EXISTS(SELECT 1 FROM pg_proc WHERE proowner=r.oid)
)
SELECT 'FAILURE' AS result,role_name,kind,object_name,privilege,expected::text,actual::text
FROM failures
UNION ALL
SELECT 'SUMMARY',NULL,'issue_count',count(*)::text,NULL,NULL,NULL FROM failures
ORDER BY result,role_name,kind,object_name,privilege;
COMMIT;
-- SUMMARY object_name must equal 0. psql exit 0 alone is NOT a privilege PASS.
-- Grant options, explicit ACLs and role password/LOGIN activation are separately
-- inspected using the audit script and approved DBA workflow.
