START TRANSACTION;

ALTER TABLE kitchen.kitchen_tickets ADD fulfillment_mode character varying(20) NOT NULL DEFAULT 'WholeOrder';

ALTER TABLE kitchen.kitchen_tickets ADD fulfillment_version bigint NOT NULL DEFAULT 0;

ALTER TABLE kitchen.kitchen_tickets ADD source_session_id uuid;

ALTER TABLE kitchen.kitchen_tickets ADD source_table_id uuid;

ALTER TABLE kitchen.kitchen_ticket_items ADD order_item_id uuid;

ALTER TABLE kitchen.cup_stickers ADD cup_task_id uuid;

CREATE TABLE kitchen.cup_tasks (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    order_id uuid NOT NULL,
    ticket_id uuid NOT NULL,
    ticket_item_id uuid NOT NULL,
    order_item_id uuid NOT NULL,
    unit_index integer NOT NULL,
    order_cup_index integer NOT NULL,
    preparation character varying(20) NOT NULL,
    service character varying(20) NOT NULL,
    preparation_cycle integer NOT NULL,
    version bigint NOT NULL,
    fence bigint NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    ready_at_utc timestamp with time zone,
    prepared_by uuid,
    picked_up_at_utc timestamp with time zone,
    served_at_utc timestamp with time zone,
    served_by uuid,
    review_reason character varying(200),
    CONSTRAINT pk_cup_tasks PRIMARY KEY (id),
    CONSTRAINT ck_cup_task_numbers CHECK (unit_index > 0 AND order_cup_index > 0 AND preparation_cycle > 0 AND version >= 0 AND fence >= 0),
    CONSTRAINT ck_cup_task_ready CHECK (service NOT IN ('Available','Reserved','Carrying','Served') OR (preparation = 'Ready' AND ready_at_utc IS NOT NULL)),
    CONSTRAINT ck_cup_task_review CHECK ((service = 'Review') = (review_reason IS NOT NULL)),
    CONSTRAINT ck_cup_task_served CHECK ((service = 'Served') = (served_at_utc IS NOT NULL AND served_by IS NOT NULL)),
    CONSTRAINT ck_cup_task_states CHECK (preparation IN ('Pending','Brewing','Ready') AND service IN ('Waiting','Available','Reserved','Carrying','Served','Review')),
    CONSTRAINT fk_cup_tasks_ticket_items_ticket_item_id FOREIGN KEY (ticket_item_id) REFERENCES kitchen.kitchen_ticket_items (id) ON DELETE RESTRICT,
    CONSTRAINT fk_cup_tasks_tickets_ticket_id FOREIGN KEY (ticket_id) REFERENCES kitchen.kitchen_tickets (id) ON DELETE RESTRICT
);

CREATE TABLE kitchen.fulfillment_outbox (
    event_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    order_id uuid NOT NULL,
    source_version bigint NOT NULL,
    payload jsonb NOT NULL,
    payload_hash character varying(64) NOT NULL,
    state character varying(20) NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    next_attempt_at_utc timestamp with time zone NOT NULL,
    attempt_count integer NOT NULL,
    lease_token uuid,
    lease_until_utc timestamp with time zone,
    acknowledged_at_utc timestamp with time zone,
    last_error_code character varying(100),
    CONSTRAINT pk_fulfillment_outbox PRIMARY KEY (event_id),
    CONSTRAINT ck_fulfillment_outbox_ack CHECK ((state = 'ACKED') = (acknowledged_at_utc IS NOT NULL)),
    CONSTRAINT ck_fulfillment_outbox_lease CHECK ((state = 'IN_FLIGHT' AND lease_token IS NOT NULL AND lease_until_utc IS NOT NULL) OR (state <> 'IN_FLIGHT' AND lease_token IS NULL AND lease_until_utc IS NULL)),
    CONSTRAINT ck_fulfillment_outbox_payload CHECK (jsonb_typeof(payload) = 'object' AND length(payload_hash) = 64),
    CONSTRAINT ck_fulfillment_outbox_state CHECK (state IN ('PENDING','IN_FLIGHT','ACKED','NEEDS_REVIEW') AND source_version > 0 AND attempt_count >= 0)
);

