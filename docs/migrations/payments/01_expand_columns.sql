-- EXPAND PHASE ONLY. Adds backward-compatible columns; no backfill or constraints.
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '60s';
ALTER TABLE public.bills
    ADD COLUMN branch_id uuid,
    ADD COLUMN created_by_user_id uuid,
    ADD COLUMN print_status varchar(30) NOT NULL DEFAULT 'NOT_ISSUED',
    ADD COLUMN printed_at timestamp without time zone,
    ADD COLUMN printed_by_user_id uuid,
    ADD COLUMN closed_at timestamp without time zone,
    ADD COLUMN closed_by_user_id uuid,
    ADD COLUMN version bigint NOT NULL DEFAULT 0;
ALTER TABLE public.payments
    ADD COLUMN created_by_user_id uuid,
    ADD COLUMN confirmed_by_user_id uuid,
    ADD COLUMN confirmed_at timestamp without time zone,
    ADD COLUMN evidence_reference varchar(255),
    ADD COLUMN status_reason varchar(500),
    ADD COLUMN version bigint NOT NULL DEFAULT 0;
COMMIT;
