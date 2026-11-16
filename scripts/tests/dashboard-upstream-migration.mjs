// Offline SQL generation and isolated rehearsal only. Never connects to operating PostgreSQL.
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import cp from 'node:child_process';
import assert from 'node:assert/strict';
const root=process.cwd(), source=path.resolve(process.argv[2]??'dashboard-upstream-export');
const targets=JSON.parse(await fs.readFile(path.join(source,'targets.json'),'utf8'));
assert.deepEqual(targets.map(t=>t.schema),['identity','payment','workforce']);
const folder=path.join(root,'docs/proposals/cashier-dashboard-upstream');
await fs.mkdir(folder,{recursive:true});
const mainCluster='7703616372368646182';
const affected="SELECT 1 FROM identity.tenants t WHERE t.status='Active' AND NOT EXISTS(SELECT 1 FROM identity.subscription_payments p WHERE p.tenant_id=t.id) AND EXISTS(SELECT 1 FROM identity.tenant_subscriptions s WHERE s.tenant_id=t.id AND s.status='Active' AND s.fee>0)";
const manifests=[];
for(const [i,t] of targets.entries()){
 const raw=(await fs.readFile(path.join(source,t.schema+'.up.sql'),'utf8')).replace(/^\uFEFF/,'').replaceAll('\r\n','\n').trim();
 assert.ok(!/\b(DROP\s+TABLE|TRUNCATE|DELETE\s+FROM|CREATE\s+DATABASE|ALTER\s+ROLE|GRANT|REVOKE)\b/i.test(raw));
 if(t.schema!=='workforce')assert.ok(!/\bDROP\b/i.test(raw));
 const history="ARRAY["+t.baseline.map(x=>"'"+x+"'").join(',')+"]::text[]";
 const guard=[
 "IF current_setting('aicos.expected_database',true) IS DISTINCT FROM current_database() OR current_setting('aicos.expected_schema',true) IS DISTINCT FROM '"+t.schema+"' THEN RAISE EXCEPTION 'Wrong target'; END IF;",
 "IF current_setting('aicos.expected_cluster',true) IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system()) THEN RAISE EXCEPTION 'Wrong cluster'; END IF;",
 "IF NOT (current_database()='aicos_"+t.schema+"' AND current_setting('aicos.expected_cluster')='"+mainCluster+"') AND NOT (current_database()='aicos_dashboard_upstream_"+t.schema+"' AND current_setting('aicos.expected_cluster')<>'"+mainCluster+"') THEN RAISE EXCEPTION 'Outside reviewed scope'; END IF;",
 "IF current_setting('aicos.dashboard_upstream_approved',true) IS DISTINCT FROM 'true' THEN RAISE EXCEPTION 'Separate approval required'; END IF;",
 "IF ARRAY(SELECT migration_id::text FROM "+t.schema+".__ef_migrations_history ORDER BY migration_id) IS DISTINCT FROM "+history+" THEN RAISE EXCEPTION 'History mismatch or already applied'; END IF;",
 t.schema==='identity'?"LOCK TABLE identity.tenants,identity.subscription_payments,identity.tenant_subscriptions IN SHARE ROW EXCLUSIVE MODE; IF EXISTS("+affected+") THEN RAISE EXCEPTION 'Tenant login transition requires separate business review'; END IF;":''
 ].filter(Boolean).join('\n');
 const text=['-- PROPOSAL ONLY. Requires fresh backup, maintenance and separate approval.','BEGIN;',"SET LOCAL lock_timeout='3s'; SET LOCAL statement_timeout='60s';",'DO $guard$ BEGIN',guard,'END $guard$;',raw,'COMMIT;',''].join('\n');
 const name='0'+(i+1)+'_'+t.schema+'.PROPOSAL.sql';await fs.writeFile(path.join(folder,name),text);
 manifests.push(crypto.createHash('sha256').update(text).digest('hex')+'  '+name);
}
await fs.writeFile(path.join(folder,'SHA256SUMS.txt'),manifests.join('\n')+'\n');
const purpose='dashboard-upstream-review',network='aicos-upstream-'+crypto.randomBytes(5).toString('hex');
const temp=await fs.mkdtemp(path.join(os.tmpdir(),'aicos-upstream-review-'));
const checks=[],pass=name=>{checks.push({name,status:'PASS'});console.log('PASS '+name);};
function run(args,input,deny=false){const r=cp.spawnSync('docker',args,{input,encoding:'utf8',windowsHide:true,maxBuffer:32*1024*1024});if(deny){assert.notEqual(r.status,0);return r.stdout;}if(r.status!==0)throw Error('Docker '+args[0]+' failed (details suppressed)');return r.stdout.trim();}
const inspect=id=>JSON.parse(run(['inspect',id]))[0];
const ids=run(['ps','-aq']).split(/\r?\n/).filter(Boolean);
const snapshot=()=>JSON.stringify(ids.map(id=>{const c=inspect(id);return[c.Id,c.Image,c.State.Status,c.State.StartedAt,c.RestartCount];}));
const before=snapshot();let pg,cluster;
function guard(){const c=inspect(pg);assert.equal(c.Id,pg);assert.equal(c.Config.Labels['aicos.purpose'],purpose);assert.deepEqual(Object.keys(c.NetworkSettings.Networks),[network]);assert.equal(c.NetworkSettings.Ports['5432/tcp'],null);assert.ok(Object.hasOwn(c.HostConfig.Tmpfs,'/var/lib/postgresql/data'));assert.ok(!c.Mounts.some(m=>m.Type==='volume'));const actual=run(['exec',pg,'psql','-XqAt','-U','review_admin','-d','postgres','-c','SELECT system_identifier::text FROM pg_control_system()']);assert.match(actual,/^\d+$/);assert.notEqual(actual,mainCluster);if(cluster)assert.equal(actual,cluster);else cluster=actual;}
function sql(db,text,deny=false){assert.ok(db==='postgres'||/^aicos_dashboard_upstream_(identity|payment|workforce)(?:_restore)?$/.test(db));guard();return run(['exec','-i',pg,'psql','-XqAt','-v','ON_ERROR_STOP=1','-U','review_admin','-d',db],text,deny);}
const settings=(db,s)=>"SET aicos.expected_database='"+db+"'; SET aicos.expected_schema='"+s+"'; SET aicos.expected_cluster='"+cluster+"'; SET aicos.dashboard_upstream_approved='true';\n";
try{
 run(['network','create','--internal',network]);
 const password=crypto.randomBytes(32).toString('base64url');
 const started=cp.spawnSync('docker',['run','-d','--network',network,'--label','aicos.purpose='+purpose,'--tmpfs','/var/lib/postgresql/data','-e','POSTGRES_USER=review_admin','-e','POSTGRES_PASSWORD','postgres:16-alpine'],{encoding:'utf8',windowsHide:true,env:{...process.env,POSTGRES_PASSWORD:password}});assert.equal(started.status,0);pg=started.stdout.trim();
 let ready=false;for(let i=0;i<60;i++){const r=cp.spawnSync('docker',['exec',pg,'pg_isready','-U','review_admin'],{encoding:'utf8',windowsHide:true});if(r.status===0){ready=true;break;}await new Promise(r=>setTimeout(r,500));}assert.ok(ready);guard();pass('Fresh internal tmpfs PostgreSQL, no host port; different cluster; no operating database connection');
 for(const [i,t] of targets.entries()){
  const db='aicos_dashboard_upstream_'+t.schema;sql('postgres','CREATE DATABASE '+db+';');
  sql(db,await fs.readFile(path.join(source,t.schema+'.baseline.sql'),'utf8'));
  const body=await fs.readFile(path.join(folder,'0'+(i+1)+'_'+t.schema+'.PROPOSAL.sql'),'utf8');
  sql(db,body,true);sql(db,settings(db,t.schema).replace(cluster,'0')+body,true);sql(db,settings(db,'wrong')+body,true);
  sql(db,settings(db,t.schema)+body);sql(db,settings(db,t.schema)+body,true);
  const columns=JSON.parse(sql(db,"SELECT json_agg(json_build_object('table',c.relname,'name',a.attname,'type',format_type(a.atttypid,a.atttypmod),'nullable',NOT a.attnotnull)) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid WHERE n.nspname='"+t.schema+"' AND c.relkind='r' AND c.relname<>'__ef_migrations_history' AND a.attnum>0 AND NOT a.attisdropped;"));
  const sort=xs=>xs.map(x=>JSON.stringify(x)).sort();assert.deepEqual(sort(columns),sort(t.columns));
  assert.equal(sql(db,'SELECT migration_id FROM '+t.schema+'.__ef_migrations_history ORDER BY migration_id DESC LIMIT 1;'),t.to);
  const role=t.schema+'_review';sql(db,'CREATE ROLE '+role+' NOLOGIN; GRANT USAGE ON SCHEMA '+t.schema+' TO '+role+'; GRANT SELECT ON ALL TABLES IN SCHEMA '+t.schema+' TO '+role+';');
  for(const table of new Set(columns.map(c=>c.table)))assert.equal(sql(db,'SET ROLE '+role+'; SELECT count(*) FROM '+t.schema+'.'+table+';'),'0');
  pass(t.schema+': exact EF columns/history, target/approval/repeat refusal and empty-data SELECT role access');
  const dump=cp.spawnSync('docker',['exec',pg,'pg_dump','-U','review_admin','-d',db],{windowsHide:true,maxBuffer:32*1024*1024});assert.equal(dump.status,0);
  const restored=db+'_restore';sql('postgres','CREATE DATABASE '+restored+';');sql(restored,dump.stdout);
  assert.equal(sql(restored,'SELECT count(*) FROM '+t.schema+'.__ef_migrations_history;'),String(t.baseline.length+t.pending.length));
  const load=dbName=>JSON.parse(sql(dbName,"SELECT json_agg(json_build_object('table',c.relname,'name',k.conname,'kind',k.contype,'definition',pg_get_constraintdef(k.oid),'validated',k.convalidated) ORDER BY c.relname,k.conname) FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid WHERE k.connamespace='"+t.schema+"'::regnamespace;"));
  const canonical=rows=>rows.map(row=>row.kind!=='c'?row:{...row,definition:sql(db,'CREATE TEMP TABLE compare_constraint (LIKE '+t.schema+'.'+row.table+'); ALTER TABLE compare_constraint ADD CONSTRAINT compared '+row.definition+"; SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='compare_constraint'::regclass AND conname='compared';")});
  assert.deepEqual(canonical(load(db)),canonical(load(restored)));
  const indexes="SELECT count(*) FROM pg_indexes WHERE schemaname='"+t.schema+"';";assert.equal(sql(db,indexes),sql(restored,indexes));
  if(t.schema==='workforce')assert.equal(sql(db,"SELECT indexdef LIKE '%UNIQUE%WHERE (deleted_at_utc IS NULL)' FROM pg_indexes WHERE schemaname='workforce' AND indexname='ix_work_shifts_branch_id_name';"),'t');
  pass(t.schema+': separate dump/restore clone retains migration history and PK/FK/CHECK definitions');
 }
 assert.equal(snapshot(),before);pass('All existing containers unchanged; no migration or grant on operating cluster');
 await fs.writeFile(path.join(root,'docs/DASHBOARD_UPSTREAM_REHEARSAL_RESULT.json'),JSON.stringify({cluster,mainConnections:0,checks,notTested:['Operating Identity/Workforce full backup and restore','Nonzero existing tenant status transition (guard refuses)','Operating service-role ACL changes','Rollback after new business writes'],rollback:'Keep services stopped; restore approved backup into a separate clone; repair forward after business writes'},null,2)+'\n');
}finally{if(pg)run(['rm','-f',pg]);run(['network','rm',network]);await fs.rm(temp,{recursive:true,force:true});}