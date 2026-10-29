-- REVIEW ONLY. NOT EXECUTED IN D1-A. Must precede ANY D1-B schema/ACL mutation.
-- Runner must first verify Docker IDs/network/volume/port against private manifest.
\set ON_ERROR_STOP on
DO $guard$
DECLARE
    expected_id text := current_setting('aicos.gated_expected_system_identifier', true);
    approved_stage text := current_setting('aicos.gated_approved_stage', true);
    actual_id text;
BEGIN
    SELECT system_identifier::text INTO actual_id FROM pg_control_system();
    IF current_database() <> 'aicos_payment_test'
       OR current_schema() <> 'public'
       OR current_setting('server_version_num')::int NOT BETWEEN 180000 AND 189999
       OR expected_id IS NULL OR expected_id !~ '^[0-9]{15,20}$'
       OR expected_id <> actual_id
       OR approved_stage IS DISTINCT FROM 'D1-B'
    THEN
        RAISE EXCEPTION 'Refusing target/stage not approved for isolated Gate D1-B';
    END IF;
END
$guard$;

