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
 const login=async(email)=>(await request('POST','/api/v1/identity/auth/login',null,{login:email,password:userPassword},null,200)).data.accessToken;
 const superToken=await login('admin@billstack.invalid');
 const stamp=crypto.randomBytes(4).toString('hex');
 const tenant=(await request('POST','/api/v1/identity/tenants',superToken,{name:'Isolated Bill '+stamp,code:'billtest-'+stamp,firstAdmin:{fullName:'Test Owner',email:'owner@billstack.invalid',password:userPassword}},null,201)).data;
 const owner=await login('owner@billstack.invalid');
 const plan=(await request('POST','/api/v1/identity/subscription-plans',superToken,{name:'Isolated Bill Plan',tier:'Basic',fee:500000,billingPeriod:'Monthly',maxBranches:5,maxStaffAccounts:20},null,201)).data;
 await request('POST','/api/v1/identity/tenants/'+tenant.id+'/subscription',superToken,{planId:plan.id},null,201);
 const branch=(await request('POST','/api/v1/identity/branches',owner,{name:'Test Branch',address:'Test only',phone:'0900000001',openingTime:'00:00:00',closingTime:'23:59:00'},null,201)).data;
 for(const [name,role] of [['cashier','Cashier'],['cashier2','Cashier'],['barista','Barista']])await request('POST','/api/v1/identity/users',owner,{fullName:'Test '+name,email:name+'@billstack.invalid',username:name,password:userPassword,role,branchId:branch.id},null,201);
 const cashier=await login('cashier@billstack.invalid'),cashier2=await login('cashier2@billstack.invalid'),barista=await login('barista@billstack.invalid');report('Real Identity login and scoped Cashier/Barista accounts through Gateway');
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
 const replay=await collect(draft,cashier,cashKey);assert.equal(replay.status,200);assert.equal(replay.data.payment.id,paid.payment.id);report('Lost HTTP response after committed Cash; GET and same-key retry recover one payment');
 await request('POST','/api/v1/orders',cashier,orderBody(s),'paid-new-'+crypto.randomUUID(),409);
 const blocked=(await request('GET','/api/v1/orders/qr/sessions/'+s.session+'/close-eligibility',cashier,null,null,200)).data;assert.equal(blocked.canClose,false);await request('POST','/api/v1/orders/qr/sessions/'+s.session+'/close',cashier,{},null,409);report('PAID forbids Add Order and cannot close before serving');
 const mobile=await run('cmd.exe',['/d','/s','/c','C:/src/flutter/bin/flutter.bat test test/session_bill_live_stack_test.dart --dart-define=AICOS_MAIN_API=false --reporter expanded'],{cwd:path.resolve(root,'../AICOS-Mobile'),env:{AICOS_BILL_STACK_URL:base,AICOS_BILL_STACK_LOGIN:'cashier@billstack.invalid',AICOS_BILL_STACK_PASSWORD:userPassword,AICOS_BILL_STACK_BRANCH:branch.id,AICOS_BILL_STACK_TABLE:s.table.id,AICOS_BILL_STACK_SESSION:s.session}});assert.match(mobile.out,/All tests passed/);report('Flutter real HTTP login/repositories and Session Bill screen show verified PAID');
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
 c.checks.push({name:'Reporting and cash-shift reconciliation',status:'BLOCKED',evidence:{paidBillTotal:Number(received),reportedGrossRevenue:reportView.totals.grossRevenue,cashShiftLedger:Number(drawer),reason:'Existing consumers require per-order PaymentSucceeded/CashPaymentRecorded contracts; no approved Bill allocation/refund/shift contract.'}});
 console.log('BLOCKED Reporting/shift ledger omit Bill receipts; no revenue allocation policy fabricated.');
 const money=await sql(databases.payment,"SELECT count(*) FROM payment.session_bills b JOIN payment.session_bill_attempts a ON a.id=b.paid_payment_id WHERE b.status IN ('PAID','CLOSED') AND (a.status<>'SUCCEEDED' OR a.amount<>b.total);");assert.equal(money,'0');report('No PAID Bill lacks matching SUCCEEDED amount proof');
}
