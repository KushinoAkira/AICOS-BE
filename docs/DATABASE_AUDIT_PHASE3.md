# AICOS Phase 3 - Full Database Audit Before Deployment

Audit date: 2026-10-25  
Database: `aicos_db`  
Operational schema: `public`  
Reference DDL: `D:\ki9\sep490\db\Database.sql`

## 1. Safety and scope

All database inspection in this audit ran inside PostgreSQL `READ ONLY`
transactions. No migration or `CREATE`, `ALTER`, `DROP`, `TRUNCATE`, `INSERT`,
`UPDATE`, or `DELETE` statement was executed against `aicos_db`.

The read-only Payments preflight script `docs/migrations/payments/00_preflight.sql`
was executed successfully and reported:

- database `aicos_db`;
- schema `public`;
- `transaction_read_only = on`;
- `bills = 0`, `bill_items = 0`, `payments = 0`.

No Mobile source, migration, or committed migration history was changed.

## 2. Executive result

`Database.sql` defines 35 tables and 208 columns. All 35 tables and all 208
columns exist in `aicos_db.public`; no reference table is missing and no
duplicate physical table exists in another schema.

The live database contains 37 tables and 219 columns in total:

- 35 reference business tables / 208 columns from `Database.sql`;
- `refresh_sessions` / 9 columns, added by the approved Identity migration;
- `__ef_migrations_history` / 2 columns, used by EF Core.

Live structural totals:

| Object | Count | Notes |
|---|---:|---|
| Schemas containing application tables | 1 | `public` only |
| Tables | 37 | 35 reference + refresh + EF history |
| Columns | 219 | 208 reference + 11 support columns |
| Primary keys | 37 | Every table has one |
| Foreign keys | 53 | 50 reference + 3 refresh-session FKs |
| Indexes | 57 | Includes PK/unique backing indexes |
| Unique constraints | 8 | Does not include eight standalone unique indexes |
| Custom CHECK constraints | 0 | Payment contract checks are still pending |
| Invalid/unvalidated constraints | 0 | All current constraints are valid |

The only table required by the implemented Payment model but absent from the
main database is `payment_events`. The Payment model also requires 14 additive
columns, eight indexes, new foreign keys/check constraints, and stronger
nullability. These are already represented by the staged Payment SQL and were
validated only on `aicos_payment_test`.

The current Payment Backend must not serve requests against `aicos_db` before
the approved Payment expand stages are applied: its EF model selects columns
that do not yet exist and maps `payment_events`, which does not yet exist.

## 3. Complete table ownership and column inventory

`PK` is `id` for every business table unless explicitly stated otherwise.
`Indexes` counts all live indexes, including PK and unique indexes.

