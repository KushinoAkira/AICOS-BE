-- PROPOSAL ONLY. Requires fresh backup, maintenance and separate approval.
BEGIN;
SET LOCAL lock_timeout='3s'; SET LOCAL statement_timeout='60s';
DO $guard$ BEGIN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'workforce' THEN RAISE EXCEPTION 'Wrong target'; END IF;
IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Wrong cluster'; END IF;
IF NOT (current_database()='aicos_workforce' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database()='aicos_dashboard_upstream_workforce' AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside reviewed scope'; END IF;
IF current_setting('aicos.dashboard_upstream_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Separate approval required'; END IF;
IF ARRAY(SELECT migration_id::text FROM workforce.__ef_migrations_history ORDER BY migration_id) IS DISTINCT FROM ARRAY['20261011162457_AddWorkforceScheduling','20261020191800_TenantReplacementPayrollCash','20261021101943_PayrollAdjustments','20261021102656_ConstraintsAndIndexes']::text[] THEN RAISE EXCEPTION 'History mismatch or already applied'; END IF;
END $guard$;
DROP INDEX workforce.ix_work_shifts_branch_id_name;

ALTER TABLE workforce.work_shifts ADD deleted_at_utc timestamp with time zone;

CREATE UNIQUE INDEX ix_work_shifts_branch_id_name ON workforce.work_shifts (branch_id, name) WHERE deleted_at_utc IS NULL;

INSERT INTO workforce.__ef_migrations_history (migration_id, product_version)
VALUES ('20261108163812_WorkShiftSoftDelete', '8.0.31');

ALTER TABLE workforce.employee_shifts ADD manager_note character varying(500);

ALTER TABLE workforce.employee_shifts ADD manager_status character varying(30);

INSERT INTO workforce.__ef_migrations_history (migration_id, product_version)
VALUES ('20261113161932_AttendanceManagerStatus', '8.0.31');

ALTER TABLE workforce.cash_closings ADD opening_amount numeric(14,2) NOT NULL DEFAULT 0.0;

ALTER TABLE workforce.cash_closings ADD previous_closing_id uuid;

ALTER TABLE workforce.cash_closings ADD since_utc timestamp with time zone;

CREATE INDEX ix_cash_transactions_tenant_id_branch_id_occurred_at_utc ON workforce.cash_transactions (tenant_id, branch_id, occurred_at_utc);

CREATE INDEX ix_cash_closings_tenant_id_branch_id_submitted_at_utc ON workforce.cash_closings (tenant_id, branch_id, submitted_at_utc);

INSERT INTO workforce.__ef_migrations_history (migration_id, product_version)
VALUES ('20261115162626_CashHandoverAtShiftStart', '8.0.31');
COMMIT;
