-- REVIEW ONLY: additive permissions ONLY on the five new tables. No REVOKE/role/password changes.
-- Existing business-table privileges remain separate prerequisites. UUID keys need no sequence grant.
BEGIN;
DO $guard$
DECLARE s text := current_setting('aicos.expected_schema', true);
BEGIN
 IF current_setting('aicos.bill_grants_approved',true) IS DISTINCT FROM 'true' OR current_database() IS DISTINCT FROM current_setting('aicos.expected_database',true) THEN RAISE EXCEPTION 'Approval/target guard failed'; END IF;
 IF s='ordering' THEN
  IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='ordering_svc') THEN RAISE EXCEPTION 'Missing ordering_svc'; END IF;
  GRANT SELECT, INSERT ON ordering.session_checkouts TO ordering_svc;
  GRANT UPDATE(state,payment_id) ON ordering.session_checkouts TO ordering_svc;
 ELSIF s='payment' THEN
  IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='payment_svc') THEN RAISE EXCEPTION 'Missing payment_svc'; END IF;
  GRANT SELECT, INSERT ON payment.session_bills,payment.session_bill_orders,payment.session_bill_attempts,payment.session_bill_events TO payment_svc;
  GRANT UPDATE(status,version,paid_payment_id,paid_at_utc,closed_at_utc,ordering_acknowledged,needs_reconciliation) ON payment.session_bills TO payment_svc;
  GRANT UPDATE(status,provider_reference,checkout_url,paid_at_utc) ON payment.session_bill_attempts TO payment_svc;
  GRANT UPDATE(delivered_at_utc,next_attempt_at_utc,lease_owner,lease_until_utc,fence) ON payment.session_bill_events TO payment_svc;
 ELSE RAISE EXCEPTION 'Unknown service schema'; END IF;
END $guard$;
COMMIT;
