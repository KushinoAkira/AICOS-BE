import {precheckContracts} from './cash-precheck-flow.mjs';
import {dashboardSnapshot, dashboardFinalChecks} from './cashier-dashboard-flow.mjs';
import {runOperationsFlow} from './table-transfer-split-flow.mjs';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import http from 'node:http';
import path from 'node:path';
export async function runFlow(c){
 const {base,userPassword,webhook,sql,databases,report,until,docker,run,serviceIds,redis,root}=c;
 async function request(method,url,token,body,key,expected){
  const response=await fetch(base+url,{method,headers:{...(token?{Authorization:'Bearer '+token}:{}),...(body?{'Content-Type':'application/json'}:{}),...(key?{'Idempotency-Key':key}:{})},body:body?JSON.stringify(body):undefined,signal:AbortSignal.timeout(25000)});
  let data;const text=await response.text();try{data=text?JSON.parse(text):null;}catch{data=null;}
  if(expected!==undefined)assert.equal(response.status,expected,method+' '+url+' HTTP '+response.status+' '+(data?.code??data?.title??''));
  return {status:response.status,data};
 }
 const login=async(email,password=userPassword)=>(await request('POST','/api/v1/identity/auth/login',null,{login:email,password},null,200)).data.accessToken;
 const superToken=await login('admin@billstack.invalid');
 const stamp=crypto.randomBytes(4).toString('hex');
 const tenant=(await request('POST','/api/v1/identity/tenants',superToken,{name:'Isolated Bill '+stamp,code:'billtest-'+stamp,firstAdmin:{fullName:'Test Owner',email:'owner@billstack.invalid',password:userPassword}},null,201)).data;
 await request('POST','/api/v1/identity/auth/login',null,{login:'owner@billstack.invalid',password:userPassword},null,403);
 const plan=(await request('POST','/api/v1/identity/subscription-plans',superToken,{name:'Isolated Bill Plan',tier:'Basic',fee:500000,billingPeriod:'Monthly',maxBranches:5,maxStaffAccounts:20},null,201)).data;
 await request('POST','/api/v1/identity/tenants/'+tenant.id+'/subscription',superToken,{planId:plan.id},null,201);
 await request('POST','/api/v1/identity/tenants/'+tenant.id+'/subscription/payments',superToken,{amount:500000,method:'BankTransfer',reference:'TEST-PLAN-'+stamp},null,201);
 const owner=await login('owner@billstack.invalid');
 report('Synthetic tenant stays closed until plan payment is recorded through real Identity API');
 const branch=(await request('POST','/api/v1/identity/branches',owner,{name:'Test Branch',address:'Test only',phone:'0900000001',openingTime:'00:00:00',closingTime:'23:59:00'},null,201)).data;
 for(const [name,role] of [['cashier','Cashier'],['cashier2','Cashier'],['barista','Barista']])await request('POST','/api/v1/identity/users',owner,{fullName:'Test '+name,email:name+'@billstack.invalid',username:name,password:userPassword,role,branchId:branch.id},null,201);
 const staffPassword=userPassword+'-Changed1!';
 for(const name of ['cashier','cashier2','barista']){const token=await login(name+'@billstack.invalid');await request('POST','/api/v1/identity/auth/change-password',token,{currentPassword:userPassword,newPassword:staffPassword,confirmNewPassword:staffPassword},null,204);}
 const cashier=await login('cashier@billstack.invalid',staffPassword),cashier2=await login('cashier2@billstack.invalid',staffPassword),barista=await login('barista@billstack.invalid',staffPassword);report('Real Identity login and scoped Cashier/Barista accounts through Gateway');
 if(c.dashboardMode)await dashboardSnapshot(c,{branch,cashier,staffPassword,shift:'NONE',stage:'EMPTY'});
 let moneyShift;
 if(c.cashierMode){moneyShift=(await request('POST','/api/v1/payments/cashier-shifts',cashier,{branchId:branch.id,openingCash:100000,note:'Test drawer'},'open-test-drawer',200)).data;await request('POST','/api/v1/payments/cashier-shifts',cashier,{branchId:branch.id,openingCash:0},'second-open-request',409);report('Money shift opens without attendance; second open request conflicts');}
 await request('GET','/api/v1/payments/bills?sessionId='+crypto.randomUUID(),null,null,null,401);
 const category=(await request('POST','/api/v1/menu/categories',owner,{name:'Test Coffee'},null,201)).data;
 const product=(await request('POST','/api/v1/menu/products',owner,{categoryId:category.id,name:'Bill Test Latte',basePrice:35000},null,201)).data;
 await request('PUT','/api/v1/menu/products/'+product.id+'/branches/'+branch.id,owner,{isPublished:true},null,200);
 const area=(await request('POST','/api/v1/tables/areas',owner,{branchId:branch.id,name:'Isolated Zone',sortOrder:0},null,201)).data;
 async function open(code){const table=(await request('POST','/api/v1/tables/dining-tables',owner,{branchId:branch.id,areaId:area.id,tableCode:code,capacity:4},null,201)).data;const scan=(await request('POST','/api/v1/orders/qr/scan',null,{qrToken:table.qrToken},null,200)).data;return {table,session:scan.sessionId};}
 const orderBody=(s,quantity=1)=>({branchId:branch.id,type:'DineIn',tableId:s.table.id,tableNumber:s.table.tableCode,tableQrToken:s.table.qrToken,expectedSessionId:s.session,items:[{productId:product.id,quantity}]});
 async function add(s,quantity=1){return (await request('POST','/api/v1/orders',cashier,orderBody(s,quantity),'order-'+crypto.randomUUID(),201)).data;}
 const bill=async(s)=>(await request('GET','/api/v1/payments/bills?sessionId='+s.session,cashier,null,null,200)).data;
 const collect=(b,who=cashier,key='cash-'+crypto.randomUUID())=>request('POST','/api/v1/payments/bills/'+b.id+'/payments/cash',who,{sessionId:b.sessionId,fingerprint:b.fingerprint,amountTendered:200000,confirmReceived:true},key);
 async function synced(s){return until(async()=>{const b=await bill(s);return b.orderingAcknowledged&&b;},'durable paid sync',75);}
 if(c.dashboardMode){
  const noShift=await open('NO-SHIFT');await add(noShift);const unassigned=await bill(noShift);
  const result=await collect(unassigned,cashier2);assert.equal(result.status,c.requireShift?409:200);
  const bad=await request('POST','/api/v1/payments/bills/'+unassigned.id+'/payments/cash',barista,{sessionId:noShift.session,fingerprint:unassigned.fingerprint,amountTendered:200000,confirmReceived:true},'barista-denied-cash');assert.equal(bad.status,403);
  const duplicates=await Promise.all(Array.from({length:3},()=>request('POST','/api/v1/payments/cashier-shifts',cashier,{branchId:branch.id,openingCash:100000,note:'Test drawer'},'open-test-drawer',200)));assert.ok(duplicates.every(x=>x.data.id===moneyShift.id));
  const foreign=(await request('POST','/api/v1/identity/branches',owner,{name:'Other Branch',address:'Test only',phone:'0900000002',openingTime:'00:00:00',closingTime:'23:59:00'},null,201)).data;
  for(const url of ['/api/v1/payments/cashier-shifts?branchId=','/api/v1/reporting/collections?branchId='])await request('GET',url+foreign.id,cashier,null,null,403);
  await request('POST','/api/v1/identity/users',owner,{fullName:'Foreign Cashier',email:'foreign@billstack.invalid',password:userPassword,role:'Cashier',branchId:foreign.id},null,201);
  const initialForeign=await login('foreign@billstack.invalid');await request('POST','/api/v1/identity/auth/change-password',initialForeign,{currentPassword:userPassword,newPassword:staffPassword,confirmNewPassword:staffPassword},null,204);
  const foreignToken=await login('foreign@billstack.invalid',staffPassword);assert.ok([403,404].includes((await collect(unassigned,foreignToken)).status), 'Foreign branch Bill must be denied; 404 may conceal ownership');
  await request('GET','/api/v1/reporting/collections?branchId='+branch.id,null,null,null,401);await request('GET','/api/v1/reporting/collections?branchId='+branch.id,'invalid-token',null,null,401);

  for(const url of ['/api/v1/payments/cashier-shifts?branchId=','/api/v1/reporting/collections?branchId='])await request('GET',url+branch.id,barista,null,null,403);
  report('Cash shift enforcement '+(c.requireShift?'ON rejects unassigned Cash':'OFF preserves unassigned Cash')+'; parallel open replay and real JWT branch/role isolation');
 }
 if(c.precheckMode)await precheckContracts(c,{request,branch,cashier,cashier2,barista,owner,open,add,bill,collect});
 const s=await open('B01'),o1=await add(s),o2=await add(s,2);const draft=await bill(s);assert.equal(draft.quote.total,105000);assert.equal(draft.quote.orders.length,2);report('Real Table -> Menu prices -> two Orders -> one aggregate Bill');
 const queue=async()=>(await request('GET','/api/v1/kitchen/queue',barista,null,null,200)).data;
 await until(async()=>{const q=await queue();return q.some(t=>t.orderId===o1.id)&&q.some(t=>t.orderId===o2.id);},'Kitchen real Redis/admission',75);report('Unpaid DineIn reaches Kitchen before payment');
 await until(async()=>Number(await sql(databases.ordering,"SELECT count(*) FROM ordering.kitchen_admission_outbox WHERE order_id IN ('"+o1.id+"','"+o2.id+"') AND state='ACKED';"))===2,'real machine admission ACK',75);report('Both Orders durably ACKED by Kitchen admission HTTP, not only Redis');
 const cashKey='lost-'+crypto.randomUUID();let backendAccepted=false;
 const proxy=http.createServer(async(req,res)=>{let body='';for await(const part of req)body+=part;try{const response=await fetch(base+req.url,{method:req.method,headers:{Authorization:'Bearer '+cashier,'Content-Type':'application/json','Idempotency-Key':cashKey},body});await response.text();backendAccepted=response.ok;res.destroy();}catch{res.destroy();}});
 await new Promise(r=>proxy.listen(0,'127.0.0.1',r));
 try{await fetch('http://127.0.0.1:'+proxy.address().port+'/api/v1/payments/bills/'+draft.id+'/payments/cash',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({sessionId:s.session,fingerprint:draft.fingerprint,amountTendered:200000,confirmReceived:true})});assert.fail('Proxy must drop response');}catch(error){if(error.code==='ERR_ASSERTION')throw error;}finally{await new Promise(r=>proxy.close(r));}
 assert.equal(backendAccepted,true);const paid=await synced(s);assert.equal(paid.status,'PAID');
 const invoice=(await request('GET','/api/v1/payments/bills/'+paid.id+'/invoice',cashier,null,null,200)).data;assert.equal(invoice.quote.total,105000);assert.equal(invoice.payment.status,'SUCCEEDED');await request('GET','/api/v1/payments/bills/'+paid.id+'/invoice',barista,null,null,403);report('Real paid invoice snapshot and Counter-only RBAC verified');
 const history=(await request('GET','/api/v1/payments/bills/'+paid.id+'/history?sessionId='+s.session,cashier,null,null,200)).data;assert.equal(history.billId,paid.id);assert.equal(history.sessionId,s.session);assert.equal(history.attempts.length,1);assert.ok(history.attempts[0].cashierUserId);assert.ok(history.events.length>0);assert.ok(history.events.every(e=>!('payload' in e)));await request('GET','/api/v1/payments/bills/'+paid.id+'/history?sessionId='+s.session,barista,null,null,403);report('Persisted Bill history through Gateway: scoped receipt actor, journal metadata only, Barista denied');
 const replay=await collect(draft,cashier,cashKey);assert.equal(replay.status,200);assert.equal(replay.data.payment.id,paid.payment.id);report('Lost HTTP response after committed Cash; GET and same-key retry recover one payment');
 await request('POST','/api/v1/orders',cashier,orderBody(s),'paid-new-'+crypto.randomUUID(),409);
 const blocked=(await request('GET','/api/v1/orders/qr/sessions/'+s.session+'/close-eligibility',cashier,null,null,200)).data;assert.equal(blocked.canClose,false);await request('POST','/api/v1/orders/qr/sessions/'+s.session+'/close',cashier,{},null,409);report('PAID forbids Add Order and cannot close before serving');
 if(c.dashboardMode)await dashboardSnapshot(c,{branch,cashier,staffPassword,shift:'OPEN',stage:'PAID'});
 const mobile=await run('cmd.exe',['/d','/s','/c','C:/src/flutter/bin/flutter.bat test test/session_bill_live_stack_test.dart --dart-define=AICOS_MAIN_API=false --reporter expanded'],{cwd:c.mobileRoot||path.resolve(root,'../AICOS-Mobile'),env:{AICOS_BILL_STACK_URL:base,AICOS_BILL_STACK_LOGIN:'cashier@billstack.invalid',AICOS_BILL_STACK_PASSWORD:staffPassword,AICOS_BILL_STACK_BRANCH:branch.id,AICOS_BILL_STACK_TABLE:s.table.id,AICOS_BILL_STACK_SESSION:s.session}});assert.match(mobile.out,/All tests passed/);report('Flutter real HTTP login/repositories and Session Bill screen show verified PAID');
 async function serve(order){
  let t=await until(async()=>(await queue()).find(x=>x.orderId===order.id),'ticket '+order.id,75);
  for(const status of ['Brewing','ReadyForPickup']){const previous=t.status;const result=await request('PUT','/api/v1/kitchen/tickets/'+t.id+'/status',barista,{status,expectedStatus:previous,expectedRemakeCount:t.remakeCount},null,200);t=result.data;}
  await until(async()=>{const r=await request('GET','/api/v1/orders/'+order.id,cashier,null,null,200);return r.data.status==='Ready';},'real Kitchen -> Ordering Redis event');
  await request('POST','/api/v1/orders/'+order.id+'/deliver',cashier,{},null,200);
 }
 await serve(o1);await serve(o2);
 const eligible=(await request('GET','/api/v1/orders/qr/sessions/'+s.session+'/close-eligibility',cashier,null,null,200)).data;assert.equal(eligible.canClose,true);
 const closePath='/api/v1/orders/qr/sessions/'+s.session+'/close';const closes=await Promise.all([request('POST',closePath,cashier,{}),request('POST',closePath,cashier2,{})]);assert.ok(closes.every(r=>r.status===204));assert.equal((await bill(s)).status,'CLOSED');
 const table=(await request('GET','/api/v1/tables/dining-tables/'+s.table.id,cashier,null,null,200)).data;assert.equal(table.status,'Available');report('Real Barista -> serving -> parallel close -> Table Available with one CLOSED Bill');
 const s2=await open('B02');await add(s2);const d2=await bill(s2);const charges=await Promise.all([collect(d2),collect(d2,cashier2)]);assert.equal(charges.filter(x=>x.status===200).length,1);assert.equal(charges.filter(x=>x.status===409).length,1);report('Two real cashier accounts race: exactly one collection');
 const s3=await open('B03');await add(s3);const d3=await bill(s3);const race=await Promise.all([request('POST','/api/v1/orders',cashier,orderBody(s3),'race-'+crypto.randomUUID()),collect(d3)]);assert.ok((race[0].status===201&&race[1].status===409)||(race[0].status===409&&race[1].status===200));report('Real Add Order vs Cash race linearizes without omitted round');
 const s4=await open('B04');await add(s4);await until(async()=>(await queue()).some(t=>t.tableNumber==='B04'),'admission before outage');
 await docker(['stop',serviceIds.kitchen]);const cash4=await collect(await bill(s4));assert.equal(cash4.status,200);assert.equal(cash4.data.status,'PAID');assert.equal(cash4.data.orderingAcknowledged,false);
 await docker(['restart',serviceIds.payment]);await docker(['restart',redis]);await docker(['start',serviceIds.kitchen]);await synced(s4);report('Durable PAID outbox retries after Kitchen outage and Payments/Redis restart');
 const s5=await open('B05');await add(s5);const qdraft=await bill(s5);const qr=(await request('POST','/api/v1/payments/bills/'+qdraft.id+'/payments/qr',cashier,{sessionId:s5.session,fingerprint:qdraft.fingerprint,providerCode:'Mock'},'qr-'+crypto.randomUUID(),200)).data;assert.equal(qr.status,'UNPAID');assert.equal(qr.payment.status,'PENDING');
 await docker(['restart',serviceIds.payment]);await until(async()=>{const r=await request('GET','/api/v1/payments/bills?sessionId='+s5.session,cashier);return r.status===200;},'Payments restart');assert.equal((await bill(s5)).payment.id,qr.payment.id);
 if(c.dashboardMode)await dashboardSnapshot(c,{branch,cashier,staffPassword,shift:'OPEN',stage:'QR_PENDING'});
 const eventId='mock-'+crypto.randomUUID(),ref=qr.payment.transactionRef,amount=qr.quote.total;
 const signature=crypto.createHmac('sha256',webhook).update(eventId+'|'+ref+'|success|'+amount).digest('hex');
 const callback={eventId,transactionRef:ref,status:'success',amount,signature};
 await request('POST','/api/v1/payments/bills/webhooks/Mock',null,{...callback,signature:'invalid'},null,401);
 const receipts=await Promise.all(Array.from({length:5},()=>request('POST','/api/v1/payments/bills/webhooks/Mock',null,callback)));assert.ok(receipts.every(r=>r.status===200));const qpaid=await synced(s5);assert.equal(qpaid.status,'PAID');assert.equal(qpaid.providerIsTest,true);
 assert.equal(await sql(databases.payment,"SELECT count(*) FROM payment.session_bill_events WHERE bill_id='"+qr.id+"' AND kind='PROVIDER_RECEIPT';"),'1');report('Mock only: PENDING survives restart; signed duplicate callbacks apply once; bad signature 401');
 const reportView=(await request('GET','/api/v1/reporting/branch?branchId='+branch.id,owner,null,null,200)).data;
 const received=await sql(databases.payment,"SELECT COALESCE(sum(total),0) FROM payment.session_bills WHERE status IN ('PAID','CLOSED');");
 const drawer=await sql(databases.workforce,'SELECT COALESCE(sum(amount),0) FROM workforce.cash_transactions;');
 assert.ok(Number(received)>0);assert.equal(reportView.totals.grossRevenue,0);assert.equal(Number(drawer),0);
 if(c.cashierMode){
  const read=async()=>(await request('GET','/api/v1/reporting/collections?branchId='+branch.id,cashier,null,null,200)).data;
  await until(async()=>Number((await read()).confirmedCollected)===Number(received),'Bill collections reach Reporting',90);
  const r=await read();assert.equal(r.netSales,null);assert.equal(r.transactions,r.rows.length);report('Confirmed Bill collections now match Payments ledger; no fake per-order/net-sales revenue');
  await sql(databases.payment,'UPDATE payment.collection_outbox SET delivered_at_utc=NULL;');await docker(['restart',serviceIds.payment]);await docker(['restart',serviceIds.reporting]);await docker(['restart',redis]);
  await until(async()=>Number(await sql(databases.payment,'SELECT count(*) FROM payment.collection_outbox WHERE delivered_at_utc IS NULL;'))===0,'duplicate outbox ACKs after restart',90);assert.equal((await read()).confirmedCollected,r.confirmedCollected);report('Lost ACK replay after Payments/Reporting/Redis restart does not double count');
  const id=moneyShift.id,shift=(await request('GET','/api/v1/payments/cashier-shifts/'+id,cashier,null,null,200)).data;
  assert.equal(shift.qrCollected,null);assert.equal(shift.expectedCash,100000+shift.cashCollected);assert.equal(shift.canClose,true);
  const closing={version:shift.version,countedCash:shift.expectedCash,note:'Matched isolated count'};
  const closes=await Promise.all([request('POST','/api/v1/payments/cashier-shifts/'+id+'/close',cashier,closing),request('POST','/api/v1/payments/cashier-shifts/'+id+'/close',cashier,closing)]);assert.ok(closes.every(x=>x.status===200));
  await request('POST','/api/v1/payments/cashier-shifts/'+id+'/close',cashier,{...closing,countedCash:1},null,409);await request('GET','/api/v1/payments/cashier-shifts/'+id,cashier2,null,null,403);
  const history=(await request('GET','/api/v1/payments/cashier-shifts?branchId='+branch.id+'&status=CLOSED',cashier,null,null,200)).data;assert.ok(history.some(x=>x.id===id));report('Cash drawer counts reconcile, parallel close is idempotent, closed history immutable and foreign cashier denied');
  c.checks.push({name:'Gross/net sales and refund/manager allocation policy',status:'BLOCKED',evidence:{confirmedCollections:r.confirmedCollected,legacyGross:reportView.totals.grossRevenue,reason:'Collections are fixed; net sales/refund allocation and manager approval policy remain distinct and are not fabricated.'}});
 }else{
  c.checks.push({name:'Reporting and cash-shift reconciliation',status:'BLOCKED',evidence:{paidBillTotal:Number(received),reportedGrossRevenue:reportView.totals.grossRevenue,cashShiftLedger:Number(drawer),reason:'CashierShifts feature disabled.'}});
  console.log('BLOCKED Reporting/shift feature disabled in baseline stack.');
 }
 if(c.dashboardMode)await dashboardFinalChecks(c,{request,open,add,bill,collect,cashier,cashier2,barista,branch,staffPassword,moneyShift,product});
 if(c.operationsMode)await runOperationsFlow({...c,request,open,add,bill,cashier,cashier2,barista,owner,branch,area,product});
 const money=await sql(databases.payment,"SELECT count(*) FROM payment.session_bills b JOIN payment.session_bill_attempts a ON a.id=b.paid_payment_id WHERE b.status IN ('PAID','CLOSED') AND (a.status<>'SUCCEEDED' OR a.amount<>b.total);");assert.equal(money,'0');report('No PAID Bill lacks matching SUCCEEDED amount proof');
}
