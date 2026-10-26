-- Run only after the approved application version writes every required field.
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
ALTER TABLE public.bills
    ADD CONSTRAINT fk_bills_branch_id FOREIGN KEY (branch_id) REFERENCES public.branches(id) ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE NOT VALID,
    ADD CONSTRAINT fk_bills_created_by_user_id FOREIGN KEY (created_by_user_id) REFERENCES public.users(id) ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE NOT VALID,
    ADD CONSTRAINT fk_bills_printed_by_user_id FOREIGN KEY (printed_by_user_id) REFERENCES public.users(id) ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE NOT VALID,
    ADD CONSTRAINT fk_bills_closed_by_user_id FOREIGN KEY (closed_by_user_id) REFERENCES public.users(id) ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE NOT VALID,
    ADD CONSTRAINT ck_bills_exactly_one_source CHECK ((session_id IS NULL) <> (order_id IS NULL)) NOT VALID,
    ADD CONSTRAINT ck_bills_status CHECK (status IN ('UNPAID', 'PAID', 'CLOSED')) NOT VALID,
    ADD CONSTRAINT ck_bills_total_amount CHECK (total_amount > 0) NOT VALID,
    ADD CONSTRAINT ck_bills_version CHECK (version >= 0) NOT VALID,
    ADD CONSTRAINT ck_bills_print_status CHECK (print_status IN ('NOT_ISSUED', 'PRINTING', 'PRINTED', 'FAILED')) NOT VALID,
    ADD CONSTRAINT ck_bills_print_completion CHECK (print_status <> 'PRINTED' OR (printed_at IS NOT NULL AND printed_by_user_id IS NOT NULL)) NOT VALID,
    ADD CONSTRAINT ck_bills_close_audit CHECK (status <> 'CLOSED' OR (closed_at IS NOT NULL AND closed_by_user_id IS NOT NULL)) NOT VALID;
ALTER TABLE public.bill_items
    ADD CONSTRAINT ck_bill_items_quantity CHECK (quantity > 0) NOT VALID,
    ADD CONSTRAINT ck_bill_items_amount CHECK (amount >= 0) NOT VALID;
ALTER TABLE public.payments
    ADD CONSTRAINT fk_payments_created_by_user_id FOREIGN KEY (created_by_user_id) REFERENCES public.users(id) ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE NOT VALID,
    ADD CONSTRAINT fk_payments_confirmed_by_user_id FOREIGN KEY (confirmed_by_user_id) REFERENCES public.users(id) ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE NOT VALID,
    ADD CONSTRAINT ck_payments_method CHECK (method IN ('CASH', 'BANK_TRANSFER')) NOT VALID,
    ADD CONSTRAINT ck_payments_status CHECK (status IN ('PENDING', 'SUCCEEDED', 'FAILED', 'CANCELLED')) NOT VALID,
    ADD CONSTRAINT ck_payments_amount CHECK (amount > 0) NOT VALID,
    ADD CONSTRAINT ck_payments_version CHECK (version >= 0) NOT VALID,
    ADD CONSTRAINT ck_payments_success_audit CHECK (status <> 'SUCCEEDED' OR (confirmed_by_user_id IS NOT NULL AND confirmed_at IS NOT NULL AND paid_at IS NOT NULL)) NOT VALID,
    ADD CONSTRAINT ck_payments_pending_not_paid CHECK (status <> 'PENDING' OR paid_at IS NULL) NOT VALID,
    ADD CONSTRAINT ck_payments_bank_transfer_evidence CHECK (method <> 'BANK_TRANSFER' OR status <> 'SUCCEEDED' OR evidence_reference IS NOT NULL) NOT VALID,
    ADD CONSTRAINT ck_payments_terminal_reason CHECK (status NOT IN ('FAILED', 'CANCELLED') OR status_reason IS NOT NULL) NOT VALID;
