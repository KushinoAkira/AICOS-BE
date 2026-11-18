-- REVIEW PROPOSAL. Not application startup SQL. Isolated ACL tests may execute after target verification.
-- No passwords/role creation. Operating execution still requires separate DBA approval.
-- DBA approval is required separately for any operating database.
-- Required psql variables: expected_database, expected_schema, expected_system_identifier, service_role.
\set ON_ERROR_STOP on
\if :{?expected_database}
\else
  \quit 3
\endif
\if :{?expected_schema}
\else
  \quit 3
\endif
\if :{?service_role}
\else
  \quit 3
\endif
\if :{?expected_system_identifier}
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
 AND NOT EXISTS (SELECT 1 FROM pg_database d JOIN pg_roles r ON r.oid=d.datdba
   WHERE d.datname=current_database() AND r.rolname=:'service_role')
 AND NOT EXISTS (SELECT 1 FROM pg_namespace n JOIN pg_roles r ON r.oid=n.nspowner
   WHERE n.nspname=:'expected_schema' AND r.rolname=:'service_role')
 -- An owner can bypass a simple REVOKE. Do not pretend a grant matrix fixes ownership.
 AND NOT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = :'expected_schema' AND tableowner = :'service_role')
 AS target_ok \gset
\if :target_ok
\else
 ROLLBACK;
 \echo 'STOP: identity/role/ownership mismatch; DBA must review separately.'
 \quit 3
\endif
SELECT :'expected_schema' = 'kitchen' AS kitchen_target \gset
GRANT USAGE ON SCHEMA :"expected_schema" TO :"service_role";
\if :kitchen_target
 GRANT SELECT, INSERT, UPDATE ON kitchen.cup_tasks, kitchen.cup_claims, kitchen.fulfillment_outbox TO :"service_role";
 GRANT SELECT, INSERT ON kitchen.cup_operations TO :"service_role";
 REVOKE UPDATE, DELETE, TRUNCATE ON kitchen.cup_operations FROM :"service_role";
\else
 GRANT SELECT, INSERT, UPDATE ON ordering.order_fulfillment_projection TO :"service_role";
 GRANT SELECT, INSERT ON ordering.fulfillment_receipts TO :"service_role";
 REVOKE UPDATE, DELETE, TRUNCATE ON ordering.fulfillment_receipts FROM :"service_role";
\endif
SELECT CASE WHEN :'expected_schema' = 'kitchen'
 THEN NOT has_table_privilege(:'service_role', 'kitchen.cup_operations', 'UPDATE')
  AND NOT has_table_privilege(:'service_role', 'kitchen.cup_operations', 'DELETE')
  AND NOT has_table_privilege(:'service_role', 'kitchen.cup_operations', 'TRUNCATE')
 ELSE NOT has_table_privilege(:'service_role', 'ordering.fulfillment_receipts', 'UPDATE')
  AND NOT has_table_privilege(:'service_role', 'ordering.fulfillment_receipts', 'DELETE')
  AND NOT has_table_privilege(:'service_role', 'ordering.fulfillment_receipts', 'TRUNCATE')
 END AS effective_acl_ok \gset
\if :effective_acl_ok
\else
 ROLLBACK;
 \echo 'STOP: effective inherited/PUBLIC grants still allow ledger mutation.'
 \quit 3
\endif
COMMIT;
-- Existing tickets/items/stickers/admission outbox/receipts/checkpoint permissions are prerequisites.
-- Inherited/PUBLIC grants must be inspected separately; this script does not REVOKE other roles.
-- UUID/application-assigned keys: these six tables introduce no sequence privileges.
