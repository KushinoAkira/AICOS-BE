-- Cannot run inside a transaction because these drops are concurrent.
DROP INDEX CONCURRENTLY IF EXISTS public.ix_payment_events_payment_occurred_at;
DROP INDEX CONCURRENTLY IF EXISTS public.ux_payment_events_request_id;
DROP INDEX CONCURRENTLY IF EXISTS public.ix_payments_bill_id;
DROP INDEX CONCURRENTLY IF EXISTS public.ix_bill_items_order_item_id;
DROP INDEX CONCURRENTLY IF EXISTS public.ix_bill_items_bill_id;
DROP INDEX CONCURRENTLY IF EXISTS public.ix_bills_branch_id;
DROP INDEX CONCURRENTLY IF EXISTS public.ux_bills_order_id;
DROP INDEX CONCURRENTLY IF EXISTS public.ux_bills_session_id;
