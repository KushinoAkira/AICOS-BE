-- PROPOSAL: verified target/cluster and separate approval required.
DO $$ BEGIN
 IF current_setting('aicos.expected_database',true) IS NULL OR current_database()<>current_setting('aicos.expected_database',true) OR current_database()='aicos_db' THEN RAISE EXCEPTION 'Operations database guard failed'; END IF;
 IF current_setting('aicos.expected_cluster',true) IS NULL OR (SELECT system_identifier::text FROM pg_control_system())<>current_setting('aicos.expected_cluster',true) THEN RAISE EXCEPTION 'Operations cluster guard failed'; END IF;
 IF current_setting('aicos.operations_grants_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Operations grants approval required'; END IF;
END $$;
DO $$ BEGIN
 IF to_regclass('ordering.table_transfer_operations') IS NOT NULL THEN
  GRANT SELECT,INSERT ON ordering.table_transfer_operations TO ordering_svc;
  GRANT UPDATE(synced_at_utc) ON ordering.table_transfer_operations TO ordering_svc;
 END IF;
 IF to_regclass('payment.bill_split_plans') IS NOT NULL THEN
  GRANT SELECT,INSERT ON payment.bill_split_plans TO payment_svc;
  GRANT UPDATE(version,fingerprint,quote_json,groups_json,updated_by,updated_at_utc) ON payment.bill_split_plans TO payment_svc;
  GRANT SELECT,INSERT ON payment.bill_split_operations TO payment_svc;
 END IF;
 IF to_regclass('kitchen.kitchen_tickets') IS NOT NULL THEN
  GRANT SELECT(table_transfer_version),UPDATE(table_transfer_version) ON kitchen.kitchen_tickets TO kitchen_svc;
 END IF;
END $$;
-- Existing table_number UPDATE and ticket SELECT rights must already match the reviewed Kitchen ACL. No DELETE/TRUNCATE, role creation, password or ownership change. UUID keys require no sequence grant.
