-- REVIEW ONLY. Main execution requires explicit approval. No auto migration or feature activation.
BEGIN;
SET LOCAL lock_timeout='3s';
SET LOCAL statement_timeout='60s';
DO $guard$ BEGIN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'payment' THEN RAISE EXCEPTION 'Database/schema identity mismatch'; END IF;
IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
IF NOT (current_database()='aicos_payment' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database() IN ('aicos_dashboard_test_payment','aicos_dashboard_restore_payment') AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside approved local/scratch scope'; END IF;
IF current_setting('aicos.dashboard_grants_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Separate stage approval required'; END IF;
IF ARRAY(SELECT migration_id::text FROM payment.__ef_migrations_history ORDER BY migration_id) IS DISTINCT FROM ARRAY['20261020180751_AddPaymentProviders','20261020192200_CancellationPolicyAndMerchantSettings','20261021094418_PaymentsRefundsWebhooks','20261021102642_ConstraintsAndIndexes','20261113125804_PaymentSessionBills','20261114125030_CashierMoneyShifts','20261114164856_BillSplitPlans','20261115093913_RefundToBankAccount']::text[] THEN RAISE EXCEPTION 'Migration history changed; inspect before proceeding'; END IF;
END $guard$;
GRANT USAGE ON SCHEMA payment TO payment_svc;
GRANT SELECT,INSERT ON payment.session_bills,payment.session_bill_orders,payment.session_bill_attempts,payment.session_bill_events,payment.cashier_shifts,payment.collection_outbox,payment.bill_split_plans,payment.bill_split_operations TO payment_svc;
GRANT UPDATE(status,version,paid_payment_id,paid_at_utc,closed_at_utc,ordering_acknowledged,needs_reconciliation) ON payment.session_bills TO payment_svc;
GRANT UPDATE(status,provider_reference,checkout_url,paid_at_utc) ON payment.session_bill_attempts TO payment_svc;
GRANT UPDATE(delivered_at_utc,next_attempt_at_utc,lease_owner,lease_until_utc,fence) ON payment.session_bill_events TO payment_svc;
GRANT UPDATE(closed_at_utc,counted_cash,expected_cash,closing_note,version) ON payment.cashier_shifts TO payment_svc;
GRANT UPDATE(delivered_at_utc) ON payment.collection_outbox TO payment_svc;
GRANT UPDATE(version,fingerprint,quote_json,groups_json,updated_by,updated_at_utc) ON payment.bill_split_plans TO payment_svc;
COMMIT;
