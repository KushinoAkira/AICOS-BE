\set ON_ERROR_STOP on
BEGIN READ ONLY;
SELECT current_database(), current_setting('transaction_read_only');
SELECT expected.name, role.oid IS NOT NULL AS exists,
       role.rolcanlogin, role.rolsuper, role.rolcreatedb, role.rolcreaterole,
       role.rolreplication, role.rolbypassrls
FROM unnest(ARRAY['identity_svc','menu_svc','table_reservation_svc','ordering_svc',
    'payment_svc','inventory_svc','kitchen_svc','workforce_svc','reporting_svc',
    'customer_svc','ai_svc']) expected(name)
LEFT JOIN pg_roles role ON role.rolname = expected.name ORDER BY expected.name;
SELECT member.rolname AS member, parent.rolname AS inherited_role
FROM pg_auth_members membership
JOIN pg_roles member ON member.oid = membership.member
JOIN pg_roles parent ON parent.oid = membership.roleid
WHERE member.rolname LIKE '%\_svc' ESCAPE '\';
SELECT grantee, table_name, column_name, privilege_type, is_grantable
FROM information_schema.column_privileges
WHERE table_schema = 'public' AND grantee LIKE '%\_svc' ESCAPE '\'
ORDER BY grantee, table_name, column_name, privilege_type;
-- Include database/schema ACL sources: PUBLIC grants remain effective even
-- when direct role grants are absent. Do not print password verifiers.
SELECT 'DATABASE' AS object_type,d.datname AS object_name,
       CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END AS grantee,
       a.privilege_type,a.is_grantable
FROM pg_database d
CROSS JOIN LATERAL aclexplode(coalesce(d.datacl,acldefault('d',d.datdba))) a
WHERE d.datname=current_database()
ORDER BY grantee,a.privilege_type;
SELECT 'SCHEMA' AS object_type,n.nspname AS object_name,
       CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END AS grantee,
       a.privilege_type,a.is_grantable
FROM pg_namespace n
CROSS JOIN LATERAL aclexplode(coalesce(n.nspacl,acldefault('n',n.nspowner))) a
WHERE n.nspname='public'
ORDER BY grantee,a.privilege_type;
SELECT c.relname AS sequence_name,
       CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END AS grantee,
       a.privilege_type,a.is_grantable
FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
CROSS JOIN LATERAL aclexplode(coalesce(c.relacl,acldefault('s',c.relowner))) a
WHERE n.nspname='public' AND c.relkind='S'
ORDER BY c.relname,grantee,a.privilege_type;
COMMIT;
