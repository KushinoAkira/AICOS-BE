// Rehearsal only: operating connections are always READ ONLY; DDL targets fresh tmpfs PostgreSQL.
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import cp from 'node:child_process';
import assert from 'node:assert/strict';
import {preparePackage,mainCluster,databases} from '../lib/local-dashboard-sql.mjs';

const root=process.cwd(),exports=path.resolve(process.argv[2]??'');
const targets=JSON.parse(await fs.readFile(path.join(exports,'targets.json'),'utf8'));
assert.deepEqual(targets.map(t=>t.schema),Object.keys(databases));
const folder=path.join(root,'docs/proposals/cashier-dashboard-local');
const temporary=await fs.mkdtemp(path.join(os.tmpdir(),'aicos-dashboard-schema-'));
const purpose='dashboard-schema-review',network='aicos-dashboard-review-'+crypto.randomBytes(5).toString('hex');
const result={date:'2026-11-16',mainCluster,mainConnectionsReadOnly:true,mainDataCopied:false,checks:[],targets:[]};
const pass=name=>{result.checks.push({name,status:'PASS'});console.log('PASS '+name);};
const run=(args,{input,env={},allowFailure=false}={})=>{
 const r=cp.spawnSync('docker',args,{input,env:{...process.env,...env},encoding:'utf8',maxBuffer:32*1024*1024,windowsHide:true});
 if(!allowFailure&&r.status!==0)throw Object.assign(Error('Docker '+args[0]+' failed; secret output suppressed'),{diagnostic:r.stderr?.split('\n').find(l=>l.startsWith('ERROR:'))});
 return r;
};
const docker=(args,options)=>run(args,options).stdout.trim();
const inspect=id=>JSON.parse(docker(['inspect',id]))[0];
const existing=docker(['ps','-aq']).split(/\r?\n/).filter(Boolean);
const snapshot=()=>JSON.stringify(existing.map(id=>{const c=inspect(id);return[c.Id,c.Image,c.State.Status,c.State.StartedAt,c.RestartCount];}));
const originalContainers=snapshot();
const main=inspect('aicos-main-postgres');assert.ok(main.Id.startsWith('c014190eed2d'));
assert.equal(main.Config.Labels['com.docker.compose.project'],'aicos-main');
const admin=main.Config.Env.find(e=>e.startsWith('POSTGRES_USER='))?.slice('POSTGRES_USER='.length)??'postgres';
function mainSql(db,body){
 assert.ok(Object.values(databases).includes(db));
 assert.equal(inspect(main.Id).Id,main.Id);
 const guard="DO $$ BEGIN IF current_database()<>'"+db+"' OR (SELECT system_identifier::text FROM pg_control_system())<>'"+mainCluster+"' THEN RAISE EXCEPTION 'Main identity differs'; END IF; END $$;";
 return docker(['exec','-i',main.Id,'psql','-XqAt','-v','ON_ERROR_STOP=1','-U',admin,'-d',db],{input:'BEGIN READ ONLY;\n'+guard+'\n'+body+'\nCOMMIT;'});
}
const columnsSql=s=>"SELECT json_agg(json_build_object('table',c.relname,'name',a.attname,'type',format_type(a.atttypid,a.atttypmod),'nullable',NOT a.attnotnull) ORDER BY c.relname,a.attnum) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid WHERE n.nspname='"+s+"' AND c.relkind='r' AND c.relname<>'__ef_migrations_history' AND a.attnum>0 AND NOT a.attisdropped;";
const baseline={},schemas={},mainFingerprints={};
function fingerprints(sql,schema,columns){
 const tables=[...new Set(columns.map(c=>c.table))];
 return Object.fromEntries(tables.map(table=>{
  const fields=columns.filter(c=>c.table===table).map(c=>'"'+c.name+'"').join(',');
  return[table,JSON.parse(sql("SELECT json_build_object('rows',count(*),'hash',md5(COALESCE(string_agg(row_to_json(t)::text,chr(10) ORDER BY row_to_json(t)::text),''))) FROM (SELECT "+fields+' FROM '+schema+'."'+table+'") t;'))];
 }));
}
let pg,cluster,networkId;
function scratchGuard(){
 const c=inspect(pg);assert.equal(c.Id,pg);assert.equal(c.Config.Labels['aicos.purpose'],purpose);
 assert.deepEqual(Object.keys(c.NetworkSettings.Networks),[network]);assert.equal(c.NetworkSettings.Ports['5432/tcp'],null);
 assert.ok(c.HostConfig.Tmpfs['/var/lib/postgresql/data']);assert.ok(!c.Mounts.some(m=>m.Type==='volume'));
 const actual=docker(['exec',pg,'psql','-XqAt','-U','review_admin','-d','postgres','-c','SELECT system_identifier::text FROM pg_control_system();']);
 assert.match(actual,/^\d+$/);assert.notEqual(actual,mainCluster);if(cluster)assert.equal(actual,cluster);else cluster=actual;
}
function sql(db,text,{schema,action,user='review_admin',password,deny=false}={}){
 assert.ok(db==='postgres'||/^aicos_dashboard_(test|restore)_(ordering|kitchen|payment|reporting)$/.test(db));scratchGuard();
 const settings=schema?"SET aicos.expected_database='"+db+"'; SET aicos.expected_schema='"+schema+"'; SET aicos.expected_cluster='"+cluster+"'; "+(action?"SET aicos.dashboard_"+action+"_approved='true'; ":''):'';
 const args=['exec','-i'],env={};if(password){args.push('-e','PGPASSWORD');env.PGPASSWORD=password;}
 args.push(pg,'psql','-XqAt','-v','ON_ERROR_STOP=1','-U',user,'-d',db);if(password)args.push('-h','127.0.0.1');
 const r=run(args,{env,input:settings+text,allowFailure:deny});
 if(deny){assert.notEqual(r.status,0,'Expected refusal before mutation');return;}
 return r.stdout.trim();
}
const upName=s=>({ordering:'01_ordering',kitchen:'02_kitchen',payment:'03_payments',reporting:'04_reporting'}[s])+'_expand.sql';
const downName=(s,i)=>'9'+i+'_'+s+'_rollback.UNUSED_ONLY.sql';
const grantsName=(s,i)=>'1'+i+'_'+s+'_grants.PROPOSAL.sql';
const seed={
 ordering:"INSERT INTO ordering.table_sessions(id,tenant_id,branch_id,table_id,table_code,status,opened_at_utc,last_activity_at_utc,created_at_utc) VALUES(gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),'REVIEW','Closed',now(),now(),now()); INSERT INTO ordering.orders(id,tenant_id,branch_id,order_code,type,status,payment_status,discount_amount,refunded_amount,refund_status,is_printed,printed_count,created_at_utc,remake_count) VALUES(gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),'REVIEW-ONLY','Takeaway','Completed','Unpaid',0,0,'None',false,0,now(),0);",
 kitchen:"INSERT INTO kitchen.early_payments(tenant_id,order_id,paid_at_utc) VALUES(gen_random_uuid(),gen_random_uuid(),now());",
 payment:"INSERT INTO payment.payment_providers(id,code,display_name,mode,is_enabled,created_at_utc) VALUES(gen_random_uuid(),'REVIEW_ONLY','Synthetic backup fixture','Mock',false,now());",
 reporting:"INSERT INTO reporting.order_facts(id,tenant_id,branch_id,order_type,total_amount,paid_amount,refunded_amount,created_at_utc) VALUES(gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),'Takeaway',35000,0,0,now());"
};
try{
 for(const target of targets){
  const s=target.schema,db=databases[s];
  baseline[s]={columns:JSON.parse(mainSql(db,columnsSql(s))),history:mainSql(db,'SELECT migration_id FROM '+s+'.__ef_migrations_history ORDER BY migration_id;').split('\n')};
  mainFingerprints[s]=fingerprints(text=>mainSql(db,text),s,baseline[s].columns);
  const role=s==='ordering'?'ordering_svc':s+'_svc';
  const ownership=JSON.parse(mainSql(db,"SELECT json_build_object('role',r.rolname,'superuser',r.rolsuper,'createRole',r.rolcreaterole,'createDb',r.rolcreatedb,'bypassRls',r.rolbypassrls,'schemaOwner',pg_get_userbyid(n.nspowner),'databaseOwner',pg_get_userbyid(d.datdba)) FROM pg_roles r JOIN pg_namespace n ON n.nspname='"+s+"' JOIN pg_database d ON d.datname=current_database() WHERE r.rolname='"+role+"';"));
  schemas[s]=docker(['exec','-e','PGOPTIONS=-c default_transaction_read_only=on',main.Id,'pg_dump','-U',admin,'-d',db,'--schema='+s,'--schema-only','--no-owner','--no-privileges']);
  await fs.writeFile(path.join(temporary,s+'.schema-only.sql'),schemas[s]);
  result.targets.push({database:db,schema:s,from:target.from,to:target.to,pending:target.pending,role:ownership,mainOriginalFingerprints:mainFingerprints[s],schemaOnlyBackupSha256:crypto.createHash('sha256').update(schemas[s]).digest('hex')});
 }
 pass('Exact Docker main container/cluster and four READ ONLY catalog audits; schema-only export has no business rows or secrets');
 const files=await preparePackage(folder,exports,targets,baseline);
 result.sqlChecksums=(await fs.readFile(path.join(folder,'SHA256SUMS.txt'),'utf8')).trim().split('\n');
 result.targets.forEach((row,i)=>{row.newTables=targets[i].newTables;row.newColumns=targets[i].newColumns.map(c=>c.table+'.'+c.name);});
 pass('Fifteen new tables/five columns only; existing types/nullability match; ten pending migrations and no V2');
 networkId=docker(['network','create','--internal','--label','aicos.purpose='+purpose,network]);
 const password=crypto.randomBytes(32).toString('hex');
 pg=docker(['run','-d','--name',network+'-postgres','--label','aicos.purpose='+purpose,'--network',network,'--tmpfs','/var/lib/postgresql/data:rw','-e','POSTGRES_USER=review_admin','-e','POSTGRES_DB=postgres','-e','POSTGRES_PASSWORD','postgres:16-alpine'],{env:{POSTGRES_PASSWORD:password}});
 let ready=false;for(let i=0;i<60;i++){const r=run(['exec',pg,'pg_isready','-U','review_admin'],{allowFailure:true});if(r.status===0){try{scratchGuard();ready=true;break;}catch{}}await new Promise(r=>setTimeout(r,500));}
 assert.ok(ready,'Scratch PostgreSQL readiness');result.testCluster=cluster;
 pass('Fresh internal-only/no-host-port/tmpfs test cluster pinned before DDL');
 for(const [i,target] of targets.entries()){
  const s=target.schema,db='aicos_dashboard_test_'+s,clone='aicos_dashboard_restore_'+s;
  const role=s==='ordering'?'ordering_svc':s+'_svc',rolePassword=crypto.randomBytes(32).toString('hex');
  sql('postgres','CREATE DATABASE '+db+'; CREATE ROLE '+role+" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD '"+rolePassword+"'; REVOKE ALL ON DATABASE "+db+' FROM PUBLIC; GRANT CONNECT ON DATABASE '+db+' TO '+role+';');
  sql(db,schemas[s]);sql(db,baseline[s].history.map(id=>"INSERT INTO "+s+".__ef_migrations_history(migration_id,product_version) VALUES('"+id+"','8.0.31');").join('\n'));
  sql(db,seed[s]);const before=fingerprints(text=>sql(db,text),s,baseline[s].columns);
  sql(db,files['00_preflight.READ_ONLY.sql'],{schema:s});
  const up=files[upName(s)],down=files[downName(s,i)],grants=files[grantsName(s,i)];
  sql(db,"SET aicos.expected_cluster='wrong'; "+up,{schema:s,action:'expand',deny:true});
  sql(db,"SET aicos.expected_database='aicos_db'; "+up,{schema:s,action:'expand',deny:true});
  sql(db,up,{schema:s,deny:true});
  sql(db,"UPDATE "+s+".__ef_migrations_history SET migration_id='00000000000000_REVIEW' WHERE migration_id='"+target.baseline[0]+"';");
  sql(db,up,{schema:s,action:'expand',deny:true});
  sql(db,"UPDATE "+s+".__ef_migrations_history SET migration_id='"+target.baseline[0]+"' WHERE migration_id='00000000000000_REVIEW';");
  sql(db,up,{schema:s,action:'expand'});sql(db,up,{schema:s,action:'expand',deny:true});
  assert.deepEqual(fingerprints(text=>sql(db,text),s,baseline[s].columns),before);
  assert.deepEqual(JSON.parse(sql(db,columnsSql(s))).sort((a,b)=>(a.table+a.name).localeCompare(b.table+b.name)),target.columns.sort((a,b)=>(a.table+a.name).localeCompare(b.table+b.name)));
  assert.equal(sql(db,"SELECT count(*) FROM pg_constraint c JOIN pg_namespace n ON n.oid=c.connamespace WHERE n.nspname='"+s+"' AND NOT c.convalidated;"),'0');
  assert.equal(sql(db,"SELECT count(*) FROM pg_constraint c JOIN pg_class a ON a.oid=c.conrelid JOIN pg_namespace an ON an.oid=a.relnamespace JOIN pg_class b ON b.oid=c.confrelid JOIN pg_namespace bn ON bn.oid=b.relnamespace WHERE c.contype='f' AND an.nspname='"+s+"' AND bn.nspname<>an.nspname;"),'0');
  for(const match of up.matchAll(/CREATE (?:UNIQUE )?INDEX (\w+)/g))assert.equal(sql(db,"SELECT count(*) FROM pg_indexes WHERE schemaname='"+s+"' AND indexname='"+match[1]+"';"),'1');
  sql(db,grants,{schema:s,action:'grants'});
  assert.equal(sql(db,'SELECT current_user;',{schema:s,user:role,password:rolePassword}),role);
  for(const table of target.newTables){
   assert.equal(sql(db,"SELECT has_table_privilege(current_user,'"+s+'.'+table+"','SELECT') AND has_table_privilege(current_user,'"+s+'.'+table+"','INSERT') AND NOT has_table_privilege(current_user,'"+s+'.'+table+"','DELETE') AND NOT has_table_privilege(current_user,'"+s+'.'+table+"','TRUNCATE');",{schema:s,user:role,password:rolePassword}),'t');
   sql(db,'DELETE FROM '+s+'.'+table+' WHERE false;',{schema:s,user:role,password:rolePassword,deny:true});
  }
  for(const match of grants.matchAll(/GRANT UPDATE\(([^)]+)\) ON ([\w.]+) TO/g)){
   const column=match[1].split(',')[0];
   sql(db,'UPDATE '+match[2]+' SET '+column+'='+column+' WHERE false;',{schema:s,user:role,password:rolePassword});
  }
  const immutable={ordering:['kitchen_admission_outbox','creation_payload'],payment:['session_bills','total'],reporting:['collection_facts','payload']}[s];
  if(immutable)sql(db,'UPDATE '+s+'.'+immutable[0]+' SET '+immutable[1]+'='+immutable[1]+' WHERE false;',{schema:s,user:role,password:rolePassword,deny:true});
  pass(s+': guarded additive SQL, repeat/wrong-target refusal, complete columns/index/FK/CHECK and real service-role ACL PASS; legacy fixtures preserved');
  docker(['exec',pg,'pg_dump','-U','review_admin','-d',db,'-Fc','-f','/tmp/'+s+'.fixture.dump']);
  sql('postgres','CREATE DATABASE '+clone+';');docker(['exec',pg,'pg_restore','-U','review_admin','-d',clone,'--no-owner','--no-acl','/tmp/'+s+'.fixture.dump']);
  assert.deepEqual(fingerprints(text=>sql(clone,text),s,baseline[s].columns),before);
  sql(clone,down,{schema:s,action:'rollback'});
  assert.deepEqual(fingerprints(text=>sql(clone,text),s,baseline[s].columns),before);
  assert.deepEqual(JSON.parse(sql(clone,columnsSql(s))),baseline[s].columns);
  sql(clone,up,{schema:s,action:'expand'});
  pass(s+': synthetic data backup/restore, unused-only rollback and reapply on separate clone PASS');
  const used={ordering:"INSERT INTO ordering.kitchen_admission_reconcile_state(consumer_key,cycle_id,cycle_started_at_utc) VALUES('REVIEW',gen_random_uuid(),now());",
   payment:"INSERT INTO payment.cashier_shifts(id,tenant_id,branch_id,cashier_id,open_key,opening_cash,opened_at_utc,version) VALUES(gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),'REVIEW',0,now(),0);",
   reporting:"INSERT INTO reporting.collection_facts(id,tenant_id,branch_id,method,amount,paid_at_utc,payload) VALUES(gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),'Cash',35000,now(),'{}');",
   kitchen:null}[s];
  if(used){sql(db,used);sql(db,down,{schema:s,action:'rollback',deny:true});}
  else{
   // The restored main schema has no business rows; a synthetic ticket exercises the routing rollback guard.
   const raw=JSON.parse(sql(db,"SELECT json_agg(json_build_object('name',a.attname,'type',format_type(a.atttypid,a.atttypmod))) FROM pg_attribute a WHERE a.attrelid='kitchen.kitchen_tickets'::regclass AND a.attnum>0 AND NOT a.attisdropped AND a.attnotnull;"));
   const value=c=>c.name==='table_transfer_version'?'1':c.type==='uuid'?'gen_random_uuid()':c.type.includes('timestamp')?'now()':c.type==='boolean'?'false':c.type.startsWith('character')?(c.name==='status'?"'Pending'":c.name==='fulfillment_mode'?"'WholeOrder'":"'REVIEW'"):'0';
   sql(db,'INSERT INTO kitchen.kitchen_tickets('+raw.map(c=>c.name).join(',')+') VALUES('+raw.map(value).join(',')+');');
   sql(db,down,{schema:s,action:'rollback',deny:true});
  }
  pass(s+': rollback refuses new data/history rather than deleting it');
 }
 assert.equal(snapshot(),originalContainers);
 for(const t of targets)assert.deepEqual(fingerprints(text=>mainSql(databases[t.schema],text),t.schema,baseline[t.schema].columns),mainFingerprints[t.schema]);
 result.preexistingContainersUnchanged=true;result.mainOriginalRowsUnchanged=true;
 pass('All preexisting containers and every original-column main table fingerprint/count unchanged');
}catch(error){
 result.checks.push({name:error.diagnostic??error.message,status:'FAIL'});console.log('FAIL '+(error.diagnostic??error.message));process.exitCode=1;
}finally{
 if(pg){const c=inspect(pg);assert.equal(c.Id,pg);assert.equal(c.Config.Labels['aicos.purpose'],purpose);assert.deepEqual(Object.keys(c.NetworkSettings.Networks),[network]);docker(['rm','-f',pg]);}
 if(networkId){const n=JSON.parse(docker(['network','inspect',networkId]))[0];assert.equal(n.Labels['aicos.purpose'],purpose);assert.equal(Object.keys(n.Containers).length,0);docker(['network','rm',networkId]);}
 await fs.mkdir(folder,{recursive:true});await fs.writeFile(path.join(folder,'REHEARSAL_RESULT.json'),JSON.stringify(result,null,2)+'\n');
 console.log('RESULT '+result.checks.filter(x=>x.status==='PASS').length+' PASS; '+result.checks.filter(x=>x.status==='FAIL').length+' FAIL. Only owned scratch resources removed.');
}