| Logical owner | Table | Columns | FK targets | Indexes | Implementation state |
|---|---|---|---|---:|---|
| Platform / Identity | `tenants` | `id,name,code,status,created_at,updated_at` | none | 2 | Active Identity mapping |
| Platform / Identity | `branches` | `id,tenant_id,code,name,address,phone,status,created_at` | `tenants` | 2 | Active Identity mapping |
| Platform / Subscription | `subscription_plans` | `id,name,price,max_branches,duration_days,is_active` | none | 1 | Foundation only; no Entity/DbSet |
| Platform / Subscription | `subscriptions` | `id,tenant_id,plan_id,status,start_date,end_date,created_at` | `tenants,subscription_plans` | 1 | Foundation only; no Entity/DbSet |
| Identity | `users` | `id,email,password_hash,full_name,phone,status,created_at` | none | 2 | Active Identity mapping |
| Identity | `roles` | `id,code,name` | none | 2 | Active Identity mapping |
| Identity | `tenant_users` | `id,tenant_id,user_id,role_id,status` | `tenants,users,roles` | 2 | Active Identity mapping |
| Identity | `staff_assignments` | `id,branch_id,tenant_user_id,status` | `branches,tenant_users` | 2 | Active Identity mapping |
| Identity | `refresh_sessions` | `id,user_id,branch_id,token_hash,expires_at_utc,created_at_utc,revoked_at_utc,replaced_by_session_id,version` | `users,branches,refresh_sessions` | 5 | Active; added by applied EF migration |
| Workforce | `shifts` | `id,branch_id,name,start_time,end_time,status` | `branches` | 1 | Foundation only; service has no entities |
| Workforce | `shift_attendances` | `id,shift_id,staff_assignment_id,check_in,check_out,status` | `shifts,staff_assignments` | 2 | Foundation only; service has no entities |
| Workforce / Cash operations | `cash_closings` | `id,shift_id,expected_amount,actual_amount,difference,closed_at,note` | `shifts` | 1 | Foundation only; service has no entities |
| Table Reservation | `table_areas` | `id,branch_id,name,sort_order` | `branches` | 1 | Active Table mapping |
| Table Reservation | `cafe_tables` | `id,area_id,table_number,capacity,status,qr_token` | `table_areas` | 3 | Active Table mapping |
| Table Reservation | `table_sessions` | `id,branch_id,status,opened_at,closed_at` | `branches` | 1 | Active Table mapping |
| Table Reservation | `table_session_tables` | `id,session_id,table_id,joined_at,left_at` | `table_sessions,cafe_tables` | 1 | Active Table mapping |
| Menu Catalog | `categories` | `id,tenant_id,name,sort_order,is_active` | `tenants` | 1 | Active Menu mapping |
| Menu Catalog | `products` | `id,tenant_id,category_id,name,description,image_url,is_active,created_at` | `tenants,categories` | 1 | Active Menu mapping |
| Menu Catalog | `product_variants` | `id,product_id,size_name,price,is_active` | `products` | 2 | Active Menu mapping |
| Menu Catalog | `product_option_groups` | `id,product_id,name,min_select,max_select` | `products` | 1 | Active Menu mapping |
| Menu Catalog | `product_options` | `id,group_id,name,extra_price,is_active` | `product_option_groups` | 1 | Active Menu mapping |
| Menu Catalog | `branch_products` | `id,branch_id,product_id,is_available` | `branches,products` | 2 | Active Menu mapping |
| Ordering | `orders` | `id,branch_id,session_id,customer_id,order_code,order_type,status,pickup_time,subtotal,total_amount,created_at` | `branches,table_sessions,users` | 2 | Active Ordering; read by Kitchen |
| Ordering | `order_items` | `id,order_id,variant_id,product_name,variant_name,quantity,unit_price,status,note` | `orders,product_variants` | 1 | Active Ordering; status also written by Kitchen |
| Ordering | `order_item_options` | `id,order_item_id,option_id,option_name,extra_price` | `order_items,product_options` | 1 | Active Ordering mapping |
| Ordering | `order_item_events` | `id,order_item_id,actor_user_id,event_type,old_status,new_status,note,created_at` | `order_items,users` | 1 | Active Ordering; also written by Kitchen |
| Payments | `bills` | `id,session_id,order_id,bill_code,status,total_amount,created_at` | `table_sessions,orders` | 2 | Exists but incomplete for new Payment Backend |
| Payments | `bill_items` | `id,bill_id,order_item_id,quantity,amount` | `bills,order_items` | 1 | Exists; two performance indexes pending |
| Payments | `payments` | `id,bill_id,method,status,amount,transaction_ref,paid_at,created_at` | `bills` | 2 | Exists but incomplete for new Payment Backend |
| Payments | `refunds` | `id,payment_id,amount,reason,status,created_at` | `payments` | 1 | Existing model; mutation workflow not implemented |
| Customer Engagement | `loyalty_accounts` | `id,tenant_id,user_id,points_balance,tier` | `tenants,users` | 2 | Foundation only; service has no entities |
| Customer Engagement | `loyalty_transactions` | `id,account_id,order_id,points_change,type,created_at` | `loyalty_accounts,orders` | 1 | Foundation only; service has no entities |
| Customer Engagement | `rewards` | `id,tenant_id,name,required_points,is_active` | `tenants` | 1 | Foundation only; service has no entities |
| Customer Engagement | `reward_redemptions` | `id,reward_id,account_id,redeemed_at,status` | `rewards,loyalty_accounts` | 1 | Foundation only; service has no entities |
| Customer Engagement | `feedbacks` | `id,order_id,user_id,rating,comment,created_at` | `orders,users` | 1 | Foundation only; service has no entities |
| Customer Engagement / AI | `feedback_analyses` | `id,feedback_id,sentiment,confidence,analyzed_at` | `feedbacks` | 2 | Foundation only; no .NET Entity/DbSet |
| EF Core infrastructure | `__ef_migrations_history` | `migration_id,product_version` | none; PK `migration_id` | 1 | Active migration history |