CREATE TABLE kitchen.cup_claims (
    id uuid NOT NULL,
    cup_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    owner_user_id uuid NOT NULL,
    preparation_cycle integer NOT NULL,
    fence bigint NOT NULL,
    version bigint NOT NULL,
    state character varying(20) NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    lease_expires_at_utc timestamp with time zone NOT NULL,
    picked_up_at_utc timestamp with time zone,
    ended_at_utc timestamp with time zone,
    CONSTRAINT pk_cup_claims PRIMARY KEY (id),
    CONSTRAINT ck_cup_claim_lease CHECK (lease_expires_at_utc > created_at_utc AND preparation_cycle > 0 AND fence > 0 AND version >= 0),
    CONSTRAINT ck_cup_claim_state CHECK (state IN ('Reserved','Carrying','Overdue','Served','Released','Overridden')),
    CONSTRAINT ck_cup_claim_terminal CHECK ((state IN ('Served','Released','Overridden')) = (ended_at_utc IS NOT NULL)),
    CONSTRAINT fk_cup_claims_cup_tasks_cup_id FOREIGN KEY (cup_id) REFERENCES kitchen.cup_tasks (id) ON DELETE RESTRICT
);

CREATE TABLE kitchen.cup_operations (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    cup_id uuid NOT NULL,
    actor_id uuid NOT NULL,
    operation_key character varying(80) NOT NULL,
    command character varying(40) NOT NULL,
    request_hash character varying(64) NOT NULL,
    version_before bigint NOT NULL,
    version_after bigint NOT NULL,
    before_json jsonb NOT NULL,
    after_json jsonb NOT NULL,
    result_json jsonb NOT NULL,
    reason character varying(500),
    committed_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_cup_operations PRIMARY KEY (id),
    CONSTRAINT ck_cup_operation_audit CHECK (version_after > version_before AND version_before >= 0 AND length(operation_key) > 0 AND length(request_hash) = 64),
    CONSTRAINT ck_cup_operation_json CHECK (jsonb_typeof(before_json) = 'object' AND jsonb_typeof(after_json) = 'object' AND jsonb_typeof(result_json) = 'object'),
    CONSTRAINT fk_cup_operations_cup_tasks_cup_id FOREIGN KEY (cup_id) REFERENCES kitchen.cup_tasks (id) ON DELETE RESTRICT
);

ALTER TABLE kitchen.kitchen_tickets ADD CONSTRAINT ck_ticket_fulfillment_mode CHECK (fulfillment_mode IN ('WholeOrder','CupV1') AND fulfillment_version >= 0);

CREATE UNIQUE INDEX ix_kitchen_ticket_items_ticket_id_order_item_id ON kitchen.kitchen_ticket_items (ticket_id, order_item_id) WHERE order_item_id IS NOT NULL;

CREATE UNIQUE INDEX ix_cup_stickers_cup_task_id ON kitchen.cup_stickers (cup_task_id) WHERE cup_task_id IS NOT NULL;

CREATE INDEX ix_cup_claims_state_lease_expires_at_utc ON kitchen.cup_claims (state, lease_expires_at_utc);

CREATE INDEX ix_cup_claims_tenant_id_branch_id_owner_user_id_state ON kitchen.cup_claims (tenant_id, branch_id, owner_user_id, state);

CREATE UNIQUE INDEX uq_cup_claim_live ON kitchen.cup_claims (cup_id) WHERE state IN ('Reserved','Carrying','Overdue');

CREATE INDEX ix_cup_operations_cup_id_committed_at_utc ON kitchen.cup_operations (cup_id, committed_at_utc);

CREATE UNIQUE INDEX ix_cup_operations_tenant_id_actor_id_operation_key ON kitchen.cup_operations (tenant_id, actor_id, operation_key);

CREATE INDEX ix_cup_tasks_tenant_id_branch_id_service_created_at_utc ON kitchen.cup_tasks (tenant_id, branch_id, service, created_at_utc);

CREATE UNIQUE INDEX ix_cup_tasks_tenant_id_order_id_order_item_id_unit_index ON kitchen.cup_tasks (tenant_id, order_id, order_item_id, unit_index);

CREATE UNIQUE INDEX ix_cup_tasks_ticket_id_order_cup_index ON kitchen.cup_tasks (ticket_id, order_cup_index);

CREATE INDEX ix_cup_tasks_ticket_item_id ON kitchen.cup_tasks (ticket_item_id);

CREATE INDEX ix_fulfillment_outbox_next_attempt_at_utc_lease_until_utc ON kitchen.fulfillment_outbox (next_attempt_at_utc, lease_until_utc) WHERE state IN ('PENDING','IN_FLIGHT');

CREATE UNIQUE INDEX ix_fulfillment_outbox_tenant_id_order_id_source_version ON kitchen.fulfillment_outbox (tenant_id, order_id, source_version);

ALTER TABLE kitchen.cup_stickers ADD CONSTRAINT fk_cup_stickers_cup_tasks_cup_task_id FOREIGN KEY (cup_task_id) REFERENCES kitchen.cup_tasks (id) ON DELETE RESTRICT;

INSERT INTO kitchen.__ef_migrations_history (migration_id, product_version)
VALUES ('20261106183333_KitchenCupServingFoundation', '8.0.31');

COMMIT;

