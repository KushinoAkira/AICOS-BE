-- REVIEW ONLY: explicit target, cluster and approval required. No operational execution is authorized by this file.
DO $$ BEGIN
 IF current_setting('aicos.expected_database',true) IS NULL OR current_database() <> current_setting('aicos.expected_database',true) OR current_database()='aicos_db' THEN RAISE EXCEPTION 'Database target guard failed'; END IF;
 IF current_setting('aicos.expected_cluster',true) IS NULL OR (SELECT system_identifier::text FROM pg_control_system()) <> current_setting('aicos.expected_cluster',true) THEN RAISE EXCEPTION 'Cluster guard failed'; END IF;
 IF current_setting('aicos.cashier_grants_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Explicit cashier grants approval required'; END IF;
END $$;
-- Run the block for the current service-owned database only. UUID keys need no sequence privileges.
DO $$ BEGIN
 IF to_regclass('payment.cashier_shifts') IS NOT NULL THEN
  GRANT USAGE ON SCHEMA payment TO payment_svc;
  GRANT SELECT,INSERT ON payment.cashier_shifts TO payment_svc;
  GRANT UPDATE(closed_at_utc,counted_cash,expected_cash,closing_note,version) ON payment.cashier_shifts TO payment_svc;
  GRANT SELECT,INSERT ON payment.collection_outbox TO payment_svc;
  GRANT UPDATE(delivered_at_utc) ON payment.collection_outbox TO payment_svc;
 END IF;
 IF to_regclass('reporting.collection_facts') IS NOT NULL THEN
  GRANT USAGE ON SCHEMA reporting TO reporting_svc;
  GRANT SELECT,INSERT ON reporting.collection_facts TO reporting_svc;
 END IF;
END $$;
-- No role creation, password, ownership, superuser, DELETE, TRUNCATE or PUBLIC REVOKE.
