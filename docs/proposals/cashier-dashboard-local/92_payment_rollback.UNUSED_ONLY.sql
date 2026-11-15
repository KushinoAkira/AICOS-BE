-- REVIEW ONLY. Main execution requires explicit approval. No auto migration or feature activation.
BEGIN;
SET LOCAL lock_timeout='3s';
SET LOCAL statement_timeout='60s';
DO $guard$ BEGIN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'payment' THEN RAISE EXCEPTION 'Database/schema identity mismatch'; END IF;
IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
IF NOT (current_database()='aicos_payment' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database() IN ('aicos_dashboard_test_payment','aicos_dashboard_restore_payment') AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside approved local/scratch scope'; END IF;
IF current_setting('aicos.dashboard_rollback_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Separate stage approval required'; END IF;
IF ARRAY(SELECT migration_id::text FROM payment.__ef_migrations_history ORDER BY migration_id) IS DISTINCT FROM ARRAY['20261020180751_AddPaymentProviders','20261020192200_CancellationPolicyAndMerchantSettings','20261021094418_PaymentsRefundsWebhooks','20261021102642_ConstraintsAndIndexes','20261113125804_PaymentSessionBills','20261114125030_CashierMoneyShifts','20261114164856_BillSplitPlans']::text[] THEN RAISE EXCEPTION 'Migration history changed; inspect before proceeding'; END IF;
LOCK TABLE payment.bill_split_operations,payment.bill_split_plans,payment.cashier_shifts,payment.collection_outbox,payment.session_bill_attempts,payment.session_bill_events,payment.session_bill_orders,payment.session_bills IN ACCESS EXCLUSIVE MODE;
IF EXISTS(SELECT 1 FROM payment.bill_split_operations) THEN RAISE EXCEPTION 'New data/audit exists; retain schema and repair forward'; END IF;
IF EXISTS(SELECT 1 FROM payment.bill_split_plans) THEN RAISE EXCEPTION 'New data/audit exists; retain schema and repair forward'; END IF;
IF EXISTS(SELECT 1 FROM payment.cashier_shifts) THEN RAISE EXCEPTION 'New data/audit exists; retain schema and repair forward'; END IF;
IF EXISTS(SELECT 1 FROM payment.collection_outbox) THEN RAISE EXCEPTION 'New data/audit exists; retain schema and repair forward'; END IF;
IF EXISTS(SELECT 1 FROM payment.session_bill_attempts) THEN RAISE EXCEPTION 'New data/audit exists; retain schema and repair forward'; END IF;
IF EXISTS(SELECT 1 FROM payment.session_bill_events) THEN RAISE EXCEPTION 'New data/audit exists; retain schema and repair forward'; END IF;
IF EXISTS(SELECT 1 FROM payment.session_bill_orders) THEN RAISE EXCEPTION 'New data/audit exists; retain schema and repair forward'; END IF;
IF EXISTS(SELECT 1 FROM payment.session_bills) THEN RAISE EXCEPTION 'New data/audit exists; retain schema and repair forward'; END IF;
END $guard$;
DO $$ BEGIN IF EXISTS (SELECT 1 FROM payment.bill_split_plans) OR EXISTS (SELECT 1 FROM payment.bill_split_operations) THEN RAISE EXCEPTION 'Split audit is nonempty; rollback refused'; END IF; END $$;

DROP TABLE payment.bill_split_operations;

DROP TABLE payment.bill_split_plans;

DELETE FROM payment.__ef_migrations_history
WHERE migration_id = '20261114164856_BillSplitPlans';

DO $$ BEGIN IF EXISTS (SELECT 1 FROM payment.collection_outbox) OR EXISTS (SELECT 1 FROM payment.cashier_shifts) THEN RAISE EXCEPTION 'Nonempty financial rollback refused; retain ledger and roll code forward'; END IF; END $$;

DROP TABLE payment.collection_outbox;

DROP TABLE payment.cashier_shifts;

DELETE FROM payment.__ef_migrations_history
WHERE migration_id = '20261114125030_CashierMoneyShifts';

DROP TABLE payment.session_bill_attempts;

DROP TABLE payment.session_bill_events;

DROP TABLE payment.session_bill_orders;

DROP TABLE payment.session_bills;

DELETE FROM payment.__ef_migrations_history
WHERE migration_id = '20261113125804_PaymentSessionBills';
COMMIT;
