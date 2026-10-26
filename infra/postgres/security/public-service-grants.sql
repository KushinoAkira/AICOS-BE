-- REVIEW-ONLY. Do not place this file in docker-entrypoint-initdb.d.
-- Run only after backup, approval, and confirmation that all roles exist.
-- This script targets the official AICOS database aicos_db and schema public.

\set ON_ERROR_STOP on

BEGIN;

DO $$
BEGIN
    IF current_database() <> 'aicos_db' THEN
        RAISE EXCEPTION 'Expected database aicos_db, got %', current_database();
    END IF;

    IF current_schema() <> 'public' THEN
        RAISE EXCEPTION 'Expected current schema public, got %', current_schema();
    END IF;

    IF EXISTS (
        SELECT required.role_name
        FROM (VALUES
            ('identity_svc'), ('menu_svc'), ('table_reservation_svc'),
            ('ordering_svc'), ('payment_svc'), ('inventory_svc'),
            ('kitchen_svc'), ('workforce_svc'), ('reporting_svc'),
            ('customer_svc'), ('ai_svc')
        ) AS required(role_name)
        LEFT JOIN pg_roles role ON role.rolname = required.role_name
        WHERE role.oid IS NULL
    ) THEN
        RAISE EXCEPTION 'At least one required service role does not exist';
    END IF;

    IF EXISTS (
        SELECT 1 FROM pg_roles role
        WHERE role.rolname IN (
            'identity_svc','menu_svc','table_reservation_svc','ordering_svc',
            'payment_svc','inventory_svc','kitchen_svc','workforce_svc',
            'reporting_svc','customer_svc','ai_svc')
          AND (role.rolsuper OR role.rolcreatedb OR role.rolcreaterole OR
               role.rolreplication OR role.rolbypassrls OR
               EXISTS (SELECT 1 FROM pg_auth_members WHERE member = role.oid) OR
               EXISTS (SELECT 1 FROM pg_class WHERE relowner = role.oid) OR
               EXISTS (SELECT 1 FROM pg_namespace WHERE nspowner = role.oid) OR
               EXISTS (SELECT 1 FROM pg_database WHERE datdba = role.oid) OR
               EXISTS (SELECT 1 FROM pg_proc WHERE proowner = role.oid))
    ) THEN
        RAISE EXCEPTION 'Runtime role owns objects or has elevated/inherited privileges; DBA review required';
    END IF;
    IF EXISTS (
        SELECT 1 FROM information_schema.table_privileges
        WHERE table_schema = 'public' AND grantee = 'PUBLIC'
    ) THEN
        RAISE EXCEPTION 'PUBLIC has table privileges; direct role REVOKE cannot enforce isolation';
    END IF;
END $$;

-- Conditional Gate A approval excludes every PUBLIC ACL change.
-- Preserve PUBLIC TEMPORARY/CONNECT and schema privileges exactly as found.
-- Direct role revokes below cannot override privileges inherited from PUBLIC.

REVOKE CREATE, TEMPORARY ON DATABASE aicos_db FROM
    identity_svc, menu_svc, table_reservation_svc, ordering_svc, payment_svc,
    inventory_svc, kitchen_svc, workforce_svc, reporting_svc, customer_svc,
    ai_svc;
REVOKE CREATE ON SCHEMA public FROM
    identity_svc, menu_svc, table_reservation_svc, ordering_svc, payment_svc,
    inventory_svc, kitchen_svc, workforce_svc, reporting_svc, customer_svc,
    ai_svc;

-- Table REVOKE does not clear previously granted column privileges.
-- Remove only explicit column ACLs belonging to the named runtime roles.
DO $$
DECLARE permission record;
BEGIN
    FOR permission IN
        SELECT grantee, table_name, column_name, privilege_type
        FROM information_schema.column_privileges
        WHERE table_schema = 'public' AND grantee IN (
            'identity_svc','menu_svc','table_reservation_svc','ordering_svc',
            'payment_svc','inventory_svc','kitchen_svc','workforce_svc',
            'reporting_svc','customer_svc','ai_svc')
    LOOP
        EXECUTE format('REVOKE %s (%I) ON public.%I FROM %I',
            permission.privilege_type, permission.column_name,
            permission.table_name, permission.grantee);
    END LOOP;
