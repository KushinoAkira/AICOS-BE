BEGIN READ ONLY;
DO $$ BEGIN
  IF current_database() <> 'aicos_ordering' OR current_setting('aicos.expected_cluster',true)
    IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN
    RAISE EXCEPTION 'Wrong or unverified deployment target';
  END IF;
END $$;
SELECT current_database(),current_user;
SELECT count(*) AS audit_columns FROM information_schema.columns
WHERE table_schema='ordering' AND table_name='session_discount_events'; -- 13
SELECT conname,pg_get_constraintdef(oid) FROM pg_constraint
WHERE conrelid='ordering.session_discount_events'::regclass ORDER BY conname;
SELECT indexname,indexdef FROM pg_indexes WHERE schemaname='ordering' AND tablename='session_discount_events';
SELECT
  has_table_privilege('ordering_svc','ordering.session_discount_events','SELECT') AS can_read,
  has_table_privilege('ordering_svc','ordering.session_discount_events','INSERT') AS can_append,
  NOT has_table_privilege('ordering_svc','ordering.session_discount_events','UPDATE') AS cannot_update,
  NOT has_table_privilege('ordering_svc','ordering.session_discount_events','DELETE') AS cannot_delete,
  NOT has_table_privilege('ordering_svc','ordering.session_discount_events','TRUNCATE') AS cannot_truncate,
  has_column_privilege('ordering_svc','ordering.orders','discount_amount','UPDATE') AS can_update_discount,
  has_column_privilege('ordering_svc','ordering.orders','updated_at_utc','UPDATE') AS can_update_timestamp,
  has_column_privilege('ordering_svc','ordering.orders','updated_by','UPDATE') AS can_update_actor;
SELECT r.rolname,r.rolsuper,r.rolbypassrls,pg_get_userbyid(c.relowner) AS audit_owner
FROM pg_roles r CROSS JOIN pg_class c WHERE r.rolname='ordering_svc' AND c.oid='ordering.session_discount_events'::regclass;
-- Every boolean above must be true, rolsuper/rolbypassrls false, service role must not own audit table.
-- If inherited/owner rights are broader, STOP. No unapproved REVOKE/ownership changes here.
SELECT count(*) AS audit_rows FROM ordering.session_discount_events;
COMMIT;
