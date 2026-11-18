# Isolated Cup service-role permission evidence

Generated only after role-login and HTTP/SQL assertions PASS. No credentials or operating-role inspection.
Cluster: 7705410743844257826

## kitchen_svc

| Table | SELECT | INSERT | UPDATE | DELETE | TRUNCATE |
| --- | --- | --- | --- | --- | --- |
| __ef_migrations_history | NO | NO | NO | NO | NO |
| cup_claims | YES | YES | YES | NO | NO |
| cup_operations | YES | YES | NO | NO | NO |
| cup_stickers | YES | YES | NO | NO | NO |
| cup_tasks | YES | YES | YES | NO | NO |
| fulfillment_outbox | YES | YES | YES | NO | NO |
| kitchen_ticket_items | YES | YES | NO | NO | NO |
| kitchen_tickets | YES | YES | YES | NO | NO |
| order_admission_receipts | YES | YES | YES | NO | NO |

## ordering_svc

| Table | SELECT | INSERT | UPDATE | DELETE | TRUNCATE |
| --- | --- | --- | --- | --- | --- |
| __ef_migrations_history | NO | NO | NO | NO | NO |
| fulfillment_receipts | YES | YES | NO | NO | NO |
| kitchen_admission_outbox | YES | YES | YES | NO | NO |
| kitchen_admission_reconcile_state | YES | YES | YES | NO | NO |
| order_feedbacks | YES | YES | NO | NO | NO |
| order_fulfillment_projection | YES | YES | YES | NO | NO |
| order_item_options | YES | YES | NO | NO | NO |
| order_items | YES | YES | NO | NO | NO |
| orders | YES | YES | YES | NO | NO |
| pickup_slot_settings | YES | YES | YES | NO | NO |
| table_sessions | YES | YES | YES | NO | NO |
