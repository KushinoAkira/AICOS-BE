// Isolated fixture only. The Flutter client never receives the Mock signing secret.
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
export async function startSessionQrUi(c, {branch, session, password, getBill}) {
  assert.match(path.basename(c.temporary), /^aicos-bill-e2e-/);
  const marker = path.join(c.temporary, 'qr-ui-ready.json');
  const failure = error => { error.message += ' QR UI: ' + String(error.detail??'').split(/\r?\n/).filter(line=>/The following|Expected:|Actual:|did not reach|session_qr_live_stack_test.dart:\d+/.test(line)).slice(-10).join(' '); return error; };
  const task = c.run('cmd.exe', ['/d','/s','/c','C:/src/flutter/bin/flutter.bat test test/session_qr_live_stack_test.dart --dart-define=AICOS_MAIN_API=false --reporter expanded'], {
    cwd:c.mobileRoot,
    env:{AICOS_QR_UI_TEST:'true',AICOS_QR_UI_READY:marker,AICOS_BILL_STACK_URL:c.base,
      AICOS_BILL_STACK_LOGIN:'cashier@billstack.invalid',AICOS_BILL_STACK_PASSWORD:password,
      AICOS_BILL_STACK_BRANCH:branch.id,AICOS_BILL_STACK_TABLE:session.table.id,AICOS_BILL_STACK_SESSION:session.session}
  }).then(result => ({result}), error => ({error}));
  await Promise.race([
    c.until(async()=>{try {await fs.access(marker);return true;}catch{return false;}},'Flutter QR UI PENDING',90),
    task.then(({error})=>{throw error ? failure(error) : Error('Flutter QR UI ended before reaching PENDING');})
  ]);
  const proof = JSON.parse(await fs.readFile(marker,'utf8'));
  const bill = await getBill();
  assert.equal(bill.payment.status,'PENDING');
  assert.equal(proof.billId,bill.id);
  assert.equal(proof.transactionRef,bill.payment.transactionRef);
  assert.equal(proof.amount,bill.quote.total);
  return {bill, finish:async()=>{
    const {result,error} = await task;if(error)throw failure(error);
    assert.match(result.out,/All tests passed/);
    assert.equal(await c.sql(c.databases.payment,"SELECT count(*) FROM payment.session_bill_attempts WHERE bill_id='"+bill.id+"';"),'1');
    c.report('Flutter real QR UI creates one PENDING attempt, survives Payments restart, polls signed provider PAID; no Cash or auto-close');
  }};
}