END $$;

REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM
    identity_svc, menu_svc, table_reservation_svc, ordering_svc, payment_svc,
    inventory_svc, kitchen_svc, workforce_svc, reporting_svc, customer_svc,
    ai_svc;

-- Current UUID-based schema has no sequences. Do not retain stale sequence
-- privileges if an existing role is reused; new sequence grants need review.
REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public FROM
    identity_svc, menu_svc, table_reservation_svc, ordering_svc, payment_svc,
    inventory_svc, kitchen_svc, workforce_svc, reporting_svc, customer_svc,
    ai_svc;

GRANT CONNECT ON DATABASE aicos_db TO
    identity_svc, menu_svc, table_reservation_svc, ordering_svc, payment_svc,
    inventory_svc, kitchen_svc, workforce_svc, reporting_svc, customer_svc,
    ai_svc;

GRANT USAGE ON SCHEMA public TO
    identity_svc, menu_svc, table_reservation_svc, ordering_svc, payment_svc,
    inventory_svc, kitchen_svc, workforce_svc, reporting_svc, customer_svc,
    ai_svc;

GRANT SELECT ON
    public.tenants,
    public.branches,
    public.users,
    public.roles,
    public.tenant_users,
    public.staff_assignments,
    public.refresh_sessions
TO identity_svc;
GRANT INSERT ON
    public.users,
    public.roles,
    public.tenant_users,
    public.staff_assignments,
    public.refresh_sessions
TO identity_svc;
GRANT UPDATE (revoked_at_utc, replaced_by_session_id, version)
ON public.refresh_sessions TO identity_svc;

GRANT SELECT ON
    public.categories,
    public.products,
    public.product_variants,
    public.product_option_groups,
    public.product_options,
    public.branch_products
TO menu_svc;

GRANT SELECT ON
    public.table_areas,
    public.cafe_tables,
    public.table_sessions,
    public.table_session_tables
TO table_reservation_svc;
GRANT INSERT ON public.table_sessions, public.table_session_tables TO table_reservation_svc;
GRANT UPDATE (status) ON public.cafe_tables TO table_reservation_svc;
GRANT UPDATE (status, closed_at) ON public.table_sessions TO table_reservation_svc;
GRANT UPDATE (left_at) ON public.table_session_tables TO table_reservation_svc;

GRANT SELECT, INSERT ON
    public.orders,
    public.order_items,
    public.order_item_options,
    public.order_item_events
TO ordering_svc;

GRANT SELECT ON
    public.orders,
    public.order_items,
    public.order_item_events
TO kitchen_svc;
GRANT UPDATE (status) ON public.order_items TO kitchen_svc;
GRANT INSERT ON public.order_item_events TO kitchen_svc;

-- Apply only after the approved Payment migration has created payment_events.
GRANT SELECT ON
    public.bills,
    public.bill_items,
    public.payments
TO payment_svc;
GRANT INSERT ON public.bills, public.bill_items, public.payments TO payment_svc;
GRANT UPDATE (status, closed_at, closed_by_user_id, version)
ON public.bills TO payment_svc;
GRANT UPDATE (
    status,
    confirmed_by_user_id,
    confirmed_at,
    evidence_reference,
    paid_at,
    version
) ON public.payments TO payment_svc;
GRANT SELECT, INSERT ON public.payment_events TO payment_svc;

-- Inventory, Workforce, Reporting, Customer, and AI do not yet implement
-- database-backed features. CONNECT and schema USAGE are sufficient until an
-- approved feature and service-owned grant migration exist.

COMMIT;

-- Read-only verification; expected rows show only approved privileges.
SELECT grantee, table_name, string_agg(privilege_type, ',' ORDER BY privilege_type) AS privileges
FROM information_schema.role_table_grants
WHERE table_schema = 'public'
  AND grantee IN (
      'identity_svc', 'menu_svc', 'table_reservation_svc', 'ordering_svc',
      'payment_svc', 'inventory_svc', 'kitchen_svc', 'workforce_svc',
      'reporting_svc', 'customer_svc', 'ai_svc')
GROUP BY grantee, table_name
ORDER BY grantee, table_name;