No Inventory, Reporting, or AI-specific operational table is defined by
`Database.sql`, and those .NET DbContexts currently contain no entities. Their
absence is therefore a future-feature gap, not a blocker for current
Identity/Menu/Table/Ordering/Kitchen/Payment deployment.

## 4. Existing keys, unique rules, and indexes

All 35 `Database.sql` primary keys, all 50 reference foreign keys, and all
reference unique definitions are present. The eight standalone composite
unique indexes are:

- `branches(tenant_id, code)`;
- `tenant_users(tenant_id, user_id, role_id)`;
- `staff_assignments(branch_id, tenant_user_id)`;
- `shift_attendances(shift_id, staff_assignment_id)`;
- `cafe_tables(area_id, table_number)`;
- `product_variants(product_id, size_name)`;
- `branch_products(branch_id, product_id)`;
- `loyalty_accounts(tenant_id, user_id)`.

The eight live UNIQUE constraints from the reference schema protect:

- `tenants.code`, `users.email`, `roles.code`, `cafe_tables.qr_token`;
- `orders.order_code`, `bills.bill_code`, `payments.transaction_ref`;
- `feedback_analyses.feedback_id`.

The three `refresh_sessions` foreign keys and its four non-PK indexes match the
applied Identity migration. Unlike the original 50 foreign keys, these three
EF-created foreign keys are not deferrable. This is a known structural
difference from the style of `Database.sql`, but it does not conflict with the
current Identity entity or runtime behavior.

There are currently no database CHECK constraints for business statuses,
positive quantities, or positive financial amounts. The Payment migration adds
checks only to Payments-owned tables.

## 5. Difference from Backend models

### 5.1 Models compatible with the live schema

- Identity Organization: six reference tables plus `refresh_sessions` match.
- Menu Catalog: all six mapped tables match.
- Table Reservation: all four mapped tables match.
- Ordering: all four mapped tables match.
- Kitchen Operations: its partial projections over `orders`, `order_items`, and
  `order_item_events` are column-compatible.
- Placeholder DbContexts for Inventory, Workforce, Reporting, and Customer
  Engagement have no mapped entities and therefore define no pending DDL.

### 5.2 Payment table and columns missing from `aicos_db`

Missing table:

- `payment_events` with 11 columns and append-only protection.

Missing `bills` columns:

- `branch_id uuid`;
- `created_by_user_id uuid`;
- `print_status varchar(30) NOT NULL DEFAULT 'NOT_ISSUED'`;
- `printed_at timestamp without time zone`;
- `printed_by_user_id uuid`;
- `closed_at timestamp without time zone`;
- `closed_by_user_id uuid`;
- `version bigint NOT NULL DEFAULT 0`.

Missing `payments` columns:

- `created_by_user_id uuid`;
- `confirmed_by_user_id uuid`;
- `confirmed_at timestamp without time zone`;
- `evidence_reference varchar(255)`;
- `status_reason varchar(500)`;
- `version bigint NOT NULL DEFAULT 0`.

Missing Payment indexes:

