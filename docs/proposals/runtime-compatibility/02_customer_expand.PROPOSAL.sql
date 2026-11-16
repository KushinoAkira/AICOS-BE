-- PROPOSAL ONLY. No local execution until explicit approval.
BEGIN;
SET LOCAL lock_timeout='3s'; SET LOCAL statement_timeout='60s';
DO $guard$ BEGIN
IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM 'customer' OR current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Wrong compatibility target'; END IF;
IF NOT (current_database()='aicos_customer' AND current_setting('aicos.expected_cluster')='7703616372368646182') AND NOT (current_database()='aicos_runtime_compat_customer' AND current_setting('aicos.expected_cluster')<>'7703616372368646182') THEN RAISE EXCEPTION 'Outside reviewed local/test scope'; END IF;
IF current_setting('aicos.runtime_compat_expand_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Separate stage approval required'; END IF;
IF ARRAY(SELECT migration_id::text FROM customer.__ef_migrations_history ORDER BY migration_id) IS DISTINCT FROM ARRAY['20261011162712_AddCustomersLoyaltyVouchers','20261020172519_AddDrinkPreferences','20261020190049_TenantLoyaltyRulesVouchers','20261021102718_ConstraintsAndIndexes','20261023083252_LoyaltyRedemptions','20261024164151_Notifications','20261101170848_CustomerProfilePerBrand','20261101183341_StaffNotifications']::text[] THEN RAISE EXCEPTION 'Exact migration history changed'; END IF;
IF to_regclass('customer.member_orders') IS NOT NULL THEN RAISE EXCEPTION 'Existing member_orders needs review'; END IF;
IF EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='customer' AND table_name='vouchers' AND column_name IN ('campaign_id','count_orders_from_utc','required_order_amount','required_orders')) THEN RAISE EXCEPTION 'Existing voucher columns need review'; END IF;
LOCK TABLE customer.loyalty_transactions,customer.customers IN SHARE MODE;
IF EXISTS(SELECT 1 FROM customer.loyalty_transactions t JOIN customer.customers c ON c.id=t.customer_id WHERE t.type='Earn' AND t.order_id IS NOT NULL AND t.note ~ 'paid [0-9]+') THEN RAISE EXCEPTION 'Nonzero note-derived backfill requires separate business review'; END IF;
END $guard$;
ALTER TABLE customer.vouchers ADD campaign_id uuid;

ALTER TABLE customer.vouchers ADD count_orders_from_utc timestamp with time zone;

ALTER TABLE customer.vouchers ADD required_order_amount numeric(14,2);

ALTER TABLE customer.vouchers ADD required_orders integer;

CREATE TABLE customer.member_orders (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    customer_id uuid NOT NULL,
    order_id uuid NOT NULL,
    amount numeric(14,2) NOT NULL,
    paid_at_utc timestamp with time zone NOT NULL,
    is_cancelled boolean NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    created_by uuid,
    updated_at_utc timestamp with time zone,
    updated_by uuid,
    CONSTRAINT pk_member_orders PRIMARY KEY (id),
    CONSTRAINT fk_member_orders_customers_customer_id FOREIGN KEY (customer_id) REFERENCES customer.customers (id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX ix_vouchers_campaign_id_issued_to_customer_id ON customer.vouchers (campaign_id, issued_to_customer_id) WHERE campaign_id IS NOT NULL;

CREATE INDEX ix_member_orders_customer_id_paid_at_utc ON customer.member_orders (customer_id, paid_at_utc);

CREATE UNIQUE INDEX ix_member_orders_order_id ON customer.member_orders (order_id);

INSERT INTO customer.member_orders (id, tenant_id, customer_id, order_id, amount, paid_at_utc, is_cancelled, created_at_utc)
SELECT gen_random_uuid(), c.tenant_id, t.customer_id, t.order_id, CAST(substring(t.note from 'paid ([0-9]+([.][0-9]+)?)') AS numeric(14,2)),
       t.created_at_utc,
       EXISTS (SELECT 1 FROM customer.loyalty_transactions r WHERE r.order_id = t.order_id AND r.type = 'Reverse'),
       t.created_at_utc
FROM customer.loyalty_transactions t
JOIN customer.customers c ON c.id = t.customer_id
WHERE t.type = 'Earn' AND t.order_id IS NOT NULL AND t.note ~ 'paid [0-9]+'
ON CONFLICT (order_id) DO NOTHING;

INSERT INTO customer.__ef_migrations_history (migration_id, product_version)
VALUES ('20261113175515_HuntVouchers', '8.0.31');
COMMIT;
