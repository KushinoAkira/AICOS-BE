BEGIN READ ONLY;
SELECT current_database(), current_user, system_identifier::text FROM pg_control_system();
DO $$ DECLARE s text; t text; base text; migration text; expected integer; found boolean; applied boolean; BEGIN
  IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN
    RAISE EXCEPTION 'Wrong or unverified cluster';
  END IF;
  IF current_database()='aicos_ordering' THEN
    s:='ordering'; t:='order_cancellation_operations'; base:='20261118143317_OrderingSessionManualDiscount'; migration:='20261118161648_OrderingSafeOrderCancellation'; expected:=22;
  ELSIF current_database()='aicos_kitchen' THEN
    s:='kitchen'; t:='order_cancellation_receipts'; base:='20261114164849_TableTransferRoutingVersion'; migration:='20261118161652_KitchenSafeOrderCancellation'; expected:=10;
  ELSE RAISE EXCEPTION 'Wrong service database'; END IF;
  IF to_regclass(format('%I.__ef_migrations_history',s)) IS NULL THEN RAISE EXCEPTION 'Missing baseline history'; END IF;
  IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema=s AND table_name IN ('orders','kitchen_tickets')) THEN RAISE EXCEPTION 'Missing service baseline'; END IF;
  IF NOT (SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname=s)) THEN RAISE EXCEPTION 'Missing schema'; END IF;
  -- Dynamic SQL reads only the verified service schema.
  EXECUTE format('SELECT EXISTS(SELECT 1 FROM %I.__ef_migrations_history WHERE migration_id=$1)',s) INTO STRICT found USING base;
  IF NOT found THEN RAISE EXCEPTION 'Required baseline migration missing'; END IF;
  IF s='ordering' THEN
    EXECUTE 'SELECT EXISTS(SELECT 1 FROM ordering.__ef_migrations_history WHERE migration_id=$1)' INTO STRICT found USING '20261113093202_OrderBasketsAssociationRules';
    IF NOT found THEN RAISE EXCEPTION 'Merged main requires separately reviewed Apriori baseline; do not deploy against missing tables'; END IF;
  END IF;
  EXECUTE format('SELECT EXISTS(SELECT 1 FROM %I.__ef_migrations_history WHERE migration_id=$1)',s) INTO STRICT applied USING migration;
  found := to_regclass(format('%I.%I',s,t)) IS NOT NULL;
  IF found <> applied THEN RAISE EXCEPTION 'Table/history mismatch; do not recreate'; END IF;
  IF found AND (SELECT count(*) FROM information_schema.columns WHERE table_schema=s AND table_name=t) <> expected THEN
    RAISE EXCEPTION 'Unexpected existing table shape; stop and compare types/constraints/indexes';
  END IF;
END $$;
-- Run the per-service guarded expand only after this inspection and review of exact baseline/history.
SELECT table_schema,table_name,column_name,data_type,is_nullable FROM information_schema.columns
WHERE table_schema IN ('ordering','kitchen') AND table_name IN ('order_cancellation_operations','order_cancellation_receipts') ORDER BY table_schema,ordinal_position;
SELECT schemaname,tablename,indexname,indexdef FROM pg_indexes WHERE tablename IN ('order_cancellation_operations','order_cancellation_receipts');
SELECT n.nspname,c.relname,p.conname,pg_get_constraintdef(p.oid) FROM pg_constraint p JOIN pg_class c ON c.oid=p.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE c.relname IN ('order_cancellation_operations','order_cancellation_receipts');
COMMIT;
