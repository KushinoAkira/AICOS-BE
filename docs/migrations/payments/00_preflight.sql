-- READ-ONLY PREFLIGHT. Safe to run before approval because it cannot mutate data.
BEGIN READ ONLY;
DO $$
BEGIN
    IF current_schema() <> 'public' THEN
        RAISE EXCEPTION 'Expected current schema public, got %', current_schema();
    END IF;
    IF to_regclass('public.bills') IS NULL OR to_regclass('public.bill_items') IS NULL
       OR to_regclass('public.payments') IS NULL THEN
        RAISE EXCEPTION 'Required Payments tables are missing from public schema';
    END IF;
    IF to_regclass('public.payment_events') IS NOT NULL THEN
        RAISE EXCEPTION 'payment_events already exists; inspect schema before continuing';
    END IF;
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public'
          AND ((table_name = 'bills' AND column_name IN (
                  'branch_id', 'created_by_user_id', 'print_status', 'printed_at',
                  'printed_by_user_id', 'closed_at', 'closed_by_user_id', 'version'))
            OR (table_name = 'payments' AND column_name IN (
                  'created_by_user_id', 'confirmed_by_user_id', 'confirmed_at',
                  'evidence_reference', 'status_reason', 'version')))
    ) THEN
        RAISE EXCEPTION 'At least one proposed column already exists; inspect its exact definition';
    END IF;
    IF EXISTS (SELECT 1 FROM public.bills WHERE (session_id IS NULL) = (order_id IS NULL)) THEN
        RAISE EXCEPTION 'A bill must reference exactly one session or order';
    END IF;
    IF EXISTS (SELECT session_id FROM public.bills WHERE session_id IS NOT NULL GROUP BY session_id HAVING count(*) > 1) THEN
        RAISE EXCEPTION 'Duplicate bills exist for at least one table session';
    END IF;
    IF EXISTS (SELECT order_id FROM public.bills WHERE order_id IS NOT NULL GROUP BY order_id HAVING count(*) > 1) THEN
        RAISE EXCEPTION 'Duplicate bills exist for at least one takeaway order';
    END IF;
    IF EXISTS (SELECT 1 FROM public.bills WHERE status IS NULL OR status NOT IN ('UNPAID', 'PAID', 'CLOSED')) THEN
        RAISE EXCEPTION 'Unsupported or null bill status exists';
    END IF;
    IF EXISTS (SELECT 1 FROM public.payments WHERE status IS NULL OR status NOT IN ('PENDING', 'SUCCEEDED', 'FAILED', 'CANCELLED')) THEN
        RAISE EXCEPTION 'Unsupported or null payment status exists';
    END IF;
    IF EXISTS (SELECT 1 FROM public.payments WHERE method NOT IN ('CASH', 'BANK_TRANSFER')) THEN
        RAISE EXCEPTION 'Unsupported payment method exists';
    END IF;
    IF EXISTS (SELECT 1 FROM public.bills WHERE total_amount <= 0)
       OR EXISTS (SELECT 1 FROM public.bill_items WHERE quantity <= 0 OR amount < 0)
       OR EXISTS (SELECT 1 FROM public.payments WHERE amount <= 0) THEN
        RAISE EXCEPTION 'Invalid bill, bill item, or payment amount exists';
    END IF;
    IF EXISTS (SELECT 1 FROM public.bills WHERE created_at IS NULL)
       OR EXISTS (SELECT 1 FROM public.payments WHERE created_at IS NULL) THEN
        RAISE EXCEPTION 'A legacy financial row has no creation timestamp';
    END IF;
    IF EXISTS (
        SELECT 1 FROM public.bills b
        LEFT JOIN public.orders o ON o.id = b.order_id
        LEFT JOIN public.table_sessions s ON s.id = b.session_id
        WHERE COALESCE(o.branch_id, s.branch_id) IS NULL
    ) THEN
        RAISE EXCEPTION 'At least one bill cannot be mapped to a branch';
    END IF;
END $$;
SELECT current_database() AS database_name, current_schema() AS schema_name,
       current_setting('transaction_read_only') AS transaction_read_only,
       (SELECT count(*) FROM public.bills) AS bill_count,
       (SELECT count(*) FROM public.bill_items) AS bill_item_count,
       (SELECT count(*) FROM public.payments) AS payment_count;
COMMIT;
