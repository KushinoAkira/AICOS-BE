import assert from 'node:assert/strict';
import crypto from 'node:crypto';
export async function runOperationsFlow(c){
 const {request,open,add,bill,cashier,cashier2,barista,owner,branch,area,docker,serviceIds,until,sql,databases,report}=c;
 const queue=async()=>(await request('GET','/api/v1/kitchen/queue',barista,null,null,200)).data;
 const table=async(code)=>(await request('POST','/api/v1/tables/dining-tables',owner,{branchId:branch.id,areaId:area.id,tableCode:code,capacity:4},null,201)).data;
 const s=await open('OP01'),order=await add(s),draft=await bill(s);await until(async()=>(await queue()).some(t=>t.orderId===order.id),'operations ticket admitted');
 const line=draft.quote.lines[0];assert.ok(line.orderItemId);const a=crypto.randomUUID(),b=crypto.randomUUID();const path='/api/v1/payments/bills/'+draft.id+'/split-plan';
 const save={sessionId:s.session,version:0,fingerprint:draft.fingerprint,groups:[{id:a,name:'A',items:[{orderItemId:line.orderItemId,quantity:1}]},{id:b,name:'B',items:[]}]};
 const rootCount=await sql(databases.payment,'SELECT count(*) FROM payment.session_bills;');
 let plan=(await request('PUT',path,cashier,save,'split-e2e-save',200)).data;assert.equal(plan.paymentAvailable,false);assert.equal(plan.allocationComplete,true);assert.equal(plan.groups[0].amountDue,null);
 await request('PUT',path,cashier,save,'split-e2e-save',200);
 const merged={...save,version:1,groups:[{id:crypto.randomUUID(),name:'AB',items:[{orderItemId:line.orderItemId,quantity:1}]}]};plan=(await request('PUT',path,cashier,merged,'split-e2e-merge',200)).data;assert.equal(plan.groups.length,1);assert.equal(plan.allocatedSubtotal,draft.quote.subtotal);
 await request('PUT',path,cashier,{...merged,version:2,groups:[]},'split-e2e-delete',409);assert.equal(await sql(databases.payment,'SELECT count(*) FROM payment.session_bills;'),rootCount);report('Real partial allocation, merge, delete-loss guard and retry keep one original Bill with no money collected');
 const target=await table('OP02');await docker(['stop',serviceIds.kitchen]);
 const body={expectedTableId:s.table.id,targetQrToken:target.qrToken};let move=(await request('POST','/api/v1/orders/qr/sessions/'+s.session+'/transfer',cashier,body,'move-e2e-request',202)).data;assert.equal(move.syncStatus,'PENDING_SYNC');
 const replay=(await request('POST','/api/v1/orders/qr/sessions/'+s.session+'/transfer',cashier,body,'move-e2e-request',202)).data;assert.equal(replay.operationId,move.operationId);assert.equal((await bill(s)).id,draft.id);assert.equal((await bill(s)).quote.total,draft.quote.total);
 await docker(['restart',serviceIds.order]);await docker(['start',serviceIds.kitchen]);
 await until(async()=>{const r=await request('GET','/api/v1/orders/qr/transfers/'+move.operationId,cashier);return r.status===200&&r.data.syncStatus==='COMPLETED';},'durable table routing after Ordering/Kitchen restart',90);
 assert.equal((await queue()).find(t=>t.orderId===order.id).tableNumber,'OP02');const moved=await bill(s);assert.equal(moved.quote.tableId,target.id);assert.equal(moved.id,draft.id);report('Kitchen offline returns PENDING; transfer journal survives restart and routes one original ticket without moving Bill identity');
 const history=(await request('GET','/api/v1/orders/qr/sessions/'+s.session+'/transfers',cashier,null,null,200)).data;assert.equal(history.length,1);assert.equal(history[0].sourceCode,'OP01');assert.equal(history[0].tableCode,'OP02');
 await request('POST','/api/v1/orders/qr/sessions/'+s.session+'/transfer',cashier,body,'move-e2e-request',200);
 await request('POST','/api/v1/orders/qr/sessions/'+s.session+'/transfer',barista,body,'move-barista-forbidden',403);report('Transfer history preserves source/target/actor and Barista cannot transfer');
 const second=await open('OP03');await add(second);const t1=await table('OP04'),t2=await table('OP05');const race=await Promise.all([[cashier,t1,'move-race-first'],[cashier2,t2,'move-race-second']].map(([who,t,key])=>request('POST','/api/v1/orders/qr/sessions/'+second.session+'/transfer',who,{expectedTableId:second.table.id,targetQrToken:t.qrToken},key)));
 assert.equal(race.filter(r=>r.status===200||r.status===202).length,1);assert.equal(race.filter(r=>r.status===409).length,1);assert.equal(await sql(databases.ordering,"SELECT count(*) FROM ordering.table_transfer_operations WHERE session_id='"+second.session+"';"),'1');report('Two real Staff actors race one session move: one durable winner, one 409');
 c.checks.push({name:'Item Transfer, CupV1 routing and group-payment policy',status:'BLOCKED',evidence:{reason:'Real item transfer design awaits approval; Cup admission source/route compatibility and discount/tax/fee/group payment contracts are not bypassed.'}});
}
