import assert from 'node:assert/strict';
import crypto from 'node:crypto';

export async function dashboardSnapshot(c,{branch,cashier,staffPassword,shift,stage}){
 const totals=JSON.parse(await c.sql(c.databases.payment,`SELECT json_build_object('cash',COALESCE(sum(amount) FILTER (WHERE method='Cash'),0),'qr',COALESCE(sum(amount) FILTER (WHERE method IN ('QR','VietQR','Mock')),0)) FROM payment.collection_outbox WHERE branch_id='${branch.id}';`));
 if(stage!=='REPORTING_OFFLINE')await c.until(async()=>{const r=await fetch(c.base+'/api/v1/reporting/collections?branchId='+branch.id,{headers:{Authorization:'Bearer '+cashier}});if(!r.ok)return false;const v=await r.json();return v.cashCollected===totals.cash&&v.qrCollected===totals.qr;},'Dashboard Reporting catches up Cash '+totals.cash+' QR '+totals.qr,90);
 const result=await c.run('cmd.exe',['/d','/s','/c','C:/src/flutter/bin/flutter.bat test test/cashier_dashboard_live_stack_test.dart --dart-define=AICOS_MAIN_API=false --reporter expanded'],{cwd:c.mobileRoot,env:{AICOS_BILL_STACK_URL:c.base,AICOS_BILL_STACK_LOGIN:'cashier@billstack.invalid',AICOS_BILL_STACK_PASSWORD:staffPassword,AICOS_BILL_STACK_BRANCH:branch.id,AICOS_DASHBOARD_STAGE:stage,AICOS_EXPECT_SHIFT:shift,AICOS_EXPECT_CASH:String(totals.cash),AICOS_EXPECT_QR:String(totals.qr)}});
 assert.match(result.out,/All tests passed/);
 c.report('Flutter real HTTP Dashboard '+stage+' at 360/393/430px: Cash '+totals.cash+', QR '+totals.qr+', shift '+shift);
}

