BEGIN READ ONLY;
SELECT current_database(),current_user,system_identifier FROM pg_control_system();
SELECT table_schema,table_name,column_name,data_type,is_nullable FROM information_schema.columns WHERE table_schema IN ('ordering','payment','kitchen') AND (table_name IN ('table_transfer_operations','bill_split_plans','bill_split_operations') OR table_name='kitchen_tickets' AND column_name='table_transfer_version') ORDER BY 1,2,3;
SELECT schemaname,tablename,indexname,indexdef FROM pg_indexes WHERE tablename IN ('table_transfer_operations','bill_split_plans','bill_split_operations');
SELECT conname,pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid IN (to_regclass('ordering.table_transfer_operations'),to_regclass('payment.bill_split_plans'),to_regclass('payment.bill_split_operations'));
COMMIT;
