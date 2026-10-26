-- READ-ONLY POST-MIGRATION VERIFICATION.
BEGIN READ ONLY;
SELECT table_name, column_name, data_type, is_nullable, column_default
FROM information_schema.columns
WHERE table_schema = 'public' AND table_name IN ('bills', 'bill_items', 'payments', 'payment_events')
ORDER BY table_name, ordinal_position;
SELECT c.relname AS table_name, con.conname AS constraint_name, con.convalidated AS validated,
       pg_get_constraintdef(con.oid, true) AS definition
FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND c.relname IN ('bills', 'bill_items', 'payments', 'payment_events')
ORDER BY c.relname, con.conname;
SELECT tablename, indexname, indexdef FROM pg_indexes
WHERE schemaname = 'public' AND tablename IN ('bills', 'bill_items', 'payments', 'payment_events')
ORDER BY tablename, indexname;
SELECT 'invalid_indexes' AS check_name, count(*) AS issue_count
FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND NOT i.indisvalid
UNION ALL SELECT 'invalid_bill_sources', count(*) FROM public.bills WHERE (session_id IS NULL) = (order_id IS NULL)
UNION ALL SELECT 'unmapped_bill_branches', count(*) FROM public.bills WHERE branch_id IS NULL
UNION ALL SELECT 'successful_payments_without_audit', count(*) FROM public.payments
WHERE status = 'SUCCEEDED' AND (confirmed_by_user_id IS NULL OR confirmed_at IS NULL OR paid_at IS NULL)
UNION ALL SELECT 'successful_bank_transfers_without_evidence', count(*) FROM public.payments
WHERE method = 'BANK_TRANSFER' AND status = 'SUCCEEDED' AND evidence_reference IS NULL
UNION ALL SELECT 'missing_payment_columns', 14 - count(*)
FROM information_schema.columns
WHERE table_schema = 'public'
  AND ((table_name = 'bills' AND column_name IN (
          'branch_id', 'created_by_user_id', 'print_status', 'printed_at',
          'printed_by_user_id', 'closed_at', 'closed_by_user_id', 'version'))
    OR (table_name = 'payments' AND column_name IN (
          'created_by_user_id', 'confirmed_by_user_id', 'confirmed_at',
          'evidence_reference', 'status_reason', 'version')))
UNION ALL SELECT 'missing_payment_indexes', 8 - count(*)
FROM pg_indexes
WHERE schemaname = 'public'
  AND indexname IN (
      'ux_bills_session_id', 'ux_bills_order_id', 'ix_bills_branch_id',
      'ix_bill_items_bill_id', 'ix_bill_items_order_item_id',
      'ix_payments_bill_id', 'ux_payment_events_request_id',
      'ix_payment_events_payment_occurred_at')
UNION ALL SELECT 'missing_payment_constraints', 28 - count(*)
FROM pg_constraint con
JOIN pg_class relation ON relation.oid = con.conrelid
JOIN pg_namespace namespace ON namespace.oid = relation.relnamespace
WHERE namespace.nspname = 'public'
  AND con.conname IN (
      'fk_bills_branch_id', 'fk_bills_created_by_user_id',
      'fk_bills_printed_by_user_id', 'fk_bills_closed_by_user_id',
      'ck_bills_exactly_one_source', 'ck_bills_status',
      'ck_bills_total_amount', 'ck_bills_version', 'ck_bills_print_status',
      'ck_bills_print_completion', 'ck_bills_close_audit',
      'ck_bill_items_quantity', 'ck_bill_items_amount',
      'fk_payments_created_by_user_id', 'fk_payments_confirmed_by_user_id',
      'ck_payments_method', 'ck_payments_status', 'ck_payments_amount',
      'ck_payments_version', 'ck_payments_success_audit',
      'ck_payments_pending_not_paid', 'ck_payments_bank_transfer_evidence',
      'ck_payments_terminal_reason', 'fk_payment_events_payment_id',
      'fk_payment_events_actor_user_id', 'ck_payment_events_type',
      'ck_payment_events_from_status', 'ck_payment_events_to_status')
UNION ALL SELECT 'missing_append_only_trigger',
    CASE WHEN EXISTS (
        SELECT 1
        FROM pg_trigger trigger
        JOIN pg_class relation ON relation.oid = trigger.tgrelid
        JOIN pg_namespace namespace ON namespace.oid = relation.relnamespace
        WHERE namespace.nspname = 'public'
          AND relation.relname = 'payment_events'
          AND trigger.tgname = 'trg_payment_events_append_only'
          AND NOT trigger.tgisinternal
    ) THEN 0 ELSE 1 END;
COMMIT;
