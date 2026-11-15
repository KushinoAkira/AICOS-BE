// Exact additive package for the audited Dashboard baseline. Never executes SQL.
import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';

export const mainCluster='7703616372368646182';
export const databases={ordering:'aicos_ordering',kitchen:'aicos_kitchen',payment:'aicos_payment',reporting:'aicos_reporting'};
const names={ordering:'01_ordering',kitchen:'02_kitchen',payment:'03_payments',reporting:'04_reporting'};
const approvals={expand:'dashboard_expand',rollback:'dashboard_rollback',grants:'dashboard_grants'};

function identity(schema,action){
 const db=databases[schema];
 return [
  "IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM '"+schema+"' THEN RAISE EXCEPTION 'Database/schema identity mismatch'; END IF;",
  "IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Cluster identity mismatch'; END IF;",
  "IF NOT (current_database()='"+db+"' AND current_setting('aicos.expected_cluster')='"+mainCluster+"') AND NOT (current_database() IN ('aicos_dashboard_test_"+schema+"','aicos_dashboard_restore_"+schema+"') AND current_setting('aicos.expected_cluster')<>'"+mainCluster+"') THEN RAISE EXCEPTION 'Outside approved local/scratch scope'; END IF;",
  action?"IF current_setting('aicos."+approvals[action]+"_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Separate stage approval required'; END IF;":''
 ].filter(Boolean).join('\n');
}
const wrap=(guard,body,readOnly=false)=>[
 '-- REVIEW ONLY. Main execution requires explicit approval. No auto migration or feature activation.',
 readOnly?'BEGIN READ ONLY;':'BEGIN;',"SET LOCAL lock_timeout='3s';","SET LOCAL statement_timeout='60s';",
 'DO $guard$ BEGIN',guard,'END $guard$;',body,'COMMIT;',''
].join('\n');
const historyGuard=(schema,ids)=>"IF ARRAY(SELECT migration_id::text FROM "+schema+".__ef_migrations_history ORDER BY migration_id) IS DISTINCT FROM ARRAY["+ids.map(id=>"'"+id+"'").join(',')+"]::text[] THEN RAISE EXCEPTION 'Migration history changed; inspect before proceeding'; END IF;";

const grantBodies={
 ordering:[
  'GRANT SELECT,INSERT ON ordering.kitchen_admission_outbox,ordering.kitchen_admission_reconcile_state,ordering.fulfillment_receipts,ordering.order_fulfillment_projection,ordering.session_checkouts,ordering.table_transfer_operations TO ordering_svc;',
  'GRANT UPDATE(state,next_attempt_at_utc,attempt_count,lease_token,lease_until_utc,acknowledged_at_utc,acknowledged_ticket_id,last_error_code) ON ordering.kitchen_admission_outbox TO ordering_svc;',
  'GRANT UPDATE(cycle_id,upper_order_id,last_order_id,cycle_started_at_utc,last_completed_at_utc,lease_token,lease_until_utc) ON ordering.kitchen_admission_reconcile_state TO ordering_svc;',
  'GRANT UPDATE(source_version,snapshot_hash,total,served,review,snapshot_json,fulfilled_at_utc,last_verified_at_utc) ON ordering.order_fulfillment_projection TO ordering_svc;',
  'GRANT UPDATE(state,payment_id) ON ordering.session_checkouts TO ordering_svc;',
  'GRANT UPDATE(synced_at_utc) ON ordering.table_transfer_operations TO ordering_svc;',
  'GRANT SELECT(fulfillment_mode),UPDATE(fulfillment_mode) ON ordering.orders TO ordering_svc;',
  'GRANT SELECT(replied_at_utc,replied_by_user_id,reply),UPDATE(replied_at_utc,replied_by_user_id,reply) ON ordering.order_feedbacks TO ordering_svc;'
 ].join('\n'),
 kitchen:'GRANT SELECT(table_transfer_version),UPDATE(table_transfer_version) ON kitchen.kitchen_tickets TO kitchen_svc;',
 payment:[
  'GRANT SELECT,INSERT ON payment.session_bills,payment.session_bill_orders,payment.session_bill_attempts,payment.session_bill_events,payment.cashier_shifts,payment.collection_outbox,payment.bill_split_plans,payment.bill_split_operations TO payment_svc;',
  'GRANT UPDATE(status,version,paid_payment_id,paid_at_utc,closed_at_utc,ordering_acknowledged,needs_reconciliation) ON payment.session_bills TO payment_svc;',
  'GRANT UPDATE(status,provider_reference,checkout_url,paid_at_utc) ON payment.session_bill_attempts TO payment_svc;',
  'GRANT UPDATE(delivered_at_utc,next_attempt_at_utc,lease_owner,lease_until_utc,fence) ON payment.session_bill_events TO payment_svc;',
  'GRANT UPDATE(closed_at_utc,counted_cash,expected_cash,closing_note,version) ON payment.cashier_shifts TO payment_svc;',
  'GRANT UPDATE(delivered_at_utc) ON payment.collection_outbox TO payment_svc;',
  'GRANT UPDATE(version,fingerprint,quote_json,groups_json,updated_by,updated_at_utc) ON payment.bill_split_plans TO payment_svc;'
 ].join('\n'),
 reporting:'GRANT SELECT,INSERT ON reporting.collection_facts TO reporting_svc;'
};

