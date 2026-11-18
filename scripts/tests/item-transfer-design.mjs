// Design proof only. Does not implement an Item Transfer API or runtime worker.
import {spawn} from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
import {fileURLToPath} from 'node:url';
import {verifyDesign} from './item-transfer-design-scenarios.mjs';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const purpose='item-transfer-design',network='aicos-item-design-'+crypto.randomBytes(5).toString('hex');
const temporary=await fs.mkdtemp(path.join(os.tmpdir(),'aicos-item-design-'));
const password=crypto.randomBytes(32).toString('hex'),containers=[],checks=[],names=['ordering','kitchen','payment'];
const db=n=>'aicos_item_transfer_design_'+n;
let pg,redis,cluster;
const run=(file,args=[],{input,env={},allowFailure=false}={})=>new Promise((resolve,reject)=>{
 const p=spawn(file,args,{cwd:root,env:{...process.env,...env},windowsHide:true,stdio:['pipe','pipe','pipe']});let out='',err='';
 p.stdout.on('data',x=>out+=x);p.stderr.on('data',x=>err+=x);p.on('error',()=>reject(Error(file+' failed to start')));
 p.on('close',code=>code===0||allowFailure?resolve({code,out,err}):reject(Object.assign(Error(file+' '+args[0]+' failed'),{detail:err||out})));
 p.stdin.end(input);
});
const docker=async(args,opts)=>(await run('docker',args,opts)).out.trim();
const report=name=>{checks.push({name,status:'PASS'});console.log('PASS '+name);};
const snapshot=async(ids)=>{const rows=[];for(const id of ids){const x=JSON.parse(await docker(['inspect',id]))[0];rows.push([x.Id,x.Image,x.State.StartedAt,x.State.Status,x.RestartCount]);}return JSON.stringify(rows);};
const prior=(await docker(['ps','-aq'])).split(/\r?\n/).filter(Boolean),before=await snapshot(prior);
async function guard(){
 const x=JSON.parse(await docker(['inspect',pg]))[0];assert.equal(x.Id,pg);assert.equal(x.Config.Labels['aicos.purpose'],purpose);
 assert.deepEqual(Object.keys(x.NetworkSettings.Networks),[network]);assert.equal(x.NetworkSettings.Ports['5432/tcp'],null);
 assert.ok(x.HostConfig.Tmpfs['/var/lib/postgresql/data']);assert.ok(!x.Mounts.some(m=>m.Type==='volume'));
 const value=await docker(['exec',pg,'psql','-U','design_admin','-d','postgres','-AtX','-c','SELECT system_identifier::text FROM pg_control_system()']);
 if(cluster)assert.equal(value,cluster);else{assert.match(value,/^\d+$/);cluster=value;}
}
async function sql(name,text,{deny=false}={}){
 assert.ok(names.includes(name)||name==='postgres');await guard();
 const settings=`SET aicos.expected_cluster='${cluster}'; SET aicos.design_approved='true'; `;
 const r=await run('docker',['exec','-i',pg,'psql','-U','design_admin','-d',name==='postgres'?'postgres':db(name),'-XAtq','-v','ON_ERROR_STOP=1'],{input:settings+text.replace(/\uFEFF/g,''),allowFailure:deny});
 if(deny){assert.notEqual(r.code,0);return r.err;}return r.out.trim();
}
try{
 await docker(['info','--format','{{.ServerVersion}}']);
 const folder=path.join(root,'docs/proposals/item-transfer');
 for(const line of (await fs.readFile(path.join(folder,'SHA256SUMS.txt'),'utf8')).trim().split(/\r?\n/)){
  const [hash,name]=line.split('  ');assert.equal(crypto.createHash('sha256').update(await fs.readFile(path.join(folder,name))).digest('hex'),hash);
 }
 const tools=path.join(temporary,'tools'),baseline=path.join(temporary,'schema');
 await run('dotnet',['publish','tests/AICOS.Billing.StackTools','-c','Debug','-o',tools,'--verbosity','quiet']);
 await run('dotnet',[path.join(tools,'AICOS.Billing.StackTools.dll'),'item-transfer-scripts',baseline]);
 await docker(['network','create','--internal','--label','aicos.purpose='+purpose,network]);
 pg=await docker(['run','-d','--network',network,'--network-alias','postgres','--label','aicos.purpose='+purpose,'--tmpfs','/var/lib/postgresql/data:rw','-e','POSTGRES_USER=design_admin','-e','POSTGRES_PASSWORD','postgres:16-alpine'],{env:{POSTGRES_PASSWORD:password}});containers.push(pg);
 redis=await docker(['run','-d','--network',network,'--label','aicos.purpose='+purpose,'--tmpfs','/data:rw','redis:7-alpine']);containers.push(redis);
 let ready=false;for(let i=0;i<60;i++){
  const r=await run('docker',['exec',pg,'pg_isready','-h','127.0.0.1','-U','design_admin'],{allowFailure:true});if(r.code===0){ready=true;break;}await new Promise(r=>setTimeout(r,500));
 }assert.ok(ready);await guard();console.log('TEST cluster '+cluster);
 report('Exact cluster/container, internal network, no host ports/volumes verified before every SQL');
 for(const [i,name] of names.entries()){
  await sql('postgres','CREATE DATABASE '+db(name)+';');await sql(name,await fs.readFile(path.join(baseline,name+'.sql'),'utf8'));
  assert.ok(Number(await sql(name,'SELECT count(*) FROM '+name+'.__ef_migrations_history;'))>0);
  assert.equal(await sql(name,"SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NULL;"),'t');
  const proposal=await fs.readFile(path.join(folder,'0'+(i+1)+'_'+name+'.sql'),'utf8');await sql(name,proposal);await sql(name,proposal,{deny:true});
  await sql(name,await fs.readFile(path.join(folder,'90_rollback.EMPTY_ONLY.sql'),'utf8'));await sql(name,proposal);
  await sql(name,'CREATE ROLE design_'+name+'_svc NOLOGIN;');await sql(name,await fs.readFile(path.join(folder,'04_acl.PROPOSAL.sql'),'utf8'));
 }
 report('Current real EF schemas plus six additive proposal tables; repeated proposal refuses duplicate objects');
 const env={DESIGN_CLUSTER:cluster};for(const name of names)env['DESIGN_'+name.toUpperCase()]=`Host=postgres;Database=${db(name)};Username=design_admin;Password=${password}`;
 const probe=mode=>docker(['run','--rm','--network',network,...Object.keys(env).flatMap(k=>['-e',k]),'--mount','type=bind,src='+tools+',dst=/probe,readonly','--entrypoint','dotnet','aicos-main/identity:local','/probe/AICOS.Billing.StackTools.dll',mode],{env});
 const fixture=JSON.parse((await probe('item-transfer-fixture')).split(/\r?\n/).at(-1));env.DESIGN_FIXTURE=JSON.stringify(fixture);
 await verifyDesign({sql,fixture,probe,report,restartRedis:async()=>{await docker(['restart',redis]);assert.equal(await docker(['exec',redis,'redis-cli','PING']),'PONG');}});
 for(const name of names)await sql(name,await fs.readFile(path.join(folder,'90_rollback.EMPTY_ONLY.sql'),'utf8'),{deny:true});
 report('Empty-only rollback/reapply succeeds unused; nonempty assignment/receipt/unit audit rollback is refused');
 assert.equal(await snapshot(prior),before);report('All preexisting container IDs/images/start/status/restart counts unchanged');
 checks.push({name:'Public transfer API/authorization and actual Barista reservation guard',status:'BLOCKED',reason:'Prototype only; current Cup UoW has no transfer reservation guard.'},
 {name:'V2 quote/freeze/PAID ACK and per-session unit serving proof',status:'BLOCKED',reason:'Current APIs are Order-scoped; six proposal tables alone do not fix these contracts.'},
 {name:'Process kill, HTTP lost ACK, Web/Mobile E2E, bank QR',status:'NOT RUN',reason:'SQL rollback/replay/Redis restart are not process-crash or HTTP/Identity E2E.'});
 console.log(`RESULT ${checks.filter(x=>x.status==='PASS').length} PASS; 2 BLOCKED; 1 NOT RUN`);
}catch(e){checks.push({name:e.message,status:'FAIL'});console.error('FAIL '+e.message);if(e.detail)console.error(String(e.detail).replaceAll(password,'[redacted]').slice(-2000));process.exitCode=1;
}finally{
 if(pg)await fs.writeFile(path.join(root,'docs/ITEM_TRANSFER_DESIGN_TEST_EVIDENCE.json'),JSON.stringify({cluster,checks,operatingContainersUnchanged:await snapshot(prior)===before,type:'Real PostgreSQL SQL design proof + EF/CupAdmissionWriter fixtures; NOT API E2E'},null,2)+'\n');
 for(const id of containers.reverse())await docker(['rm','-f',id]);await run('docker',['network','rm',network],{allowFailure:true});console.log('Removed only this run scratch containers/network; no operating DB connection or volume used.');
}
