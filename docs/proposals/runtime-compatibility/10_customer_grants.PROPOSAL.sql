-- PROPOSAL ONLY. No local execution until explicit approval.
BEGIN;
SET LOCAL lock_timeout='3s'; SET LOCAL statement_timeout='60s';
DO $guard$ BEGIN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'customer' OR current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Wrong compatibility target'; END IF;
IF NOT (current_database()='aicos_customer' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database()='aicos_runtime_compat_customer' AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside reviewed local/test scope'; END IF;
IF current_setting('aicos.runtime_compat_grants_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Separate stage approval required'; END IF;
IF ARRAY(SELECT migration_id::text FROM customer.__ef_migrations_history ORDER BY migration_id) IS DISTINCT FROM ARRAY['20261011162712_AddCustomersLoyaltyVouchers','20261020172519_AddDrinkPreferences','20261020190049_TenantLoyaltyRulesVouchers','20261021102718_ConstraintsAndIndexes','20261023083252_LoyaltyRedemptions','20261024164151_Notifications','20261101170848_CustomerProfilePerBrand','20261101183341_StaffNotifications','20261113175515_HuntVouchers']::text[] THEN RAISE EXCEPTION 'Exact migration history changed'; END IF;

END $guard$;
GRANT USAGE ON SCHEMA customer TO customer_svc;
GRANT SELECT,INSERT ON customer.member_orders TO customer_svc;
GRANT UPDATE(is_cancelled,updated_at_utc,updated_by) ON customer.member_orders TO customer_svc;
COMMIT;