ALTER TABLE public.payment_events
    ADD CONSTRAINT fk_payment_events_payment_id FOREIGN KEY (payment_id) REFERENCES public.payments(id) ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE NOT VALID,
    ADD CONSTRAINT fk_payment_events_actor_user_id FOREIGN KEY (actor_user_id) REFERENCES public.users(id) ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE NOT VALID,
    ADD CONSTRAINT ck_payment_events_type CHECK (event_type IN ('CREATED', 'CONFIRMED', 'FAILED', 'CANCELLED')) NOT VALID,
    ADD CONSTRAINT ck_payment_events_from_status CHECK (from_status IS NULL OR from_status IN ('PENDING', 'SUCCEEDED', 'FAILED', 'CANCELLED')) NOT VALID,
    ADD CONSTRAINT ck_payment_events_to_status CHECK (to_status IN ('PENDING', 'SUCCEEDED', 'FAILED', 'CANCELLED')) NOT VALID;
ALTER TABLE public.bills VALIDATE CONSTRAINT ck_bills_exactly_one_source;
ALTER TABLE public.bills VALIDATE CONSTRAINT ck_bills_status;
ALTER TABLE public.bills VALIDATE CONSTRAINT ck_bills_total_amount;
ALTER TABLE public.bills VALIDATE CONSTRAINT ck_bills_version;
ALTER TABLE public.bills VALIDATE CONSTRAINT ck_bills_print_status;
ALTER TABLE public.bills VALIDATE CONSTRAINT ck_bills_print_completion;
ALTER TABLE public.bills VALIDATE CONSTRAINT ck_bills_close_audit;
ALTER TABLE public.bill_items VALIDATE CONSTRAINT ck_bill_items_quantity;
ALTER TABLE public.bill_items VALIDATE CONSTRAINT ck_bill_items_amount;
ALTER TABLE public.payments VALIDATE CONSTRAINT ck_payments_method;
ALTER TABLE public.payments VALIDATE CONSTRAINT ck_payments_status;
ALTER TABLE public.payments VALIDATE CONSTRAINT ck_payments_amount;
ALTER TABLE public.payments VALIDATE CONSTRAINT ck_payments_version;
ALTER TABLE public.payments VALIDATE CONSTRAINT ck_payments_success_audit;
ALTER TABLE public.payments VALIDATE CONSTRAINT ck_payments_pending_not_paid;
ALTER TABLE public.payments VALIDATE CONSTRAINT ck_payments_bank_transfer_evidence;
ALTER TABLE public.payments VALIDATE CONSTRAINT ck_payments_terminal_reason;
ALTER TABLE public.payment_events VALIDATE CONSTRAINT ck_payment_events_type;
ALTER TABLE public.payment_events VALIDATE CONSTRAINT ck_payment_events_from_status;
ALTER TABLE public.payment_events VALIDATE CONSTRAINT ck_payment_events_to_status;
ALTER TABLE public.bills VALIDATE CONSTRAINT fk_bills_branch_id;
ALTER TABLE public.bills VALIDATE CONSTRAINT fk_bills_created_by_user_id;
ALTER TABLE public.bills VALIDATE CONSTRAINT fk_bills_printed_by_user_id;
ALTER TABLE public.bills VALIDATE CONSTRAINT fk_bills_closed_by_user_id;
ALTER TABLE public.payments VALIDATE CONSTRAINT fk_payments_created_by_user_id;
ALTER TABLE public.payments VALIDATE CONSTRAINT fk_payments_confirmed_by_user_id;
ALTER TABLE public.payment_events VALIDATE CONSTRAINT fk_payment_events_payment_id;
ALTER TABLE public.payment_events VALIDATE CONSTRAINT fk_payment_events_actor_user_id;
ALTER TABLE public.bills ALTER COLUMN branch_id SET NOT NULL, ALTER COLUMN created_by_user_id SET NOT NULL, ALTER COLUMN status SET NOT NULL, ALTER COLUMN created_at SET NOT NULL;
ALTER TABLE public.payments ALTER COLUMN created_by_user_id SET NOT NULL, ALTER COLUMN status SET NOT NULL, ALTER COLUMN created_at SET NOT NULL;

CREATE FUNCTION public.reject_payment_event_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'payment_events is append-only';
END;
$$;

CREATE TRIGGER trg_payment_events_append_only
BEFORE UPDATE OR DELETE ON public.payment_events
FOR EACH ROW EXECUTE FUNCTION public.reject_payment_event_mutation();
COMMIT;
