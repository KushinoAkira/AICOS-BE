-- PROPOSAL ONLY. Requires fresh backup, maintenance and separate approval.
BEGIN;
SET LOCAL lock_timeout='3s'; SET LOCAL statement_timeout='60s';
DO $guard$ BEGIN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'payment' THEN RAISE EXCEPTION 'Wrong target'; END IF;
IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Wrong cluster'; END IF;
IF NOT (current_database()='aicos_payment' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database()='aicos_dashboard_upstream_payment' AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside reviewed scope'; END IF;
IF current_setting('aicos.dashboard_upstream_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Separate approval required'; END IF;
IF ARRAY(SELECT migration_id::text FROM payment.__ef_migrations_history ORDER BY migration_id) IS DISTINCT FROM ARRAY['20261020180751_AddPaymentProviders','20261020192200_CancellationPolicyAndMerchantSettings','20261021094418_PaymentsRefundsWebhooks','20261021102642_ConstraintsAndIndexes','20261113125804_PaymentSessionBills','20261114125030_CashierMoneyShifts','20261114164856_BillSplitPlans']::text[] THEN RAISE EXCEPTION 'History mismatch or already applied'; END IF;
END $guard$;
ALTER TABLE payment.payment_refunds ADD bank_account_holder character varying(100);

ALTER TABLE payment.payment_refunds ADD bank_account_number character varying(20);

ALTER TABLE payment.payment_refunds ADD bank_name character varying(100);

INSERT INTO payment.__ef_migrations_history (migration_id, product_version)
VALUES ('20261115093913_RefundToBankAccount', '8.0.31');
COMMIT;
