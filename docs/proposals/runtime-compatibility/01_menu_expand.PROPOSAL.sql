-- PROPOSAL ONLY. No local execution until explicit approval.
BEGIN;
SET LOCAL lock_timeout='3s'; SET LOCAL statement_timeout='60s';
DO $guard$ BEGIN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'menu' OR current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Wrong compatibility target'; END IF;
IF NOT (current_database()='aicos_menu' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database()='aicos_runtime_compat_menu' AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside reviewed local/test scope'; END IF;
IF current_setting('aicos.runtime_compat_expand_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Separate stage approval required'; END IF;
IF ARRAY(SELECT migration_id::text FROM menu.__ef_migrations_history ORDER BY migration_id) IS DISTINCT FROM ARRAY['20261011161406_AddMenuCatalog','20261020185416_TenantMenuAndBranchProducts','20261021102711_ConstraintsAndIndexes']::text[] THEN RAISE EXCEPTION 'Exact migration history changed'; END IF;
IF NOT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='menu' AND table_name='products' AND column_name='image_url' AND data_type='character varying' AND character_maximum_length=1000 AND is_nullable='YES') THEN RAISE EXCEPTION 'Unexpected image_url definition'; END IF;
END $guard$;
ALTER TABLE menu.products ALTER COLUMN image_url TYPE character varying(400000);

INSERT INTO menu.__ef_migrations_history (migration_id, product_version)
VALUES ('20261113170918_ProductPictureUpload', '8.0.31');
COMMIT;
