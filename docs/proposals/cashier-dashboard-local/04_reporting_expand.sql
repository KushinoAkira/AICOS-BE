-- REVIEW ONLY. Main execution requires explicit approval. No auto migration or feature activation.
BEGIN;
SET LOCAL lock_timeout='3s';
SET LOCAL statement_timeout='60s';
DO $guard$ BEGIN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'reporting' THEN RAISE EXCEPTION 'Database/schema identity mismatch'; END IF;
IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;
IF NOT (current_database()='aicos_reporting' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database() IN ('aicos_dashboard_test_reporting','aicos_dashboard_restore_reporting') AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside approved local/scratch scope'; END IF;
IF current_setting('aicos.dashboard_expand_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Separate stage approval required'; END IF;
IF ARRAY(SELECT migration_id::text FROM reporting.__ef_migrations_history ORDER BY migration_id) IS DISTINCT FROM ARRAY['20261021151155_OrderFacts']::text[] THEN RAISE EXCEPTION 'Migration history changed; inspect before proceeding'; END IF;
IF to_regclass('reporting.collection_facts') IS NOT NULL THEN RAISE EXCEPTION 'Existing target table requires review'; END IF;
END $guard$;
CREATE TABLE reporting.collection_facts (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    method character varying(20) NOT NULL,
    amount numeric(18,0) NOT NULL,
    paid_at_utc timestamp with time zone NOT NULL,
    payload jsonb NOT NULL,
    CONSTRAINT pk_collection_facts PRIMARY KEY (id),
    CONSTRAINT ck_collection_fact_amount CHECK (amount > 0)
);

CREATE INDEX ix_collection_facts_tenant_id_branch_id_paid_at_utc ON reporting.collection_facts (tenant_id, branch_id, paid_at_utc);

INSERT INTO reporting.__ef_migrations_history (migration_id, product_version)
VALUES ('20261114125038_ConfirmedCollectionInbox', '8.0.31');
COMMIT;
