-- PROPOSAL ONLY. Requires fresh backup, maintenance and separate approval.
BEGIN;
SET LOCAL lock_timeout='3s'; SET LOCAL statement_timeout='60s';
DO $guard$ BEGIN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'identity' THEN RAISE EXCEPTION 'Wrong target'; END IF;
IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Wrong cluster'; END IF;
IF NOT (current_database()='aicos_identity' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database()='aicos_dashboard_upstream_identity' AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside reviewed scope'; END IF;
IF current_setting('aicos.dashboard_upstream_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Separate approval required'; END IF;
IF ARRAY(SELECT migration_id::text FROM identity.__ef_migrations_history ORDER BY migration_id) IS DISTINCT FROM ARRAY['20261009154540_AddBranches','20261011154400_AddUsersSessionsOtp','20261020154659_AddTenantsAndRoles','20261020172512_AddAuditLogs','20261020181248_AddSubscriptions','20261021102704_ConstraintsAndIndexes','20261026070957_BrandSelfService','20261031171829_SignUpEmailVerification','20261101170921_GlobalCustomerAccounts','20261101175626_LargerLogo','20261101192134_PlanTableLimit']::text[] THEN RAISE EXCEPTION 'History mismatch or already applied'; END IF;
LOCK TABLE identity.tenants,identity.subscription_payments,identity.tenant_subscriptions IN SHARE ROW EXCLUSIVE MODE; IF EXISTS(SELECT 1 FROM identity.tenants t WHERE t.status='Active' AND NOT EXISTS(SELECT 1 FROM identity.subscription_payments p WHERE p.tenant_id=t.id) AND EXISTS(SELECT 1 FROM identity.tenant_subscriptions s WHERE s.tenant_id=t.id AND s.status='Active' AND s.fee>0)) THEN RAISE EXCEPTION 'Tenant login transition requires separate business review'; END IF;
END $guard$;
ALTER TABLE identity.tenants ADD delete_reason character varying(300);

ALTER TABLE identity.tenants ADD deleted_at_utc timestamp with time zone;

ALTER TABLE identity.tenants ADD payment_claim_reference character varying(80);

ALTER TABLE identity.tenants ADD requested_plan_id uuid;

ALTER TABLE identity.subscription_plans ADD deleted_at_utc timestamp with time zone;

ALTER TABLE identity.contact_messages ADD note character varying(500);

UPDATE identity.tenants t SET status = 'AwaitingPayment'
WHERE t.status = 'Active'
  AND NOT EXISTS (SELECT 1 FROM identity.subscription_payments p WHERE p.tenant_id = t.id)
  AND EXISTS (SELECT 1 FROM identity.tenant_subscriptions s WHERE s.tenant_id = t.id AND s.status = 'Active' AND s.fee > 0);

INSERT INTO identity.__ef_migrations_history (migration_id, product_version)
VALUES ('20261115110405_TenantPaymentAndSoftDelete', '8.0.31');
COMMIT;
