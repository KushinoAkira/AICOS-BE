-- REVIEW ONLY: explicit target, cluster and approval required. No operational execution is authorized by this file.
DO $$ BEGIN
 IF current_setting('aicos.expected_database',true) IS NULL OR current_database() <> current_setting('aicos.expected_database',true) OR current_database()='aicos_db' THEN RAISE EXCEPTION 'Database target guard failed'; END IF;
 IF current_setting('aicos.expected_cluster',true) IS NULL OR (SELECT system_identifier::text FROM pg_control_system()) <> current_setting('aicos.expected_cluster',true) THEN RAISE EXCEPTION 'Cluster guard failed'; END IF;
 IF current_setting('aicos.cashier_expand_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Explicit cashier expand approval required'; END IF;
END $$;
﻿START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM payment.__ef_migrations_history WHERE "migration_id" = '20261114125030_CashierMoneyShifts') THEN
    CREATE TABLE payment.cashier_shifts (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        branch_id uuid NOT NULL,
        cashier_id uuid NOT NULL,
        open_key character varying(64) NOT NULL,
        opening_cash numeric(18,0) NOT NULL,
        note character varying(500),
        opened_at_utc timestamp with time zone NOT NULL,
        closed_at_utc timestamp with time zone,
        counted_cash numeric(18,0),
        expected_cash numeric(18,0),
        closing_note character varying(500),
        version bigint NOT NULL,
        CONSTRAINT pk_cashier_shifts PRIMARY KEY (id),
        CONSTRAINT ck_cashier_shift_money CHECK (opening_cash >= 0 AND (counted_cash IS NULL OR counted_cash >= 0))
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM payment.__ef_migrations_history WHERE "migration_id" = '20261114125030_CashierMoneyShifts') THEN
    CREATE TABLE payment.collection_outbox (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        branch_id uuid NOT NULL,
        cashier_id uuid,
        shift_id uuid,
        amount numeric(18,0) NOT NULL,
        method character varying(20) NOT NULL,
        payload jsonb NOT NULL,
        paid_at_utc timestamp with time zone NOT NULL,
        delivered_at_utc timestamp with time zone,
        CONSTRAINT pk_collection_outbox PRIMARY KEY (id),
        CONSTRAINT ck_collection_amount CHECK (amount > 0),
        CONSTRAINT fk_collection_outbox_cashier_shifts_shift_id FOREIGN KEY (shift_id) REFERENCES payment.cashier_shifts (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM payment.__ef_migrations_history WHERE "migration_id" = '20261114125030_CashierMoneyShifts') THEN
    CREATE UNIQUE INDEX ix_cashier_shifts_tenant_id_cashier_id ON payment.cashier_shifts (tenant_id, cashier_id) WHERE closed_at_utc IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM payment.__ef_migrations_history WHERE "migration_id" = '20261114125030_CashierMoneyShifts') THEN
    CREATE UNIQUE INDEX ix_cashier_shifts_tenant_id_cashier_id_open_key ON payment.cashier_shifts (tenant_id, cashier_id, open_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM payment.__ef_migrations_history WHERE "migration_id" = '20261114125030_CashierMoneyShifts') THEN
    CREATE INDEX ix_collection_outbox_paid_at_utc ON payment.collection_outbox (paid_at_utc) WHERE delivered_at_utc IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM payment.__ef_migrations_history WHERE "migration_id" = '20261114125030_CashierMoneyShifts') THEN
    CREATE INDEX ix_collection_outbox_shift_id ON payment.collection_outbox (shift_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM payment.__ef_migrations_history WHERE "migration_id" = '20261114125030_CashierMoneyShifts') THEN
    INSERT INTO payment.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261114125030_CashierMoneyShifts', '8.0.31');
    END IF;
END $EF$;
COMMIT;

