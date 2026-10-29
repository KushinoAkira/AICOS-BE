-- REVIEW ONLY; NO PASSWORDS; NOT EXECUTED IN D1-A.
\ir 00_target_guard.sql
BEGIN;
DO $roles$
DECLARE role_name text;
BEGIN
    FOREACH role_name IN ARRAY ARRAY[
        'identity_svc','menu_svc','table_reservation_svc','ordering_svc','payment_svc',
        'inventory_svc','kitchen_svc','workforce_svc','reporting_svc','customer_svc','ai_svc'
    ] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname=role_name) THEN
            RAISE EXCEPTION 'Role % already exists: stop for review, no overwrite',role_name;
        END IF;
        EXECUTE format('CREATE ROLE %I NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 10',role_name);
        EXECUTE format('ALTER ROLE %I SET search_path = public',role_name);
    END LOOP;
END
$roles$;
COMMIT;
-- Separate approval/configuration is needed to set new TEST passwords and enable LOGIN.

