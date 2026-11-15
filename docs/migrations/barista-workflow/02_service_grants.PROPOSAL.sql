BEGIN;
DO $guard$
BEGIN
 IF current_setting('aicos.expected_database',true) IS NULL OR current_database() <> current_setting('aicos.expected_database',true) THEN RAISE EXCEPTION 'Database identity mismatch'; END IF;
 IF current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'kitchen' THEN RAISE EXCEPTION 'Kitchen schema required'; END IF;
 IF current_setting('aicos.expected_cluster',true) IS NULL OR (SELECT system_identifier::text FROM pg_control_system()) <> current_setting('aicos.expected_cluster',true) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
END $guard$;
DO $approval$ BEGIN IF current_setting('aicos.barista_grants_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Explicit grants approval required'; END IF; END $approval$;

DO $role$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='kitchen_svc' AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole) THEN RAISE EXCEPTION 'Non-admin kitchen_svc required'; END IF; END $role$;
REVOKE ALL ON kitchen.barista_acceptances,kitchen.kitchen_print_jobs FROM kitchen_svc;
GRANT SELECT,INSERT ON kitchen.barista_acceptances,kitchen.kitchen_print_jobs TO kitchen_svc;
GRANT UPDATE (completed_at_utc,completed_by) ON kitchen.barista_acceptances TO kitchen_svc;
GRANT UPDATE (status,started_at_utc,printed_at_utc,test_output,error_code,lease_owner,lease_until_utc,fence) ON kitchen.kitchen_print_jobs TO kitchen_svc;
-- UUID keys: no sequence grant. No PUBLIC or other-service ACL changes.
COMMIT;
