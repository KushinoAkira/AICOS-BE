-- REVIEW ONLY. Roles are cluster-wide: never rehearse on the production cluster.
-- Deliberately NOLOGIN: DBA assigns secrets with psql \password, then enables
-- LOGIN in a separately approved provisioning step. No password in SQL/logs.
\set ON_ERROR_STOP on
BEGIN;
DO $$
DECLARE
    role_name text;
    existing_role record;
BEGIN
    IF current_database() <> 'aicos_db' THEN
        RAISE EXCEPTION 'Expected aicos_db';
    END IF;
    FOREACH role_name IN ARRAY ARRAY[
        'identity_svc','menu_svc','table_reservation_svc','ordering_svc',
        'payment_svc','inventory_svc','kitchen_svc','workforce_svc',
        'reporting_svc','customer_svc','ai_svc']
    LOOP
        SELECT * INTO existing_role FROM pg_roles WHERE rolname = role_name;
        IF FOUND THEN
            IF existing_role.rolsuper OR existing_role.rolcreatedb OR
               existing_role.rolcreaterole OR existing_role.rolreplication OR
               existing_role.rolbypassrls OR EXISTS (
                   SELECT 1 FROM pg_auth_members WHERE member = existing_role.oid) OR
               EXISTS (SELECT 1 FROM pg_class WHERE relowner = existing_role.oid) OR
               EXISTS (SELECT 1 FROM pg_namespace WHERE nspowner = existing_role.oid) OR
               EXISTS (SELECT 1 FROM pg_database WHERE datdba = existing_role.oid) OR
               EXISTS (SELECT 1 FROM pg_proc WHERE proowner = existing_role.oid) THEN
                RAISE EXCEPTION 'Role % has elevated privileges/membership/ownership; manual review required', role_name;
            END IF;
            RAISE NOTICE 'Preserving existing role %, including credentials', role_name;
        ELSE
            EXECUTE format('CREATE ROLE %I NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS', role_name);
            EXECUTE format('ALTER ROLE %I SET search_path = public', role_name);
        END IF;
    END LOOP;
END $$;
COMMIT;