export async function preparePackage(folder,exports,targets,baseline){
 await fs.mkdir(folder,{recursive:true});
 const files={};
 for(const target of targets){
  const {schema}=target,base=baseline[schema];
  const existing=new Map(base.columns.map(c=>[c.table+'.'+c.name,c]));
  const oldTables=new Set(base.columns.map(c=>c.table));
  const tables=[...new Set(target.columns.map(c=>c.table))].filter(t=>!oldTables.has(t));
  const columns=target.columns.filter(c=>oldTables.has(c.table)&&!existing.has(c.table+'.'+c.name));
  for(const col of base.columns){
   const wanted=target.columns.find(c=>c.table===col.table&&c.name===col.name);
   assert.ok(wanted,'Unexpected baseline column '+schema+'.'+col.table+'.'+col.name);
   assert.equal(col.type.replaceAll(' ',''),wanted.type.replaceAll(' ',''),'Existing type mismatch '+schema+'.'+col.table+'.'+col.name);
   assert.equal(col.nullable,wanted.nullable,'Existing nullability mismatch '+schema+'.'+col.table+'.'+col.name);
  }
  assert.deepEqual(base.history,target.baseline,'Exact predecessor history differs: '+schema);
  target.newTables=tables;target.newColumns=columns;
  let before=identity(schema,'expand')+'\n'+historyGuard(schema,target.baseline);
  for(const table of tables)before+="\nIF to_regclass('"+schema+'.'+table+"') IS NOT NULL THEN RAISE EXCEPTION 'Existing target table requires review'; END IF;";
  for(const col of columns)before+="\nIF EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='"+schema+"' AND table_name='"+col.table+"' AND column_name='"+col.name+"') THEN RAISE EXCEPTION 'Existing target column requires review'; END IF;";
  const up=(await fs.readFile(path.join(exports,schema+'.up.sql'),'utf8')).replaceAll('\uFEFF','').replaceAll('\r\n','\n').trim();
  assert.ok(!/^\s*(DROP|DELETE|UPDATE|TRUNCATE)\b/im.test(up),'Unexpected non-additive forward statement');
  assert.ok(!/ALTER TABLE[^;]*\b(DROP|ALTER COLUMN|RENAME)\b/i.test(up),'Unexpected existing-column rewrite');
  assert.ok(!/unit_session_allocations|cup_delivery_routes|session_bill_units/.test(up),'V2 outside scope');
  files[names[schema]+'_expand.sql']=wrap(before,up);
  let rollback=identity(schema,'rollback')+'\n'+historyGuard(schema,[...target.baseline,...target.pending]);
  const locks=[...tables,...new Set(columns.map(c=>c.table))];
  rollback+='\nLOCK TABLE '+locks.map(t=>schema+'.'+t).join(',')+' IN ACCESS EXCLUSIVE MODE;';
  for(const table of tables)rollback+="\nIF EXISTS(SELECT 1 FROM "+schema+'.'+table+") THEN RAISE EXCEPTION 'New data/audit exists; retain schema and repair forward'; END IF;";
  if(schema==='ordering')rollback+="\nIF EXISTS(SELECT 1 FROM ordering.orders WHERE fulfillment_mode<>'WholeOrder') OR EXISTS(SELECT 1 FROM ordering.order_feedbacks WHERE reply IS NOT NULL OR replied_at_utc IS NOT NULL OR replied_by_user_id IS NOT NULL) THEN RAISE EXCEPTION 'Added columns contain new business data'; END IF;";
  if(schema==='kitchen')rollback+="\nIF EXISTS(SELECT 1 FROM kitchen.kitchen_tickets WHERE table_transfer_version<>0) THEN RAISE EXCEPTION 'Routing history exists'; END IF;";
  files['9'+targets.indexOf(target)+'_'+schema+'_rollback.UNUSED_ONLY.sql']=wrap(rollback,(await fs.readFile(path.join(exports,schema+'.down.sql'),'utf8')).replaceAll('\uFEFF','').replaceAll('\r\n','\n').trim());
  files['1'+targets.indexOf(target)+'_'+schema+'_grants.PROPOSAL.sql']=wrap(identity(schema,'grants')+'\n'+historyGuard(schema,[...target.baseline,...target.pending]),'GRANT USAGE ON SCHEMA '+schema+' TO '+(schema==='ordering'?'ordering_svc':schema+'_svc')+';\n'+grantBodies[schema]);
 }
 assert.equal(targets.reduce((n,t)=>n+t.newTables.length,0),15);
 assert.equal(targets.reduce((n,t)=>n+t.newColumns.length,0),5);
 const choose=targets.map(t=>"WHEN '"+t.schema+"' THEN\n"+identity(t.schema)).join('\n');
 const inventory=[
  "SELECT current_database(),system_identifier::text,current_setting('transaction_read_only') FROM pg_control_system();",
  "SELECT n.nspname,c.relname,a.attname,format_type(a.atttypid,a.atttypmod),a.attnotnull FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped WHERE c.relkind='r' AND n.nspname=current_setting('aicos.expected_schema') ORDER BY c.relname,a.attnum;",
  "SELECT c.relname,k.conname,pg_get_constraintdef(k.oid) FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=current_setting('aicos.expected_schema') ORDER BY c.relname,k.conname;",
  "SELECT tablename,indexname,indexdef FROM pg_indexes WHERE schemaname=current_setting('aicos.expected_schema') ORDER BY tablename,indexname;",
  "SELECT pid,usename,application_name,state,xact_start FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid();"
 ].join('\n');
 files['00_preflight.READ_ONLY.sql']=wrap("CASE current_setting('aicos.expected_schema',true)\n"+choose+"\nELSE RAISE EXCEPTION 'Unsupported schema'; END CASE;",inventory,true);
 for(const [name,content] of Object.entries(files)){
  assert.ok(!content.includes('\r'),'SQL must be LF before checksum and Git staging');
  const file=path.join(folder,name);
  try{assert.equal(await fs.readFile(file,'utf8'),content,'Reviewed SQL changed: '+name);}
  catch(error){if(error.code!=='ENOENT')throw error;await fs.writeFile(file,content);}
 }
 const hashes=Object.keys(files).sort().map(n=>crypto.createHash('sha256').update(files[n]).digest('hex')+'  '+n).join('\n')+'\n';
 try{assert.equal(await fs.readFile(path.join(folder,'SHA256SUMS.txt'),'utf8'),hashes,'Reviewed manifest changed');}
 catch(error){if(error.code!=='ENOENT')throw error;await fs.writeFile(path.join(folder,'SHA256SUMS.txt'),hashes);}
 await fs.writeFile(path.join(folder,'.gitattributes'),'*.sql text eol=lf\nSHA256SUMS.txt text eol=lf\n');
 return files;
}
