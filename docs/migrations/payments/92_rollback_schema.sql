-- DESTRUCTIVE. Do not run after payment mutations begin; export audit data first.
BEGIN;
DROP TABLE IF EXISTS public.payment_events;
ALTER TABLE public.payments DROP COLUMN IF EXISTS version, DROP COLUMN IF EXISTS status_reason, DROP COLUMN IF EXISTS evidence_reference, DROP COLUMN IF EXISTS confirmed_at, DROP COLUMN IF EXISTS confirmed_by_user_id, DROP COLUMN IF EXISTS created_by_user_id;
ALTER TABLE public.bills DROP COLUMN IF EXISTS version, DROP COLUMN IF EXISTS closed_by_user_id, DROP COLUMN IF EXISTS closed_at, DROP COLUMN IF EXISTS printed_by_user_id, DROP COLUMN IF EXISTS printed_at, DROP COLUMN IF EXISTS print_status, DROP COLUMN IF EXISTS created_by_user_id, DROP COLUMN IF EXISTS branch_id;
COMMIT;
