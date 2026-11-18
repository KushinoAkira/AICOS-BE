-- REVIEW PROPOSAL. Execute only against an approved target. Test fixture may run on its guarded disposable cluster.
-- No role/password creation, ownership change, database migration or DELETE permission.
-- Existing runtime ACL proposal covers the six Cup tables; this file covers existing service-owned prerequisites.
\set ON_ERROR_STOP on
\if :{?expected_database}
\else
 \quit 3
\endif
\if :{?expected_schema}
\else
 \quit 3
\endif
\if :{?expected_system_identifier}
\else
 \quit 3
\endif
\if :{?service_role}
\else
 \quit 3
\endif
BEGIN;
SELECT current_database() = :'expected_database'
 AND (SELECT system_identifier::text FROM pg_control_system()) = :'expected_system_identifier'
 AND ((:'expected_schema' = 'kitchen' AND :'service_role' = 'kitchen_svc')
   OR (:'expected_schema' = 'ordering' AND :'service_role' = 'ordering_svc'))
 AND EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'service_role' AND NOT rolsuper
   AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolbypassrls AND NOT rolreplication)
 AND NOT EXISTS (SELECT 1 FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.member WHERE r.rolname=:'service_role')
 AND NOT EXISTS (SELECT 1 FROM pg_database d JOIN pg_roles r ON r.oid = d.datdba
   WHERE d.datname = current_database() AND r.rolname = :'service_role')
 AND NOT EXISTS (SELECT 1 FROM pg_namespace n JOIN pg_roles r ON r.oid = n.nspowner
   WHERE n.nspname = :'expected_schema' AND r.rolname = :'service_role')
 AND NOT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = :'expected_schema' AND tableowner = :'service_role')
 AS target_ok \gset
\if :target_ok
\else
 ROLLBACK;
 \echo 'STOP: target/role/ownership mismatch. Review DDL ownership separately.'
 \quit 3
\endif
GRANT CONNECT ON DATABASE :"expected_database" TO :"service_role";
GRANT USAGE ON SCHEMA :"expected_schema" TO :"service_role";
SELECT :'expected_schema' = 'kitchen' AS kitchen_target \gset
\if :kitchen_target
 GRANT SELECT, INSERT, UPDATE ON kitchen.kitchen_tickets, kitchen.order_admission_receipts TO :"service_role";
 GRANT SELECT, INSERT ON kitchen.kitchen_ticket_items, kitchen.cup_stickers TO :"service_role";
\else
 GRANT SELECT, INSERT, UPDATE ON ordering.orders, ordering.table_sessions, ordering.pickup_slot_settings,
   ordering.kitchen_admission_outbox, ordering.kitchen_admission_reconcile_state TO :"service_role";
 GRANT SELECT, INSERT ON ordering.order_items, ordering.order_item_options, ordering.order_feedbacks TO :"service_role";
\endif
COMMIT;
-- Explicit gap: legacy DELETE pickup_slot_settings requires a separate permission decision.
-- No PUBLIC revokes, superuser, ownership, role membership or default-privilege changes here.
