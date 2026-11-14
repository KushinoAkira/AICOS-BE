START TRANSACTION;
DO $guard$
BEGIN
 IF current_setting('aicos.expected_database',true) IS NULL OR current_database() <> current_setting('aicos.expected_database',true) THEN RAISE EXCEPTION 'Database identity mismatch'; END IF;
 IF current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'kitchen' THEN RAISE EXCEPTION 'Kitchen schema required'; END IF;
 IF current_setting('aicos.expected_cluster',true) IS NULL OR (SELECT system_identifier::text FROM pg_control_system()) <> current_setting('aicos.expected_cluster',true) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
END $guard$;
DO $approval$ BEGIN IF current_setting('aicos.barista_expand_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Explicit expand approval required'; END IF; END $approval$;


CREATE TABLE kitchen.barista_acceptances (
    ticket_id uuid NOT NULL,
    cycle integer NOT NULL,
    tenant_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    accepted_by uuid NOT NULL,
    key_hash character varying(64) NOT NULL,
    accepted_name character varying(255) NOT NULL,
    accepted_at_utc timestamp with time zone NOT NULL,
    request_hash character varying(64) NOT NULL,
    completed_at_utc timestamp with time zone,
    completed_by uuid,
    CONSTRAINT pk_barista_acceptances PRIMARY KEY (ticket_id, cycle),
    CONSTRAINT ck_barista_cycle CHECK (cycle >= 0 AND (completed_at_utc IS NULL OR completed_at_utc >= accepted_at_utc)),
    CONSTRAINT fk_barista_acceptances_tickets_ticket_id FOREIGN KEY (ticket_id) REFERENCES kitchen.kitchen_tickets (id) ON DELETE RESTRICT
);

CREATE TABLE kitchen.kitchen_print_jobs (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    ticket_id uuid NOT NULL,
    cycle integer NOT NULL,
    requested_by uuid NOT NULL,
    reprint_of uuid,
    key_hash character varying(64) NOT NULL,
    request_hash character varying(64) NOT NULL,
    kind character varying(16) NOT NULL,
    status character varying(16) NOT NULL,
    payload jsonb NOT NULL,
    payload_hash character varying(64) NOT NULL,
    table_snapshot character varying(20),
    created_at_utc timestamp with time zone NOT NULL,
    started_at_utc timestamp with time zone,
    printed_at_utc timestamp with time zone,
    test_output boolean NOT NULL,
    error_code character varying(80),
    lease_owner uuid,
    lease_until_utc timestamp with time zone,
    fence bigint NOT NULL,
    CONSTRAINT pk_kitchen_print_jobs PRIMARY KEY (id),
    CONSTRAINT ck_kitchen_print_ack CHECK ((status = 'PRINTED' AND printed_at_utc IS NOT NULL) OR (status <> 'PRINTED' AND printed_at_utc IS NULL)),
    CONSTRAINT ck_kitchen_print_kind CHECK (kind IN ('SLIP','LABELS')),
    CONSTRAINT ck_kitchen_print_state CHECK (status IN ('QUEUED','PRINTING','PRINTED','FAILED','UNKNOWN') AND fence >= 0),
    CONSTRAINT fk_kitchen_print_jobs_kitchen_print_jobs_reprint_of FOREIGN KEY (reprint_of) REFERENCES kitchen.kitchen_print_jobs (id) ON DELETE RESTRICT,
    CONSTRAINT fk_kitchen_print_jobs_tickets_ticket_id FOREIGN KEY (ticket_id) REFERENCES kitchen.kitchen_tickets (id) ON DELETE RESTRICT
);

CREATE UNIQUE INDEX ix_barista_acceptances_tenant_id_accepted_by_key_hash ON kitchen.barista_acceptances (tenant_id, accepted_by, key_hash);

CREATE INDEX ix_barista_acceptances_tenant_id_branch_id_accepted_at_utc ON kitchen.barista_acceptances (tenant_id, branch_id, accepted_at_utc);

CREATE INDEX ix_kitchen_print_jobs_reprint_of ON kitchen.kitchen_print_jobs (reprint_of);

CREATE INDEX ix_kitchen_print_jobs_status_created_at_utc ON kitchen.kitchen_print_jobs (status, created_at_utc);

CREATE UNIQUE INDEX ix_kitchen_print_jobs_tenant_id_requested_by_key_hash ON kitchen.kitchen_print_jobs (tenant_id, requested_by, key_hash);

CREATE UNIQUE INDEX ix_kitchen_print_jobs_ticket_id_cycle ON kitchen.kitchen_print_jobs (ticket_id, cycle) WHERE kind = 'SLIP' AND reprint_of IS NULL;

INSERT INTO kitchen.__ef_migrations_history (migration_id, product_version)
VALUES ('20261114041318_KitchenBaristaWorkflow', '8.0.31');

COMMIT;
