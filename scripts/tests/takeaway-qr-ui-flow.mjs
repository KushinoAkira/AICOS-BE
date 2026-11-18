import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
export async function startTakeawayQrUi(c, {order,branch,staffPassword}) {
 assert.match(path.basename(c.temporary),/^aicos-bill-e2e-/);
 const marker=path.join(c.temporary,'takeaway-qr-ready.json');
 const task=c.run('cmd.exe',['/d','/s','/c','C:/src/flutter/bin/flutter.bat test test/takeaway_qr_live_stack_test.dart --dart-define=AICOS_MAIN_API=false --reporter expanded'],{
  cwd:c.mobileRoot,env:{AICOS_TAKEAWAY_QR_UI_TEST:'true',AICOS_TAKEAWAY_QR_READY:marker,AICOS_TAKEAWAY_ORDER:order.id,
   AICOS_BILL_STACK_URL:c.base,AICOS_BILL_STACK_LOGIN:'cashier@billstack.invalid',AICOS_BILL_STACK_PASSWORD:staffPassword,AICOS_BILL_STACK_BRANCH:branch.id}
 }).then(result=>({result}),error=>{error.message += " Flutter: " + String(error.detail??"").split(/\r?\n/).filter(s=>/The following|Expected:|Actual:|assertion|Exception|Error|Timer|pending|disposed|takeaway_qr_live_stack_test.dart:\d+/.test(s)).slice(0,25).join(" ");return {error};});
 await Promise.race([c.until(async()=>{try{await fs.access(marker);return true;}catch{return false;}},'Takeaway Flutter Pending',120),
  task.then(({error})=>{throw error??Error('Takeaway UI ended before Pending');})]);
 const proof=JSON.parse(await fs.readFile(marker,'utf8'));assert.equal(proof.orderId,order.id);
 return async()=>{await fs.writeFile(path.join(c.temporary,"takeaway-qr-confirmed.json"),"{}");const {result,error}=await task;if(error){const text=String(error.detail??error.message);const begin=text.indexOf("EXCEPTION CAUGHT");const evidence=text.slice(begin<0?0:begin,(begin<0?0:begin)+2300).replaceAll(staffPassword,"[redacted]").replace(/eyJ[A-Za-z0-9_.-]{40,}/g,"[redacted token]");c.checks.push({name:"Flutter Takeaway QR live UI",status:"FAIL",evidence});console.log("FAIL Flutter live UI: "+evidence);process.exitCode=1;return;}assert.match(result.out,/All tests passed/);c.report('Flutter real HTTP creates Mock QR, exits PENDING, reopens after callback and renders verified CONFIRMED');};
}