-- Run only while bill/payment writes are paused or after dual-write deployment.
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
UPDATE public.bills AS bill SET branch_id = source.branch_id
FROM public.orders AS source
WHERE bill.branch_id IS NULL AND bill.order_id = source.id;
UPDATE public.bills AS bill SET branch_id = source.branch_id
FROM public.table_sessions AS source
WHERE bill.branch_id IS NULL AND bill.session_id = source.id;
UPDATE public.bills SET status = 'UNPAID' WHERE status IS NULL;
UPDATE public.payments SET status = 'PENDING' WHERE status IS NULL;
-- Actor columns are deliberately not fabricated.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM public.bills WHERE branch_id IS NULL) THEN
        RAISE EXCEPTION 'Backfill incomplete: bills.branch_id still contains null';
    END IF;
    IF EXISTS (SELECT 1 FROM public.bills WHERE created_by_user_id IS NULL)
       OR EXISTS (SELECT 1 FROM public.payments WHERE created_by_user_id IS NULL) THEN
        RAISE EXCEPTION 'Audit actor backfill requires manual review; no actor was fabricated';
    END IF;
END $$;
COMMIT;
