-- Payments-owned append-only audit table.
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '60s';
CREATE TABLE public.payment_events (
    id uuid PRIMARY KEY,
    payment_id uuid NOT NULL,
    request_id uuid NOT NULL,
    event_type varchar(30) NOT NULL,
    from_status varchar(30),
    to_status varchar(30) NOT NULL,
    actor_user_id uuid NOT NULL,
    evidence_reference varchar(255),
    reason varchar(500),
    correlation_id varchar(100),
    occurred_at timestamp without time zone NOT NULL DEFAULT now()
);
COMMIT;