- unique partial `bills(session_id)` when non-null;
- unique partial `bills(order_id)` when non-null;
- `bills(branch_id)`;
- `bill_items(bill_id)` and `bill_items(order_item_id)`;
- `payments(bill_id)`;
- unique `payment_events(request_id)`;
- `payment_events(payment_id, occurred_at)`.

Missing Payment contract constraints:

- actor/branch FKs for bills and payments;
- payment-event FKs;
- exactly-one bill source;
- allowed Bill/Payment/print/event statuses and payment methods;
- positive amount and quantity rules;
- successful-payment actor/timestamp rules;
- QR evidence rule;
- closed-bill audit rule;
- append-only trigger for `payment_events`;
- final `NOT NULL` rules for branch, creator, status, and creation timestamps.

All of these differences are covered by stages `01` through `05` in
`docs/migrations/payments`. The read-only preflight passed because the three
financial tables are empty, their current values are valid, no proposed column
already exists, and no bill needs actor/branch backfill.

### 5.3 Missing but non-blocking future structures

- Inventory/Purchasing has no item, stock, movement, supplier, purchase-order,
  or recipe tables and no Entity/DbSet implementation.
- Reporting has no owned aggregate/read-model tables.
- AI has no embedding/vector table in the official `public` database.
- Workforce and Customer Engagement tables exist, but their services have no
  Entities, repositories, or feature implementation yet.
- Subscription tables exist, but no current DbContext maps them.

These structures must be designed by their owning service when those features
are scheduled. They must not be invented as part of the Payment deployment.

## 6. Ownership and architecture risks

There is no duplicate physical table, but there is shared logical ownership:

- Ordering and Kitchen both map `orders`, `order_items`, and
  `order_item_events`.
- Kitchen updates `order_items.status` and inserts order-item events directly.

This works with the current legacy shared `public` schema but is weaker than a
strict microservice boundary. A future boundary migration should give Ordering
write ownership and let Kitchen submit transitions through an API/event
contract. It is not safe to restructure this before the Payment deployment.

Payments correctly obtains order billing data through `IOrderBillingClient` and
Table checks settlement through the Payments HTTP contract; the new runtime
logic does not directly query another service's DbContext.

## 7. EF Core migration history

The live lowercase table `public.__ef_migrations_history` contains exactly:

1. `20261023040204_BaselineExistingIdentityTables` (`8.0.31`);
2. `20261023040310_AddRefreshSessions` (`8.0.31`).

Both match the two committed Identity migrations. The baseline migration has
empty `Up`/`Down` methods and does not recreate the six existing Identity
tables. The refresh migration matches the live refresh table, FKs, and indexes.

No other service contains an EF Core migration. Therefore:

- no committed EF migration is pending on `aicos_db`;
- the Payment SQL stages are manual reviewed migrations and are not represented
  in EF history;
- starting Payments with `MigrateOnStartup=true` will not install the staged
  Payment schema.

All services call `MigrateDatabaseAsync<TContext>()`. Effective behavior is:

- checked-in `appsettings.json`: `MigrateOnStartup=false`;
- local launch profiles: `Database__MigrateOnStartup=true`;
- Docker Compose shared environment: `Database__MigrateOnStartup=true`.

Production must keep auto-migration disabled and execute reviewed migrations as
a separate deployment step. With many services starting concurrently, a shared
history table plus automatic migration creates unnecessary startup and
ownership risk.

## 8. Deployment configuration drift that must be resolved

The official decision is one database `aicos_db` and schema `public`, but the
checked-in Docker infrastructure still represents the older schema-per-service
design:

- `docker-compose.yml` and `.env.example` default to database `aicos`, not
  `aicos_db`;
- Docker creates schemas `identity`, `menu`, `table_reservation`, `ordering`,
  `payment`, `inventory`, `kitchen`, `workforce`, `reporting`, `customer`, and
  `ai`;
- it revokes public-schema CREATE permission and does not grant the service
  roles the required `USAGE`/table/sequence privileges on `public`;
