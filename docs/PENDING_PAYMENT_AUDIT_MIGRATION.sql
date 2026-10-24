-- REVIEW ONLY - NOT APPLIED
-- Purpose: add branch/audit/print fields required before enabling payment confirmation.
-- A backup must be created and this SQL must be approved before execution.

BEGIN;

LOCK TABLE public.bills IN SHARE ROW EXCLUSIVE MODE;
LOCK TABLE public.payments IN SHARE ROW EXCLUSIVE MODE;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM public.bills
        WHERE session_id IS NOT NULL
        GROUP BY session_id
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'Duplicate bills exist for at least one table session';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM public.bills
        WHERE order_id IS NOT NULL
        GROUP BY order_id
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'Duplicate bills exist for at least one order';
    END IF;
END $$;

ALTER TABLE public.bills
    ADD COLUMN IF NOT EXISTS branch_id uuid,
    ADD COLUMN IF NOT EXISTS created_by_user_id uuid,
    ADD COLUMN IF NOT EXISTS print_status varchar(30) NOT NULL DEFAULT 'NOT_ISSUED',
    ADD COLUMN IF NOT EXISTS printed_at timestamp without time zone,
    ADD COLUMN IF NOT EXISTS printed_by_user_id uuid;

UPDATE public.bills AS bill
SET branch_id = source.branch_id
FROM public.orders AS source
WHERE bill.branch_id IS NULL
  AND bill.order_id = source.id;

UPDATE public.bills AS bill
SET branch_id = source.branch_id
FROM public.table_sessions AS source
WHERE bill.branch_id IS NULL
  AND bill.session_id = source.id;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM public.bills WHERE branch_id IS NULL) THEN
        RAISE EXCEPTION 'Some existing bills cannot be mapped to a branch';
    END IF;
END $$;

ALTER TABLE public.bills
    ALTER COLUMN branch_id SET NOT NULL;

ALTER TABLE public.payments
    ADD COLUMN IF NOT EXISTS created_by_user_id uuid,
    ADD COLUMN IF NOT EXISTS confirmed_by_user_id uuid;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_bills_branch_id'
    ) THEN
        ALTER TABLE public.bills
            ADD CONSTRAINT fk_bills_branch_id
            FOREIGN KEY (branch_id) REFERENCES public.branches(id)
            ON DELETE NO ACTION;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_bills_created_by_user_id'
    ) THEN
        ALTER TABLE public.bills
            ADD CONSTRAINT fk_bills_created_by_user_id
            FOREIGN KEY (created_by_user_id) REFERENCES public.users(id)
            ON DELETE NO ACTION;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_bills_printed_by_user_id'
    ) THEN
        ALTER TABLE public.bills
            ADD CONSTRAINT fk_bills_printed_by_user_id
            FOREIGN KEY (printed_by_user_id) REFERENCES public.users(id)
            ON DELETE NO ACTION;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_payments_created_by_user_id'
    ) THEN
        ALTER TABLE public.payments
            ADD CONSTRAINT fk_payments_created_by_user_id
            FOREIGN KEY (created_by_user_id) REFERENCES public.users(id)
            ON DELETE NO ACTION;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_payments_confirmed_by_user_id'
    ) THEN
        ALTER TABLE public.payments
            ADD CONSTRAINT fk_payments_confirmed_by_user_id
            FOREIGN KEY (confirmed_by_user_id) REFERENCES public.users(id)
            ON DELETE NO ACTION;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_bills_print_status'
    ) THEN
        ALTER TABLE public.bills
            ADD CONSTRAINT ck_bills_print_status
            CHECK (print_status IN ('NOT_ISSUED', 'PRINTING', 'PRINTED', 'FAILED'));
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_bills_session_id
    ON public.bills(session_id)
    WHERE session_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_bills_order_id
    ON public.bills(order_id)
    WHERE order_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_bills_branch_id
    ON public.bills(branch_id);

COMMIT;
