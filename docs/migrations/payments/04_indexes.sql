-- CREATE INDEX CONCURRENTLY must not run inside a transaction.
CREATE UNIQUE INDEX CONCURRENTLY ux_bills_session_id ON public.bills(session_id) WHERE session_id IS NOT NULL;
CREATE UNIQUE INDEX CONCURRENTLY ux_bills_order_id ON public.bills(order_id) WHERE order_id IS NOT NULL;
CREATE INDEX CONCURRENTLY ix_bills_branch_id ON public.bills(branch_id);
CREATE INDEX CONCURRENTLY ix_bill_items_bill_id ON public.bill_items(bill_id);
CREATE INDEX CONCURRENTLY ix_bill_items_order_item_id ON public.bill_items(order_item_id);
CREATE INDEX CONCURRENTLY ix_payments_bill_id ON public.payments(bill_id);
CREATE UNIQUE INDEX CONCURRENTLY ux_payment_events_request_id ON public.payment_events(request_id);
CREATE INDEX CONCURRENTLY ix_payment_events_payment_occurred_at ON public.payment_events(payment_id, occurred_at);
