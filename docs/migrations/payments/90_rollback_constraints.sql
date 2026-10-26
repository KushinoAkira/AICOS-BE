-- Remove contract layer only. Run with explicit approval.
BEGIN;
DROP TRIGGER IF EXISTS trg_payment_events_append_only ON public.payment_events;
DROP FUNCTION IF EXISTS public.reject_payment_event_mutation();
ALTER TABLE public.payment_events DROP CONSTRAINT IF EXISTS ck_payment_events_to_status, DROP CONSTRAINT IF EXISTS ck_payment_events_from_status, DROP CONSTRAINT IF EXISTS ck_payment_events_type, DROP CONSTRAINT IF EXISTS fk_payment_events_actor_user_id, DROP CONSTRAINT IF EXISTS fk_payment_events_payment_id;
ALTER TABLE public.payments DROP CONSTRAINT IF EXISTS ck_payments_terminal_reason, DROP CONSTRAINT IF EXISTS ck_payments_bank_transfer_evidence, DROP CONSTRAINT IF EXISTS ck_payments_pending_not_paid, DROP CONSTRAINT IF EXISTS ck_payments_success_audit, DROP CONSTRAINT IF EXISTS ck_payments_version, DROP CONSTRAINT IF EXISTS ck_payments_amount, DROP CONSTRAINT IF EXISTS ck_payments_status, DROP CONSTRAINT IF EXISTS ck_payments_method, DROP CONSTRAINT IF EXISTS fk_payments_confirmed_by_user_id, DROP CONSTRAINT IF EXISTS fk_payments_created_by_user_id;
ALTER TABLE public.bill_items DROP CONSTRAINT IF EXISTS ck_bill_items_amount, DROP CONSTRAINT IF EXISTS ck_bill_items_quantity;
ALTER TABLE public.bills DROP CONSTRAINT IF EXISTS ck_bills_close_audit, DROP CONSTRAINT IF EXISTS ck_bills_print_completion, DROP CONSTRAINT IF EXISTS ck_bills_print_status, DROP CONSTRAINT IF EXISTS ck_bills_version, DROP CONSTRAINT IF EXISTS ck_bills_total_amount, DROP CONSTRAINT IF EXISTS ck_bills_status, DROP CONSTRAINT IF EXISTS ck_bills_exactly_one_source, DROP CONSTRAINT IF EXISTS fk_bills_closed_by_user_id, DROP CONSTRAINT IF EXISTS fk_bills_printed_by_user_id, DROP CONSTRAINT IF EXISTS fk_bills_created_by_user_id, DROP CONSTRAINT IF EXISTS fk_bills_branch_id;
-- Stage 05 strengthened these baseline columns. Restore their original
-- nullability so an application rollback remains compatible before Stage 92.
ALTER TABLE public.bills
    ALTER COLUMN branch_id DROP NOT NULL,
    ALTER COLUMN created_by_user_id DROP NOT NULL,
    ALTER COLUMN status DROP NOT NULL,
    ALTER COLUMN created_at DROP NOT NULL;
ALTER TABLE public.payments
    ALTER COLUMN created_by_user_id DROP NOT NULL,
    ALTER COLUMN status DROP NOT NULL,
    ALTER COLUMN created_at DROP NOT NULL;
COMMIT;