export async function dashboardFinalChecks(c,f){
 const {request,open,add,bill,collect,cashier,cashier2,branch,staffPassword,moneyShift}=f;
 const read=async()=>(await request('GET','/api/v1/reporting/collections?branchId='+branch.id,cashier,null,null,200)).data;
 await dashboardSnapshot(c,{branch,cashier,staffPassword,shift:'CLOSED',stage:'CLOSED'});
 const closing=(await request('GET','/api/v1/payments/cashier-shifts/'+moneyShift.id,cashier,null,null,200)).data;
 const next=await open('AFTER-CLOSE');await add(next);const d=await bill(next);
 const after=await collect(d);assert.equal(after.status,c.requireShift?409:200);
 c.report('Closed drawer '+(c.requireShift?'rejects new Cash':'does not silently capture later Cash')+'; replay of earlier receipts remains valid');
 const q=await open('LATE-QR');await add(q);const draft=await bill(q);
 const qr=(await request('POST','/api/v1/payments/bills/'+draft.id+'/payments/qr',cashier,{sessionId:q.session,fingerprint:draft.fingerprint,providerCode:'Mock'},'late-qr-test-01',200)).data;
 const before=await read();
 const callback=async(status,id)=>{const ref=qr.payment.transactionRef,amount=qr.quote.total,signature=crypto.createHmac('sha256',c.webhook).update(id+'|'+ref+'|'+status+'|'+amount).digest('hex');return request('POST','/api/v1/payments/bills/webhooks/Mock',null,{eventId:id,transactionRef:ref,status,amount,signature},null,200);};
 await callback('failed','failed-test-qr');assert.equal((await bill(q)).payment.status,'FAILED');assert.equal((await read()).qrCollected,before.qrCollected);
 await callback('success','late-success-qr');await callback('success','late-success-qr');
 await c.until(async()=>(await read()).qrCollected===before.qrCollected+qr.quote.total,'late QR reaches Reporting');
 assert.equal((await bill(q)).status,'PAID');
 assert.deepEqual((await request('GET','/api/v1/payments/cashier-shifts/'+moneyShift.id,cashier,null,null,200)).data,closing);
 c.report('Mock FAILED excluded; late signed success after CLOSED shift collected once, QR branch-only, closed drawer immutable');

 // Exercise the actual Mobile Cash form against a new real Bill and own drawer.
 const shiftUi=async(step,id='')=>{
  const result=await c.run('cmd.exe',['/d','/s','/c','C:/src/flutter/bin/flutter.bat test test/cashier_shift_live_stack_test.dart --dart-define=AICOS_MAIN_API=false --reporter expanded'],{cwd:c.mobileRoot,env:{AICOS_BILL_STACK_URL:c.base,AICOS_BILL_STACK_LOGIN:'cashier@billstack.invalid',AICOS_BILL_STACK_PASSWORD:staffPassword,AICOS_BILL_STACK_BRANCH:branch.id,AICOS_MONEY_SHIFT_STEP:step,AICOS_EXPECT_SHIFT_ID:id}});assert.match(result.out,/All tests passed/);
  c.report('Flutter actual shift '+step+' UI: explicit count confirmation, persisted own branch/actor, reload without duplicate');
 };
 if(!c.precheckMode||!c.requireShift)await shiftUi('OPEN');
 const active=(await request('GET','/api/v1/payments/cashier-shifts?branchId='+branch.id+'&status=OPEN',cashier,null,null,200)).data;assert.equal(active.length,c.precheckMode&&c.requireShift?0:1);let uiShift=active[0];
 const uiSession=await open('MOBILE-CASH');await add(uiSession);
 const ui=await c.run('cmd.exe',['/d','/s','/c','C:/src/flutter/bin/flutter.bat test test/session_bill_live_stack_test.dart --name=real.Cash.UI --dart-define=AICOS_MAIN_API=false --reporter expanded'],{cwd:c.mobileRoot,env:{AICOS_BILL_STACK_URL:c.base,AICOS_BILL_STACK_LOGIN:'cashier@billstack.invalid',AICOS_BILL_STACK_PASSWORD:staffPassword,AICOS_BILL_STACK_BRANCH:branch.id,AICOS_BILL_STACK_TABLE:uiSession.table.id,AICOS_BILL_STACK_SESSION:uiSession.session,AICOS_CASH_UI:'true',AICOS_OPEN_SHIFT_FIRST:c.precheckMode&&c.requireShift?'true':'false'}});assert.match(ui.out,/All tests passed/);
 const uiPaid=await bill(uiSession);assert.equal(uiPaid.status,'PAID');assert.equal(uiPaid.payment.change,15000);
 if(c.precheckMode&&c.requireShift){const rows=(await request('GET','/api/v1/payments/cashier-shifts?branchId='+branch.id+'&status=OPEN',cashier,null,null,200)).data;assert.equal(rows.length,1);uiShift={...rows[0],version:1};c.report('Flutter denied precheck opens existing shift screen, creates own OPEN shift explicitly and rechecks before Cash form');}
 const uiCurrent=(await request('GET','/api/v1/payments/cashier-shifts/'+uiShift.id,cashier,null,null,200)).data;assert.equal(uiCurrent.cashCollected,35000);
 assert.ok(uiCurrent.version>uiShift.version);
 const closeUiUrl='/api/v1/payments/cashier-shifts/'+uiShift.id+'/close';
 const stale=await request('POST',closeUiUrl,cashier,{version:uiShift.version,countedCash:uiCurrent.expectedCash},null,409);assert.equal(stale.data.errorCode,'cashier_shift.stale');
 const stillOpen=(await request('GET','/api/v1/payments/cashier-shifts/'+uiShift.id,cashier,null,null,200)).data;assert.equal(stillOpen.status,'OPEN');assert.equal(stillOpen.version,uiCurrent.version);
 c.report('Real PostgreSQL stale shift version returns 409 before closing; accepted Cash invalidates old count snapshot');
 await shiftUi('CLOSE',uiShift.id);
 const uiClosed=(await request('GET','/api/v1/payments/cashier-shifts/'+uiShift.id,cashier,null,null,200)).data;assert.equal(uiClosed.status,'CLOSED');assert.equal(uiClosed.countedCash,135000);assert.equal(uiClosed.difference,0);
 const retryClose=(await request('POST',closeUiUrl,cashier,{version:uiCurrent.version,countedCash:uiCurrent.expectedCash,note:'Mobile matched count'},null,200)).data;
 for(const key of ['version','countedCash','expectedCash','closedAtUtc','closingNote'])assert.equal(retryClose[key],uiClosed[key]);
 assert.equal((await bill(uiSession)).status,'PAID');assert.equal((await request('GET','/api/v1/orders/qr/sessions/'+uiSession.session+'/close-eligibility',cashier,null,null,200)).data.canClose,false);
 c.report('Flutter actual Cash UI: tender 50000, change 15000, explicit received confirmation, one PAID Bill and own shift receipt');
 if(c.precheckMode){
  const takeShift=(await request('POST','/api/v1/payments/cashier-shifts',cashier,{branchId:branch.id,openingCash:100000},'precheck-takeaway-shift',200)).data;
  const order=(await request('POST','/api/v1/orders',cashier,{branchId:branch.id,type:'Takeaway',items:[{productId:f.product.id,quantity:1}]},'precheck-takeaway-order',201)).data;
  const result=await c.run('cmd.exe',['/d','/s','/c','C:/src/flutter/bin/flutter.bat test test/cash_precheck_takeaway_live_test.dart --dart-define=AICOS_MAIN_API=false --reporter expanded'],{cwd:c.mobileRoot,env:{AICOS_BILL_STACK_URL:c.base,AICOS_BILL_STACK_LOGIN:'cashier@billstack.invalid',AICOS_BILL_STACK_PASSWORD:staffPassword,AICOS_BILL_STACK_BRANCH:branch.id,AICOS_TAKEAWAY_ORDER:order.id}});assert.match(result.out,/All tests passed/);
  const row=(await request('GET','/api/v1/payments/cashier-shifts/'+takeShift.id,cashier,null,null,200)).data;assert.equal(row.cashCollected,35000);await request('POST','/api/v1/payments/cashier-shifts/'+takeShift.id+'/close',cashier,{version:row.version,countedCash:row.expectedCash},null,200);
  assert.equal(await c.sql(c.databases.payment,"SELECT count(*) FROM payment.payments WHERE order_id='"+order.id+"';"),'1');c.report('Flutter Takeaway Cash through shared precheck, one real Order payment, own drawer receipt');
 }
 // A second drawer races Cash vs close; actor lock must bind only receipts accepted before close.
 const shift=(await request('POST','/api/v1/payments/cashier-shifts',cashier2,{branchId:branch.id,openingCash:100000},'second-cashier-drawer',200)).data;
 const raceSession=await open('CLOSE-RACE');await add(raceSession);const raceBill=await bill(raceSession);
 const closeUrl='/api/v1/payments/cashier-shifts/'+shift.id+'/close';
 const [cashResult,closeResult]=await Promise.all([collect(raceBill,cashier2),request('POST',closeUrl,cashier2,{version:shift.version,countedCash:100000})]);
 assert.ok([200,409].includes(cashResult.status));assert.ok([200,409].includes(closeResult.status));assert.ok(cashResult.status===200||closeResult.status===200);
 const current=(await request('GET','/api/v1/payments/cashier-shifts/'+shift.id,cashier2,null,null,200)).data;
 if(current.status==='OPEN')await request('POST',closeUrl,cashier2,{version:current.version,countedCash:current.expectedCash},null,200);
 const corrupt=await c.sql(c.databases.payment,`SELECT count(*) FROM payment.cashier_shifts s WHERE closed_at_utc IS NOT NULL AND expected_cash<>opening_cash+COALESCE((SELECT sum(amount) FROM payment.collection_outbox o WHERE o.shift_id=s.id AND method='Cash'),0);`);assert.equal(corrupt,'0');
 c.report('Real Cash vs close concurrency preserves committed closed drawer sums');
 await c.docker(['stop',c.serviceIds.reporting]);
 await dashboardSnapshot(c,{branch,cashier,staffPassword,shift:'CLOSED',stage:'REPORTING_OFFLINE'});
 await c.docker(['start',c.serviceIds.reporting]);
 await dashboardSnapshot(c,{branch,cashier,staffPassword,shift:'CLOSED',stage:'RECOVERED'});
 const r=await read();const total=Number(await c.sql(c.databases.payment,'SELECT COALESCE(sum(amount),0) FROM payment.collection_outbox;'));assert.equal(r.confirmedCollected,total);
 const withPending=await c.sql(c.databases.payment,"SELECT count(*) FROM payment.collection_outbox o JOIN payment.session_bill_attempts a ON a.id=o.id WHERE a.status<>'SUCCEEDED';");assert.equal(withPending,'0');
 c.checks.push({name:'Payment = Reporting = Dashboard reconciliation figures',status:'PASS',evidence:{cash:r.cashCollected,qr:r.qrCollected,total:r.confirmedCollected,receipts:r.transactions,hasMore:r.hasMore,metric:r.metric}});
 c.report('Payment durable receipts = Reporting confirmed collections = Flutter Dashboard, no PENDING or duplicate revenue');
 c.checks.push({name:'Actual Identity multi-role SERVER+CASHIER',status:'BLOCKED',evidence:'Identity user request stores one canonical role. No forged multi-role JWT; Mobile combined-role contract is unit-tested only.'});
 c.checks.push({name:'Global branch Bill counts',status:'BLOCKED',evidence:'No owner-defined global all/paid/pending Bill count contract; loaded session pages are never reported as branch totals.'});
}
