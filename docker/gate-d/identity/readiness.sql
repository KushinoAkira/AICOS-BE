-- Metadata/counts only. Bootstrap connection stays inside pinned TEST container.
\set ON_ERROR_STOP on
BEGIN READ ONLY;
DO $guard$
BEGIN
 IF current_database()<>'aicos_payment_test' OR current_schema()<>'public'
 OR (SELECT system_identifier::text FROM pg_control_system())<>'7701931530273472557'
 OR current_setting('transaction_read_only')<>'on'
 THEN RAISE EXCEPTION 'Refusing non-Gate-D read-only target'; END IF;
END $guard$;
WITH owned AS (
 SELECT c.oid,c.relname FROM pg_class c WHERE c.relnamespace='public'::regnamespace
 AND c.relkind='r' AND c.relname IN('tenants','branches','users','roles','tenant_users','staff_assignments','refresh_sessions')
), runtime_roles AS (
 SELECT * FROM pg_roles WHERE rolname IN('identity_svc','menu_svc','table_reservation_svc','ordering_svc','payment_svc','inventory_svc','kitchen_svc','workforce_svc','reporting_svc','customer_svc','ai_svc')
)
SELECT json_build_object(
 'Database',current_database(),'Schema',current_schema(),'ReadOnly',current_setting('transaction_read_only'),
 'SystemIdentifier',(SELECT system_identifier::text FROM pg_control_system()),
 'Tables',(SELECT count(*) FROM pg_tables WHERE schemaname='public'),
 'Columns',(SELECT count(*) FROM information_schema.columns WHERE table_schema='public'),
 'Roles',(SELECT json_agg(json_build_object('Role',r.rolname,'Login',r.rolcanlogin,
  'PasswordSet',(SELECT rolpassword IS NOT NULL FROM pg_authid WHERE oid=r.oid),
  'Elevated',r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolinherit OR r.rolreplication OR r.rolbypassrls,
  'Membership',(SELECT count(*) FROM pg_auth_members WHERE member=r.oid OR roleid=r.oid),
  'OwnsObjects',EXISTS(SELECT 1 FROM pg_shdepend WHERE refclassid='pg_authid'::regclass AND refobjid=r.oid AND deptype='o'),
  'Connect',has_database_privilege(r.oid,current_database(),'CONNECT'),
  'Usage',has_schema_privilege(r.oid,'public','USAGE')) ORDER BY r.rolname) FROM runtime_roles r),
 'IdentityTables',(SELECT count(*) FROM owned),
 'IdentityAclErrors',(SELECT count(*) FROM owned t CROSS JOIN(VALUES('SELECT'),('INSERT'),('UPDATE'),('DELETE'),('TRUNCATE'),('REFERENCES'),('TRIGGER'),('MAINTAIN')) p(name)
  WHERE has_table_privilege('identity_svc',t.oid,p.name) IS DISTINCT FROM
  (p.name='SELECT' OR (p.name='INSERT' AND t.relname IN('users','roles','tenant_users','staff_assignments','refresh_sessions')))),
 'UpdateColumns',(SELECT json_agg(a.attname ORDER BY a.attname) FROM pg_attribute a WHERE a.attrelid='public.refresh_sessions'::regclass AND a.attnum>0 AND NOT a.attisdropped AND has_column_privilege('identity_svc',a.attrelid,a.attnum,'UPDATE')),
 'IdentityRows',(SELECT count(*) FROM public.tenants)+(SELECT count(*) FROM public.branches)+(SELECT count(*) FROM public.users)+(SELECT count(*) FROM public.roles)+(SELECT count(*) FROM public.tenant_users)+(SELECT count(*) FROM public.staff_assignments)+(SELECT count(*) FROM public.refresh_sessions));
COMMIT;