- implemented core services now declare schema `public`, while Inventory,
  Workforce, Reporting, and Customer service metadata still declares a private
  schema;
- comments in the shared DbContext still describe schema-per-service isolation.

Consequences:

- local implemented services work because Development connection strings use
  `postgres@aicos_db`;
- a default Docker deployment points at a different database;
- service-specific Docker roles cannot reliably query/write the official
  `public` tables;
- Docker auto-migration does not install the manual Payment SQL.

Before production deployment, configuration must be reviewed in a separate
approved change: set the operational database explicitly to `aicos_db`, define
least-privilege grants for each service on its owned `public` tables, align the
remaining service schema metadata, and disable startup migrations. This audit
does not apply those changes.

## 9. Required migration order

### Stage A - deployment gate and backup

1. Freeze the exact Backend release artifact.
2. Resolve the Docker/database/role configuration drift described above.
3. Disable `MigrateOnStartup` for deployment.
4. Pause bill/payment writes.
5. Create a custom-format backup of `aicos_db`, a schema-only dump, and checksums.
6. Record current row counts and the two EF history entries.

### Stage B - Payment expand

1. Run `00_preflight.sql` again; it must pass in `READ ONLY` mode.
2. Run `01_expand_columns.sql`.
3. Run `02_create_payment_events.sql`.

Do not start the new Payment Backend before Stage B is complete.

### Stage C - compatible Backend deployment

1. Deploy the Payment Backend that writes branch, actor, evidence, version, and
   audit-event data.
2. Keep external payment mutation traffic disabled until the contract stage and
   verification complete.
3. Confirm the Table service can reach the settlement endpoint.

### Stage D - backfill and performance

1. Run `03_backfill.sql` while writes are paused or dual-write is active.
2. Run `04_indexes.sql` with autocommit enabled because it uses
   `CREATE INDEX CONCURRENTLY`.
3. Inspect `pg_index.indisvalid`; do not proceed with an invalid index.

The current financial tables are empty, so no actor must be fabricated and the
backfill is expected to be a no-op.

### Stage E - contract and verification

1. Run `05_contract_constraints.sql`.
2. Run `06_verify.sql`; every issue count must be zero.
3. Run PostgreSQL Payment integration tests on `aicos_payment_test`, never on
   `aicos_db`.
4. Perform read-only production smoke checks, then enable payment mutations.

### Stage F - later service-owned migrations

Create separate reviewed migrations only when Inventory, Workforce, Customer,
Reporting, Subscription, or AI features are implemented. Do not combine those
future schemas with the Payment release.

## 10. Rollback plan

Preferred rollback is application rollback while retaining additive columns
and financial audit data.

Before Payment mutations are enabled:

1. run `90_rollback_constraints.sql`;
2. run `91_rollback_indexes.sql` with autocommit;
3. run `92_rollback_schema.sql` only after confirming no new Payment data exists;
4. verify the original four Payment tables match the pre-deployment schema.

After any Payment mutation has occurred, do not run destructive Stage 92.
Retain `payment_events` and audit columns, roll the application back to a
compatible version, and prepare a forward-fix migration. Restoring the full
database backup is a last resort because it would also roll back unrelated
business data written after the backup.

If a concurrent index build fails, inspect and remove only the specifically
invalid new index during an approved maintenance action before retrying. Never
drop an existing reference index or table.

## 11. Deployment blockers and decision

Mandatory before the new Payment Backend can use `aicos_db`:

1. approve and execute Payment stages `01` and `02` after backup;
2. complete stages `03` through `06` in the documented order;
3. align Docker/runtime database name to `aicos_db`;
4. define least-privilege access to the owned tables in `public`;
5. disable uncontrolled `MigrateOnStartup` during deployment.

There is no need to recreate any of the 35 reference tables, change schema, or
create a second operational database. Audit is complete and stopped before all
mutating actions pending explicit approval.
