// Disposable full-stack runner. No operating .env, Docker Compose project or credentials are read.
import {spawn} from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
import {fileURLToPath} from 'node:url';
import {runFlow} from './session-billing-stack-flow.mjs';
import {runBaristaFlow} from './barista-stack-flow.mjs';
const baristaMode=process.argv.includes('--barista');
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const purpose='session-bill-e2e',suffix=crypto.randomBytes(5).toString('hex'),network='aicos-bill-e2e-'+suffix;
const temporary=await fs.mkdtemp(path.join(os.tmpdir(),'aicos-bill-e2e-'));
const containers=[],checks=[],serviceIds={};let cluster,pg,gateway,base;
const secret=()=>crypto.randomBytes(32).toString('hex');
const printerKey=secret();
const adminPassword=secret(),userPassword='Test-Aa1!'+secret(),jwt=secret(),machine=secret(),admission=secret(),webhook=secret();
const services=[['identity','IdentityOrganization','AICOS.IdentityOrganization.Api','identity'],['menu','MenuCatalog','AICOS.MenuCatalog.Api','menu'],['table','TableReservation','AICOS.TableReservation.Api','table_reservation'],['order','Ordering','AICOS.Ordering.Api','ordering'],['kitchen','KitchenOperations','AICOS.KitchenOperations.Api','kitchen'],['payment','Payments','AICOS.Payments.Api','payment'],['reporting','ReportingAnalytics','AICOS.ReportingAnalytics.Api','reporting'],['workforce','WorkforceScheduling','AICOS.WorkforceScheduling.Api','workforce'],['gateway',null,'AICOS.Gateway',null]];
const databases=Object.fromEntries(services.filter(s=>s[3]).map(([name,,,schema])=>[schema,schema==='payment'?'aicos_billflow_test':'aicos_billstack_'+schema]));
const passwords=Object.fromEntries(Object.keys(databases).map(s=>[s,secret()]));
const role=s=>s==='payment'?'payment_svc':s==='ordering'?'ordering_svc':s+'_svc';
const run=(file,args=[],{input,env={},allowFailure=false,cwd=root}={})=>new Promise((resolve,reject)=>{
 const p=spawn(file,args,{cwd,env:{...process.env,...env},windowsHide:true,stdio:['pipe','pipe','pipe']});let out='',err='';
 p.stdout.on('data',d=>out+=d);p.stderr.on('data',d=>err+=d);p.on('error',()=>reject(Error(file+' could not start')));
 p.on('close',code=>code===0||allowFailure?resolve({code,out,err}):reject(Object.assign(Error(file+' '+args[0]+' failed (details retained in memory, not dumped)'),{detail:err||out})));
 p.stdin.end(input);
});
const docker=async(args,options)=>(await run('docker',args,options)).out.trim();
const report=(name)=>{checks.push({name,status:'PASS'});console.log('PASS '+name);};
const snapshot=async(ids)=>{const a=[];for(const id of ids){const s=JSON.parse(await docker(['inspect',id]))[0];a.push([s.Id,s.Image,s.State.StartedAt,s.State.Status,s.RestartCount]);}return JSON.stringify(a);};
const existing=(await docker(['ps','-aq'])).split(/\r?\n/).filter(Boolean),before=await snapshot(existing);
async function guard(){
 const x=JSON.parse(await docker(['inspect',pg]))[0];assert.equal(x.Id,pg);assert.equal(x.Config.Labels['aicos.purpose'],purpose);assert.deepEqual(Object.keys(x.NetworkSettings.Networks),[network]);assert.equal(x.NetworkSettings.Ports['5432/tcp'],null);assert.ok(x.HostConfig.Tmpfs['/var/lib/postgresql/data']);assert.ok(!x.Mounts.some(m=>m.Type==='volume'));
 const actual=await docker(['exec',pg,'psql','-U','bill_stack_admin','-d','postgres','-AtX','-c','SELECT system_identifier FROM pg_control_system()']);if(cluster)assert.equal(actual,cluster);else{assert.match(actual,/^\d+$/);cluster=actual;}
}
async function sql(db,text,{schema,approval,user='bill_stack_admin',deny=false}={}){
 await guard();assert.ok(Object.values(databases).includes(db)||db.startsWith('aicos_billstack_restore_')||db==='postgres');
 const env={},args=['exec','-i'];if(user!=='bill_stack_admin'){env.PGPASSWORD=passwords[schema];args.push('-e','PGPASSWORD');}
 args.push(pg,'psql','-X','-v','ON_ERROR_STOP=1','-U',user,'-d',db,'-Atq');if(user!=='bill_stack_admin')args.push('-h','127.0.0.1');
 const settings=schema?"SET aicos.expected_database='"+db+"'; SET aicos.expected_schema='"+schema+"'; ":'';
 const result=await run('docker',args,{env,input:settings+"SET aicos.expected_cluster='"+cluster+"'; "+(approval?"SET aicos.barista_"+approval+"_approved='true'; ":'')+(approval?"SET aicos.bill_"+approval+"_approved='true'; ":'')+text,allowFailure:deny});if(deny){assert.notEqual(result.code,0);return result;}return result.out.trim();
}
async function create(name,image,args=[],env={}){
 const keys=Object.keys(env).flatMap(k=>['-e',k]);const id=await docker(['run','-d','--name',network+'-'+name,'--label','aicos.purpose='+purpose,'--network',network,'--network-alias',name,...keys,...args,image],{env});containers.push(id);return id;
}
async function until(action,label,seconds=90){let last;for(let i=0;i<seconds*2;i++){try{last=await action();if(last)return last;}catch{}await new Promise(r=>setTimeout(r,500));}throw Error('Timeout: '+label);}
async function publish(project,out){const result=await run('dotnet',['publish',project,'-c','Debug','-o',out,'--verbosity','quiet']);return result;}
const sqlPath=n=>path.join(root,'docs/migrations/session-billing',n);
const script=n=>fs.readFile(sqlPath(n),'utf8');
try{
 await docker(['info','--format','{{.ServerVersion}}']);
 for(const dir of ['migrations','proposals']){
  const p=path.join(root,'docs',dir,'session-billing');for(const line of (await fs.readFile(path.join(p,'SHA256SUMS.txt'),'utf8')).trim().split(/\r?\n/)){
   const [hash,name]=line.split('  ');assert.equal(crypto.createHash('sha256').update(await fs.readFile(path.join(p,name))).digest('hex'),hash);
  }
 }report('SQL SHA256 matches reviewed manifests');
 const tools=path.join(temporary,'tools'),baselines=path.join(temporary,'baseline');
 await publish('tests/AICOS.Billing.StackTools/AICOS.Billing.StackTools.csproj',tools);
 await run('dotnet',[path.join(tools,'AICOS.Billing.StackTools.dll'),'scripts',baselines]);report('Offline EF baseline export without service startup');
 await docker(['network','create','--label','aicos.purpose='+purpose,network]);
 pg=await create('postgres','postgres:16-alpine',['--tmpfs','/var/lib/postgresql/data:rw'],{POSTGRES_DB:'postgres',POSTGRES_USER:'bill_stack_admin',POSTGRES_PASSWORD:adminPassword});
 await until(async()=>{const r=await run('docker',['exec',pg,'pg_isready','-h','127.0.0.1','-U','bill_stack_admin'],{allowFailure:true});return r.code===0;},'PostgreSQL');await guard();report('Container/network/no-host-port/tmpfs/cluster guards');console.log('TEST cluster '+cluster);
 for(const [schema,db] of Object.entries(databases)){
  await sql('postgres','CREATE DATABASE '+db+'; CREATE ROLE '+role(schema)+" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT PASSWORD '"+passwords[schema]+"'; REVOKE CONNECT ON DATABASE "+db+' FROM PUBLIC; GRANT CONNECT ON DATABASE '+db+' TO '+role(schema)+';');
  await sql(db,await fs.readFile(path.join(baselines,baristaMode&&schema==='kitchen'?'kitchen.pre-barista.sql':schema+'.sql'),'utf8'));
  await sql(db,'GRANT USAGE ON SCHEMA '+schema+' TO '+role(schema)+'; GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA '+schema+' TO '+role(schema)+'; GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA '+schema+' TO '+role(schema)+'; REVOKE ALL ON '+schema+'.__ef_migrations_history FROM '+role(schema)+';');
 }
 if(baristaMode){
  const folder=path.join(root,'docs/migrations/barista-workflow');
  for(const line of (await fs.readFile(path.join(folder,'SHA256SUMS.txt'),'utf8')).trim().split(/\r?\n/)){const [hash,name]=line.split('  ');assert.equal(crypto.createHash('sha256').update(await fs.readFile(path.join(folder,name))).digest('hex'),hash);}
  const bs=n=>fs.readFile(path.join(folder,n),'utf8'),db=databases.kitchen;
  await sql(db,await bs('00_preflight.READ_ONLY.sql'),{schema:'kitchen'});
  await docker(['exec',pg,'pg_dump','-U','bill_stack_admin','-Fc','-d',db,'-f','/tmp/kitchen-before.dump']);
  await sql(db,await bs('01_expand.sql'),{schema:'kitchen',approval:'expand'});
  await sql(db,await bs('01_expand.sql'),{schema:'kitchen',approval:'expand',deny:true});
  await sql(db,await bs('02_service_grants.PROPOSAL.sql'),{schema:'kitchen',approval:'grants'});
  await sql(db,await bs('03_verify.READ_ONLY.sql'),{schema:'kitchen'});
  await sql(db,'DELETE FROM kitchen.barista_acceptances WHERE false;',{schema:'kitchen',user:'kitchen_svc',deny:true});
  await sql(db,"UPDATE kitchen.kitchen_print_jobs SET payload='{}'::jsonb WHERE false;",{schema:'kitchen',user:'kitchen_svc',deny:true});
  const clone='aicos_billstack_restore_kitchen';await sql('postgres','CREATE DATABASE '+clone+';');
  await docker(['exec',pg,'pg_restore','-U','bill_stack_admin','-d',clone,'/tmp/kitchen-before.dump']);
  await sql(clone,await bs('01_expand.sql'),{schema:'kitchen',approval:'expand'});
  await sql(clone,await bs('90_rollback.EMPTY_ONLY.sql'),{schema:'kitchen',approval:'rollback'});
  assert.equal(await sql(clone,"SELECT to_regclass('kitchen.barista_acceptances') IS NULL AND to_regclass('kitchen.kitchen_print_jobs') IS NULL;"),'t');
  await sql(clone,await bs('01_expand.sql'),{schema:'kitchen',approval:'expand'});
  report('Barista migration checksum/preflight/expand/repeat refusal, column ACL, restore/empty rollback/reapply PASS');
 }
 const fixtureEnv={BILL_STACK_ADMIN_CONNECTION:'Host=postgres;Database='+databases.identity+';Username=bill_stack_admin;Password='+adminPassword,BILL_STACK_CLUSTER:cluster,BILL_STACK_USER_PASSWORD:userPassword};
 const fixtureArgs=Object.keys(fixtureEnv).flatMap(k=>['-e',k]);await docker(['run','--rm','--network',network,...fixtureArgs,'--mount','type=bind,src='+tools+',dst=/testtools,readonly','--entrypoint','dotnet','aicos-main/identity:local','/testtools/AICOS.Billing.StackTools.dll','fixtures'],{env:fixtureEnv});
 report('Synthetic fixtures only; no operating data/secrets copied');
 const sampleBefore={};for(const schema of ['ordering','payment']){
  const db=databases[schema],table=schema==='ordering'?'table_sessions':'payment_providers';sampleBefore[schema]=await sql(db,'SELECT md5(string_agg(row_to_json(t)::text,chr(124) ORDER BY id)) FROM '+schema+'.'+table+' t;');
  await docker(['exec',pg,'pg_dump','-U','bill_stack_admin','-Fc','-d',db,'-f','/tmp/'+schema+'-before.dump']);
  await sql(db,await script('00_preflight.READ_ONLY.sql'),{schema});await sql(db,await script(schema==='ordering'?'01_ordering_expand.sql':'02_payments_expand.sql'),{schema,approval:'expand'});
  await sql(db,await fs.readFile(path.join(baselines,schema+'.post.sql'),'utf8'));
  assert.equal(await sql(db,'SELECT md5(string_agg(row_to_json(t)::text,chr(124) ORDER BY id)) FROM '+schema+'.'+table+' t;'),sampleBefore[schema]);
  await sql(db,await script(schema==='ordering'?'01_ordering_expand.sql':'02_payments_expand.sql'),{schema,approval:'expand',deny:true});
  await sql(db,await script('03_service_grants.PROPOSAL.sql'),{schema,approval:'grants'});
  await sql(db,await script('04_verify.READ_ONLY.sql'),{schema});
  await sql(db,'SELECT current_database(),current_user; SELECT count(*) FROM '+schema+'.'+(schema==='ordering'?'session_checkouts':'session_bills')+';',{schema,user:role(schema)});
  const audit=schema==='ordering'?'session_checkouts':'session_bill_events';await sql(db,'DELETE FROM '+schema+'.'+audit+' WHERE false;',{schema,user:role(schema),deny:true});
  if(schema==='payment')await sql(db,"UPDATE payment.session_bill_events SET payload='{}'::jsonb WHERE false;",{schema,user:role(schema),deny:true});
  const clone='aicos_billstack_restore_'+schema;await sql('postgres','CREATE DATABASE '+clone+';');
  await docker(['exec',pg,'pg_restore','-U','bill_stack_admin','-d',clone,'/tmp/'+schema+'-before.dump']);
  assert.equal(await sql(clone,'SELECT md5(string_agg(row_to_json(t)::text,chr(124) ORDER BY id)) FROM '+schema+'.'+table+' t;'),sampleBefore[schema]);
  await sql(clone,await script(schema==='ordering'?'01_ordering_expand.sql':'02_payments_expand.sql'),{schema,approval:'expand'});
  await sql(clone,await fs.readFile(path.join(baselines,schema+'.post.sql'),'utf8'));
  await sql(clone,await script(schema==='ordering'?'91_ordering_rollback.sql':'90_payments_rollback.sql'),{schema,approval:'rollback'});
  assert.equal(await sql(clone,'SELECT md5(string_agg(row_to_json(t)::text,chr(124) ORDER BY id)) FROM '+schema+'.'+table+' t;'),sampleBefore[schema]);
  await sql(clone,await script(schema==='ordering'?'01_ordering_expand.sql':'02_payments_expand.sql'),{schema,approval:'expand'});
  report(schema+': reviewed SQL, repeat safely refused, role login/deny, backup/restore and rollback/reapply PASS');
 }
 for(const schema of ['ordering','payment']){
  const db=databases[schema],r=role(schema);assert.equal(await sql(db,"SELECT rolsuper OR rolcreaterole OR rolcreatedb OR rolbypassrls FROM pg_roles WHERE rolname='"+r+"';"),'f');
  assert.equal(await sql(db,"SELECT has_table_privilege('"+r+"','"+schema+".__ef_migrations_history','UPDATE');"),'f');
  const other=schema==='ordering'?'payment':'ordering';await sql(databases[other],'SELECT 1;',{schema,user:r,deny:true});
 }report('Non-owner service roles have no superuser/role/DB creation/history update or cross-service CONNECT');
 const expected=JSON.parse(await fs.readFile(path.join(baselines,'schema.json'),'utf8'));
 for(const schema of Object.keys(databases)){
  const rows=JSON.parse(await sql(databases[schema],"SELECT json_agg(json_build_object('table',c.relname,'name',a.attname,'type',format_type(a.atttypid,a.atttypmod),'nullable',NOT a.attnotnull) ORDER BY c.relname,a.attname) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid WHERE n.nspname='"+schema+"' AND c.relkind='r' AND c.relname<>'__ef_migrations_history' AND a.attnum>0 AND NOT a.attisdropped;"));
  const normalize=x=>x.map(c=>({...c,type:c.type.replaceAll(' ','')})).sort((a,b)=>(a.table+'.'+a.name).localeCompare(b.table+'.'+b.name));assert.deepEqual(normalize(rows),normalize(expected[schema]));
 }report('All service tables/columns/nullability/types match EF relational models');
 const fields=await sql(databases.payment,"SELECT count(*) FROM information_schema.tables WHERE table_schema='payment' AND table_name LIKE 'session_bill%';");assert.equal(fields,'4');
 for(const schema of ['ordering','payment']){
  const db=databases[schema];assert.equal(await sql(db,"SELECT count(*) FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid JOIN pg_namespace n ON n.oid=t.relnamespace JOIN pg_class p ON p.oid=c.confrelid JOIN pg_namespace pn ON pn.oid=p.relnamespace WHERE c.contype='f' AND n.nspname='"+schema+"' AND (t.relname='session_checkouts' OR t.relname LIKE 'session_bill%') AND pn.nspname<>n.nspname;"),'0');
  const names=await sql(db,"SELECT conname FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid JOIN pg_namespace n ON n.oid=t.relnamespace WHERE n.nspname='"+schema+"' AND (t.relname='session_checkouts' OR t.relname LIKE 'session_bill%') ORDER BY conname;");
  for(const name of schema==='ordering'?['pk_session_checkouts','ck_session_checkout_state']:['ck_session_bill_status','ck_session_bill_total','ck_bill_attempt_amount','ck_bill_attempt_status','fk_session_bill_orders_session_bills_bill_id','fk_session_bill_attempts_session_bills_bill_id','fk_session_bill_events_session_bills_bill_id'])assert.ok(names.includes(name));
  const index=await sql(db,"SELECT indexdef FROM pg_indexes WHERE schemaname='"+schema+"' AND (tablename='session_checkouts' OR tablename='table_sessions' OR tablename LIKE 'session_bill%');");
  assert.ok(index.includes(schema==='ordering'?'(tenant_id, bill_id)':'(tenant_id, session_id)'));if(schema==='payment'){assert.ok(index.includes('uq_bill_pending_attempt'));assert.ok(index.includes('PENDING'));}else assert.ok(index.includes('Open'));
 }report('Actual CHECK/PK/own FK and session/Bill/pending uniqueness definitions verified');
 report('Five new tables; no cross-service FK; existing session/bill uniqueness retained');
 if(baristaMode){
  const env={BARISTA_PRINT_TEST_KEY:printerKey};const id=await docker(['run','-d','--name',network+'-printer','--label','aicos.purpose='+purpose,'--network',network,'--network-alias','printer','-e','BARISTA_PRINT_TEST_KEY','--mount','type=bind,src='+tools+',dst=/testtools,readonly','--entrypoint','dotnet','aicos-main/identity:local','/testtools/AICOS.Billing.StackTools.dll','test-printer'],{env});containers.push(id);
 }
 const redis=await create('redis','redis:7-alpine',['--tmpfs','/data:rw']);
 for(const [name,folder,assembly,schema] of services){
  console.log('Preparing TEST '+name);const output=path.join(temporary,name);await publish(folder?'src/Services/'+folder+'/'+assembly:'src/Gateway/'+assembly,output);
  const env={ASPNETCORE_ENVIRONMENT:'Development',Database__MigrateOnStartup:'false',Seed__DemoPlans:'false',Swagger__Enabled:'true',Jwt__SigningKey:jwt,Jwt__Issuer:'aicos-billstack-test',Jwt__Audience:'aicos-billstack-clients',ConnectionStrings__Redis:'redis:6379',SessionBilling__Enabled:'true',SessionBilling__MachineKey:machine,SessionBilling__AllowTestProviders:'true',SessionBilling__TestWebhookSecret:webhook,SessionBilling__DispatcherEnabled:'true',Serilog__MinimumLevel__Default:'Warning',RateLimiting__PermitLimitPerMinute:'10000',RateLimiting__AuthPermitLimitPerMinute:'100'};
  for(const [n,,,s] of services){if(n!=='gateway'){const key={identity:'Identity',menu:'Menu',table:'Table',order:'Order',kitchen:'Kitchen',payment:'Payment',reporting:'Reporting',workforce:'Workforce'}[n];env['Services__'+key+'__BaseUrl']='http://'+n+':8080';env['ReverseProxy__Clusters__'+n+'__Destinations__primary__Address']='http://'+n+':8080';}}
  if(schema)env.ConnectionStrings__Default='Host=postgres;Database='+databases[schema]+';Username='+role(schema)+';Password='+passwords[schema];
  if(baristaMode&&name==='kitchen'){env.BaristaWorkflow__Enabled='true';env.BaristaWorkflow__PrintAgentUrl='http://printer:8080/';env.BaristaWorkflow__PrintAgentKey=printerKey;env.BaristaWorkflow__AllowTestPrinter='true';}
  if(name==='order'){env.KitchenAdmission__CaptureEnabled='true';env.KitchenAdmission__DispatcherEnabled='true';env.KitchenAdmission__ReconciliationEnabled='true';env.KitchenAdmission__MachineKey=admission;}
  if(name==='kitchen'){env.KitchenAdmission__ReceiverEnabled='true';env.KitchenAdmission__MachineKey=admission;}
  const flags=['--mount','type=bind,src='+output+',dst=/app,readonly','--workdir','/app','--entrypoint','dotnet'];if(name==='gateway')flags.push('--publish','127.0.0.1::8080');
  const keys=Object.keys(env).flatMap(k=>['-e',k]);const id=await docker(['run','-d','--name',network+'-'+name,'--label','aicos.purpose='+purpose,'--network',network,'--network-alias',name,...keys,...flags,'aicos-main/'+name+':local',assembly+'.dll'],{env});containers.push(id);serviceIds[name]=id;
 }
 gateway=serviceIds.gateway;const inspection=JSON.parse(await docker(['inspect',gateway]))[0];const binding=inspection.NetworkSettings.Ports['8080/tcp'][0];assert.equal(binding.HostIp,'127.0.0.1');base='http://127.0.0.1:'+binding.HostPort;
 await until(async()=>{const r=await fetch(base+'/api/v1/identity/public/subscription-plans');return r.status===200;},'Gateway Identity');
 await docker(['run','--rm','--network',network,'--mount','type=bind,src='+tools+',dst=/testtools,readonly','--entrypoint','dotnet','aicos-main/identity:local','/testtools/AICOS.Billing.StackTools.dll','health']);report('Eight services live/ready/Swagger; Gateway metadata/live (ready/Swagger N/A by source)');
 const ctx={base,userPassword,webhook,sql,databases,report,until,docker,run,serviceIds,redis,temporary,root,checks};if(baristaMode){await runBaristaFlow(ctx);await sql(databases.kitchen,await fs.readFile(path.join(root,'docs/migrations/barista-workflow/90_rollback.EMPTY_ONLY.sql'),'utf8'),{schema:'kitchen',approval:'rollback',deny:true});report('Nonempty Barista acceptance/print audit rollback refused');}else await runFlow(ctx);
 await sql(databases.payment,await script('90_payments_rollback.sql'),{schema:'payment',approval:'rollback',deny:true});report('Nonempty financial rollback rejected; received money/audit retained');
 for(const name of ['payment','order','kitchen','reporting','workforce'])await docker(['stop',serviceIds[name]]);
 for(const schema of ['ordering','payment']){
  const db=databases[schema],target='aicos_billstack_restore_paid_'+schema;
  const tables=schema==='ordering'?['table_sessions','orders','session_checkouts']:['session_bills','session_bill_orders','session_bill_attempts','session_bill_events'];
  const fingerprint=async(database)=>Promise.all(tables.map(t=>sql(database,'SELECT md5(COALESCE(string_agg(row_to_json(r)::text,chr(124) ORDER BY row_to_json(r)::text),chr(124))) FROM '+schema+'.'+t+' r;')));
  const prior=await fingerprint(db);await docker(['exec',pg,'pg_dump','-U','bill_stack_admin','-Fc','-d',db,'-f','/tmp/'+schema+'-paid.dump']);await sql('postgres','CREATE DATABASE '+target+';');await docker(['exec',pg,'pg_restore','-U','bill_stack_admin','-d',target,'/tmp/'+schema+'-paid.dump']);assert.deepEqual(await fingerprint(target),prior);
 }report('Paid ledger/attempts/audit and Ordering sessions/orders restored exactly into separate copies');
 assert.equal(await snapshot(existing),before);report('All preexisting container IDs/images/start times/status/restart counts unchanged');
 console.log('RESULT '+checks.filter(x=>x.status==='PASS').length+' PASS; '+checks.filter(x=>x.status==='BLOCKED').length+' BLOCKED');
}catch(error){console.log('FAIL '+error.message);checks.push({name:error.message,status:'FAIL'});process.exitCode=1;
 // Container logs stay in ephemeral containers. Do not dump secrets or authorization bodies.
 if(error.detail){let diagnostic=error.detail;for(const value of [adminPassword,userPassword,jwt,machine,admission,webhook,printerKey,...Object.values(passwords)])diagnostic=diagnostic.replaceAll(value,'[redacted]');console.log('Diagnostic: '+diagnostic.replace(/eyJ[A-Za-z0-9_.-]{40,}/g,'[redacted token]').slice(-2500));}
}finally{
 await fs.writeFile(path.join(root,'docs',baristaMode?'BARISTA_STACK_TEST_EVIDENCE.json':'SESSION_BILL_STACK_TEST_EVIDENCE.json'),JSON.stringify({cluster,checks,operatingContainersUnchanged:await snapshot(existing)===before,qr:'Mock only; not bank verification'},null,2)+'\n');
 for(const id of containers.reverse()){const x=JSON.parse(await docker(['inspect',id]))[0];assert.equal(x.Config.Labels['aicos.purpose'],purpose);assert.deepEqual(Object.keys(x.NetworkSettings.Networks),[network]);await docker(['rm','-f',id]);}
 if((await docker(['network','ls','--filter','name=^'+network+'$','-q'])))await docker(['network','rm',network]);
 console.log('Removed only this run test containers/network; no operating volume touched. Temporary build files contain no credentials.');
}
