import assert from 'node:assert/strict';
import crypto from 'node:crypto';
const hash=x=>crypto.createHash('sha256').update(JSON.stringify(x)).digest('hex');
export async function verifyDesign({sql,fixture:f,probe,report,restartRedis}){
 const snapshot=(name,table)=>sql(name,`SELECT md5(COALESCE(string_agg(row_to_json(t)::text,'' ORDER BY id::text),'')) FROM ${name}.${table} t;`);
 const originals={};for(const [name,tables] of [['ordering',['orders','order_items']],['kitchen',['kitchen_tickets','cup_tasks','cup_stickers']]])for(const table of tables)originals[table]=await snapshot(name,table);
 for(const u of f.units){
  const source=u.OrderId===f.sourceOrder,session=source?f.sourceSession:f.targetSession,table=source?f.sourceTable:f.targetTable;
  await sql('ordering',`INSERT INTO ordering.unit_session_allocations(tenant_id,cup_id,branch_id,source_order_id,source_order_item_id,unit_index,origin_session_id,current_session_id,unit_amount) VALUES('${f.tenant}','${u.Id}','${f.branch}','${u.OrderId}','${u.OrderItemId}',${u.UnitIndex},'${session}','${session}',${source?50000:40000});`);
  await sql('kitchen',`INSERT INTO kitchen.cup_delivery_routes(cup_id,tenant_id,branch_id,current_session_id,current_table_id,current_table_code) VALUES('${u.Id}','${f.tenant}','${f.branch}','${session}','${table}','${source?'D01':'D02'}');`);
 }
 const locks=[f.sourceTable,f.targetTable].sort().map(id=>`SELECT pg_advisory_xact_lock(hashtextextended('aicos:counter-table:${f.tenant}:${id}',0));`).join('');
 const checkSessions=`IF (SELECT count(*) FROM ordering.table_sessions WHERE id IN ('${f.sourceSession}','${f.targetSession}') AND tenant_id='${f.tenant}' AND branch_id='${f.branch}' AND status='Open')<>2 THEN RAISE EXCEPTION '409 session scope'; END IF;
 IF EXISTS(SELECT 1 FROM ordering.session_checkouts WHERE session_id IN ('${f.sourceSession}','${f.targetSession}')) THEN RAISE EXCEPTION '409 frozen'; END IF;`;
 const operation=(unit,key,actor=f.actor)=>({id:crypto.randomUUID(),key,actor,unit,hash:hash({unit:unit.Id,from:f.sourceSession,to:f.targetSession,key,actor})});
 // Executable transaction sketch, NOT a deployed domain handler or authorization check.
 const prepare=op=>`BEGIN; ${locks} DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM ordering.item_transfer_operations WHERE tenant_id='${f.tenant}' AND actor_id='${op.actor}' AND request_key='${op.key}') THEN
  IF NOT EXISTS(SELECT 1 FROM ordering.item_transfer_operations WHERE id='${op.id}' AND request_hash='${op.hash}') THEN RAISE EXCEPTION '409 request key'; END IF;
 ELSE
  ${checkSessions}
  PERFORM 1 FROM ordering.unit_session_allocations WHERE cup_id='${op.unit.Id}' FOR UPDATE;
  IF NOT EXISTS(SELECT 1 FROM ordering.unit_session_allocations WHERE cup_id='${op.unit.Id}' AND tenant_id='${f.tenant}' AND branch_id='${f.branch}' AND current_session_id='${f.sourceSession}' AND allocation_version=1 AND active_transfer_id IS NULL AND settled_bill_id IS NULL) THEN RAISE EXCEPTION '409 unit stale/paid/reserved'; END IF;
  IF (SELECT count(*) FROM ordering.unit_session_allocations WHERE current_session_id='${f.sourceSession}')<=1 THEN RAISE EXCEPTION '409 empty source'; END IF;
  INSERT INTO ordering.item_transfer_operations(id,tenant_id,branch_id,actor_id,request_key,request_hash,source_session_id,target_session_id,unit_ids,amount,state,created_at_utc,next_attempt_at_utc)
   SELECT '${op.id}',tenant_id,branch_id,'${op.actor}','${op.key}','${op.hash}','${f.sourceSession}','${f.targetSession}','["${op.unit.Id}"]',unit_amount,'PREPARING',now(),now() FROM ordering.unit_session_allocations WHERE cup_id='${op.unit.Id}';
  UPDATE ordering.unit_session_allocations SET active_transfer_id='${op.id}' WHERE cup_id='${op.unit.Id}';
 END IF; END $$; COMMIT;`;
 async function kitchenPrepare(op){
  await sql('kitchen',`BEGIN; SELECT pg_advisory_xact_lock(hashtextextended('aicos:kitchen-operation:${f.tenant}:${op.unit.OrderId}',0));
  DO $$ BEGIN
   IF EXISTS(SELECT 1 FROM kitchen.cup_transfer_receipts WHERE operation_id='${op.id}') THEN
    IF NOT EXISTS(SELECT 1 FROM kitchen.cup_transfer_receipts WHERE operation_id='${op.id}' AND payload_hash='${op.hash}') THEN RAISE EXCEPTION '409 hash'; END IF;
   ELSE
    PERFORM 1 FROM kitchen.cup_tasks WHERE id='${op.unit.Id}' FOR UPDATE;
    IF NOT EXISTS(SELECT 1 FROM kitchen.cup_tasks c JOIN kitchen.kitchen_tickets t ON t.id=c.ticket_id JOIN kitchen.cup_delivery_routes r ON r.cup_id=c.id
     WHERE c.id='${op.unit.Id}' AND c.tenant_id='${f.tenant}' AND c.branch_id='${f.branch}' AND c.version=0 AND c.preparation='Pending' AND c.service='Waiting' AND c.review_reason IS NULL
     AND t.recovery_hold_reason IS NULL AND NOT t.pay_first AND r.route_version=1 AND r.current_session_id='${f.sourceSession}' AND r.transfer_operation_id IS NULL)
     OR EXISTS(SELECT 1 FROM kitchen.cup_claims WHERE cup_id='${op.unit.Id}') THEN RAISE EXCEPTION '409 cup ineligible'; END IF;
    INSERT INTO kitchen.cup_transfer_receipts VALUES('${op.id}','${f.tenant}','${f.branch}','${op.actor}','${op.hash}','${f.sourceSession}','${f.targetSession}','["${op.unit.Id}"]','PREPARED',0,now(),'[]');
    UPDATE kitchen.cup_delivery_routes SET transfer_operation_id='${op.id}' WHERE cup_id='${op.unit.Id}';
   END IF; END $$; COMMIT;`);
  await sql('ordering',`UPDATE ordering.item_transfer_operations SET state='PREPARED',audit=audit||'["KITCHEN_PREPARED"]'::jsonb WHERE id='${op.id}' AND state='PREPARING';`);
 }
 async function commit(op){await sql('ordering',`BEGIN; ${locks} DO $$ BEGIN
  IF EXISTS(SELECT 1 FROM ordering.item_transfer_operations WHERE id='${op.id}' AND state IN ('COMMIT_DECIDED','COMPLETED')) THEN RETURN; END IF;
  ${checkSessions}
  IF NOT EXISTS(SELECT 1 FROM ordering.item_transfer_operations WHERE id='${op.id}' AND request_hash='${op.hash}' AND state='PREPARED') THEN RAISE EXCEPTION '409 no prepare proof'; END IF;
  UPDATE ordering.unit_session_allocations SET current_session_id='${f.targetSession}',allocation_version=2 WHERE cup_id='${op.unit.Id}' AND current_session_id='${f.sourceSession}' AND active_transfer_id='${op.id}' AND allocation_version=1 AND settled_bill_id IS NULL;
  IF NOT FOUND THEN RAISE EXCEPTION '409 stale commit'; END IF;
  UPDATE ordering.item_transfer_operations SET state='COMMIT_DECIDED',decision_version=1,audit=audit||'["COMMIT_DECIDED"]'::jsonb WHERE id='${op.id}';
 END $$; COMMIT;`);}
 async function finalize(op){await sql('kitchen',`BEGIN; SELECT pg_advisory_xact_lock(hashtextextended('aicos:kitchen-operation:${f.tenant}:${op.unit.OrderId}',0));
 DO $$ BEGIN
  IF EXISTS(SELECT 1 FROM kitchen.cup_transfer_receipts WHERE operation_id='${op.id}' AND payload_hash='${op.hash}' AND state='FINALIZED') THEN RETURN; END IF;
  IF NOT EXISTS(SELECT 1 FROM kitchen.cup_transfer_receipts WHERE operation_id='${op.id}' AND payload_hash='${op.hash}' AND state='PREPARED') THEN RAISE EXCEPTION '409 receipt'; END IF;
  UPDATE kitchen.cup_delivery_routes SET current_session_id='${f.targetSession}',current_table_id='${f.targetTable}',current_table_code='D02',route_version=2,last_finalized_operation_id='${op.id}',transfer_operation_id=NULL WHERE cup_id='${op.unit.Id}' AND route_version=1 AND transfer_operation_id='${op.id}';
  IF NOT FOUND THEN RAISE EXCEPTION '409 stale route'; END IF;
  UPDATE kitchen.cup_transfer_receipts SET state='FINALIZED',decision_version=1,audit=audit||'["FINALIZED"]'::jsonb WHERE operation_id='${op.id}';
 END $$; COMMIT;`);}
 const complete=op=>sql('ordering',`BEGIN;
 UPDATE ordering.unit_session_allocations SET active_transfer_id=NULL WHERE cup_id='${op.unit.Id}' AND active_transfer_id='${op.id}' AND allocation_version=2 AND current_session_id='${f.targetSession}';
 UPDATE ordering.item_transfer_operations SET state='COMPLETED',audit=audit||'["ACKED"]'::jsonb WHERE id='${op.id}' AND state='COMMIT_DECIDED'; COMMIT;`);
 const source=f.units.filter(x=>x.OrderId===f.sourceOrder),op=operation(source[0],'design-first');
 const invalid=operation(source[2],'scope-reject-test');
 await sql('ordering',`UPDATE ordering.table_sessions SET branch_id='${crypto.randomUUID()}' WHERE id='${f.targetSession}';`);
 await sql('ordering',prepare(invalid),{deny:true});await sql('ordering',`UPDATE ordering.table_sessions SET branch_id='${f.branch}' WHERE id='${f.targetSession}';`);
 await sql('ordering',`UPDATE ordering.table_sessions SET status='Closed' WHERE id='${f.targetSession}';`);
 await sql('ordering',prepare(invalid),{deny:true});await sql('ordering',`UPDATE ordering.table_sessions SET status='Open' WHERE id='${f.targetSession}';`);
 await sql('kitchen',`UPDATE kitchen.cup_tasks SET preparation='Brewing',version=1 WHERE id='${source[2].Id}';`);
 await assert.rejects(()=>kitchenPrepare(invalid));await sql('kitchen',`UPDATE kitchen.cup_tasks SET preparation='Pending',version=0 WHERE id='${source[2].Id}';`);
 report('Prototype rejects different-branch/closed destination and non-Pending or stale cup');
 await sql('ordering',prepare(op));await sql('ordering',prepare(op));await sql('ordering',prepare({...op,hash:hash('changed')}),{deny:true});
 await kitchenPrepare(op);await kitchenPrepare(op);await commit(op);await commit(op);
 report('Same-key/hash receipt guards and duplicate SQL commit retain one assignment');
 const totals=JSON.parse(await sql('ordering',`SELECT json_object_agg(current_session_id,total) FROM (SELECT current_session_id,sum(unit_amount) total FROM ordering.unit_session_allocations GROUP BY current_session_id)t;`));
 assert.equal(totals[f.sourceSession],100000);assert.equal(totals[f.targetSession],90000);assert.equal(Object.values(totals).reduce((a,b)=>a+b),190000);
 report('Source -50000 equals target +50000; one Cup has one current financial owner');
 await restartRedis();assert.equal(await sql('ordering',"SELECT count(*) FROM ordering.item_transfer_operations WHERE state='COMMIT_DECIDED';"),'1');
 await finalize(op);await finalize(op);await complete(op);await complete(op);
 report('Redis restart/lost-finalize-response simulation replays durable decision/receipt once');
 await probe('item-transfer-replay');
 for(const [name,tables] of [['ordering',['orders','order_items']],['kitchen',['kitchen_tickets','cup_tasks','cup_stickers']]])for(const table of tables)assert.equal(await snapshot(name,table),originals[table]);
 report('Actual EF fixtures and CupAdmissionWriter replay preserve original Order/item/ticket/cup/sticker; altered origin rejected');
 // Synthetic financial snapshots validate schema, not a Cash/QR API or provider confirmation.
 for(const [session,bill,total] of [[f.sourceSession,f.sourceBill,100000],[f.targetSession,f.targetBill,90000]])await sql('payment',`INSERT INTO payment.session_bills(id,tenant_id,branch_id,session_id,fingerprint,quote_json,status,version,total,created_by_user_id,created_at_utc,ordering_acknowledged,needs_reconciliation) VALUES('${bill}','${f.tenant}','${f.branch}','${session}','${hash(session)}','{}','UNPAID',1,${total},'${f.actor}',now(),false,false);`);
 const allocation=JSON.parse(await sql('ordering','SELECT json_agg(a) FROM ordering.unit_session_allocations a;'));
 for(const a of allocation)await sql('payment',`INSERT INTO payment.session_bill_units VALUES('${a.tenant_id}','${a.cup_id}','${a.current_session_id===f.sourceSession?f.sourceBill:f.targetBill}','${a.source_order_id}','${a.source_order_item_id}',${a.unit_index},${a.allocation_version},${a.unit_amount});`);
 await sql('payment',`INSERT INTO payment.session_bill_units SELECT tenant_id,cup_id,'${f.sourceBill}',source_order_id,source_order_item_id,unit_index,allocation_version,unit_amount FROM payment.session_bill_units WHERE cup_id='${op.unit.Id}';`,{deny:true});
 assert.equal(await sql('payment','SELECT count(*) FROM payment.session_bills b WHERE b.total<>(SELECT sum(unit_amount) FROM payment.session_bill_units u WHERE u.bill_id=b.id);'),'0');
 report('Unique Cup/ordinal prevents two Bill manifests charging one unit; amounts match synthetic snapshots');
 // Synthetic future V2 serving projection: verifies query membership, not current Serving/Close APIs.
 await sql('ordering',`UPDATE ordering.unit_session_allocations SET service_status='Served',served_at_utc=now(),fulfillment_version=1,fulfillment_route_version=allocation_version WHERE cup_id<>'${op.unit.Id}';`);
 const unserved=JSON.parse(await sql('ordering',"SELECT json_object_agg(current_session_id,n) FROM (SELECT current_session_id,count(*) FILTER(WHERE service_status<>'Served' OR fulfillment_route_version<>allocation_version) n FROM ordering.unit_session_allocations GROUP BY current_session_id)t;"));
 assert.equal(unserved[f.sourceSession],0);assert.equal(unserved[f.targetSession],1);
 report('Proposed per-unit serving projection counts only current-session units, not the whole original Order');

 const a=operation(source[1],'race-design-first'),b=operation(source[1],'race-design-second',crypto.randomUUID());
 const race=await Promise.allSettled([sql('ordering',prepare(a)),sql('ordering',prepare(b))]);assert.equal(race.filter(x=>x.status==='fulfilled').length,1);assert.equal(race.filter(x=>x.status==='rejected').length,1);
 const winner=race[0].status==='fulfilled'?a:b;
 await sql('ordering',`UPDATE ordering.item_transfer_operations SET created_at_utc=now()-interval '1 year',next_attempt_at_utc=now()-interval '1 year' WHERE id='${winner.id}';`);
 assert.equal(await sql('ordering',`SELECT count(*) FROM ordering.unit_session_allocations WHERE active_transfer_id='${winner.id}';`),'1');
 assert.equal(await sql('ordering',"SELECT count(*) FROM ordering.item_transfer_operations WHERE state NOT IN ('COMPLETED','CANCELLED') AND next_attempt_at_utc<=now();"),'1');
 report('Concurrent reservation has one winner; old/timeout operations remain reserved and reconciliation-visible');
 await sql('ordering',`BEGIN; UPDATE ordering.unit_session_allocations SET allocation_version=99 WHERE cup_id='${op.unit.Id}'; SELECT 1/0; COMMIT;`,{deny:true});
 assert.equal(await sql('ordering',`SELECT allocation_version FROM ordering.unit_session_allocations WHERE cup_id='${op.unit.Id}';`),'2');report('Transaction failure rolls back unit mutation');
 await sql('ordering',`INSERT INTO ordering.session_checkouts(session_id,tenant_id,branch_id,bill_id,fingerprint,quote_json,state,payment_id,created_at_utc) VALUES('${f.sourceSession}','${f.tenant}','${f.branch}','${f.sourceBill}','${hash('paid-proof')}','{}','Paid','${crypto.randomUUID()}',now());`);
 const frozen=await sql('ordering','SELECT row_to_json(t) FROM ordering.session_checkouts t;'),paymentBefore=await snapshot('payment','session_bills');
 await sql('ordering',prepare(operation(source[2],'paid-move-reject')),{deny:true});
 assert.equal(await sql('ordering','SELECT row_to_json(t) FROM ordering.session_checkouts t;'),frozen);assert.equal(await snapshot('payment','session_bills'),paymentBefore);
 report('Checkout/PAID fence blocks further transfer without changing synthetic financial state');
 for(const [name,tables] of [['ordering',['unit_session_allocations','item_transfer_operations','unit_settlement_receipts']],['kitchen',['cup_delivery_routes','cup_transfer_receipts']],['payment',['session_bill_units']]]){
  const role='design_'+name+'_svc';
  for(const table of tables){await sql(name,`SET ROLE ${role}; SELECT count(*) FROM ${name}.${table};`);await sql(name,`SET ROLE ${role}; DELETE FROM ${name}.${table} WHERE false;`,{deny:true});await sql(name,`SET ROLE ${role}; TRUNCATE ${name}.${table};`,{deny:true});}
 }
 await sql('ordering',"SET ROLE design_ordering_svc; UPDATE ordering.unit_session_allocations SET allocation_version=allocation_version WHERE false;");
 await sql('ordering',"SET ROLE design_ordering_svc; UPDATE ordering.unit_session_allocations SET unit_amount=unit_amount WHERE false;",{deny:true});
 await sql('kitchen',"SET ROLE design_kitchen_svc; UPDATE kitchen.cup_delivery_routes SET route_version=route_version WHERE false;");
 await sql('kitchen',"SET ROLE design_kitchen_svc; UPDATE kitchen.cup_delivery_routes SET cup_id=cup_id WHERE false;",{deny:true});
 await sql('payment',"SET ROLE design_payment_svc; UPDATE payment.session_bill_units SET unit_amount=unit_amount WHERE false;",{deny:true});
 report('Reviewed column ACL reads/allowed updates succeed; immutable identity/price UPDATE, DELETE/TRUNCATE denied');
}
