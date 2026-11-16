// Full backup restore verification only. Never connects to the operating PostgreSQL server or runs migrations.
import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import cp from 'node:child_process';
import assert from 'node:assert/strict';
const mainCluster='7703616372368646182';
const root=path.resolve(process.env.LOCALAPPDATA,'AICOS/backups')+path.sep;
const backup=path.resolve(process.argv[2]??'');
assert.ok(backup.startsWith(root)&&path.basename(backup).startsWith('dashboard-'),'Private backup directory required');
assert.ok(!(await fs.lstat(backup)).isSymbolicLink(),'Refusing backup symlink');
const manifest=JSON.parse(await fs.readFile(path.join(backup,'MANIFEST.json'),'utf8'));
assert.equal(manifest.Cluster,mainCluster);assert.ok(manifest.Container.startsWith('c014190eed2d'));
const schemas={aicos_ordering:'ordering',aicos_kitchen:'kitchen',aicos_payment:'payment',aicos_reporting:'reporting'};
const includeRuntimeCompatibility=manifest.Databases.length===8;
const includeUpstream=manifest.Databases.length===6||includeRuntimeCompatibility;
if(includeUpstream)Object.assign(schemas,{aicos_identity:'identity',aicos_workforce:'workforce'});
if(includeRuntimeCompatibility)Object.assign(schemas,{aicos_menu:'menu',aicos_customer:'customer'});
assert.deepEqual(manifest.Databases.map(d=>d.Database),Object.keys(schemas));
const purpose='dashboard-full-restore',network='aicos-dashboard-full-'+crypto.randomBytes(5).toString('hex');
const result={date:'2026-11-16',sourceCluster:mainCluster,backupFilesVerified:false,checks:[],databases:[],operatingConnectionUsed:false,migrationApplied:false};
const pass=name=>{result.checks.push({name,status:'PASS'});console.log('PASS '+name);};
function run(args,{input,env={},deny=false}={}){
 const r=cp.spawnSync('docker',args,{input,env:{...process.env,...env},encoding:'utf8',maxBuffer:32*1024*1024,windowsHide:true});
 if(!deny&&r.status!==0)throw Error('Docker command failed; database/backup output suppressed');
 return r;
}
const docker=(args,options)=>run(args,options).stdout.trim();
const inspect=id=>JSON.parse(docker(['inspect',id]))[0];
const existing=docker(['ps','-aq']).split(/\r?\n/).filter(Boolean);
const snapshot=()=>JSON.stringify(existing.map(id=>{const c=inspect(id);return[c.Id,c.Image,c.State.Status,c.State.StartedAt,c.RestartCount];}));
const before=snapshot();
const targets=new Set(Object.values(schemas).map(s=>'aicos_dashboard_full_'+s));
let pg,cluster,networkId,stage="backup-integrity";
function guard(){
 const c=inspect(pg);assert.equal(c.Id,pg);assert.equal(c.Config.Labels['aicos.purpose'],purpose);
 assert.deepEqual(Object.keys(c.NetworkSettings.Networks),[network]);assert.equal(c.NetworkSettings.Ports['5432/tcp'],null);
 assert.ok(c.HostConfig.Tmpfs['/var/lib/postgresql/data']);assert.ok(!c.Mounts.some(m=>m.Type==='volume'));
 const binds=c.Mounts.filter(m=>m.Type==='bind');assert.equal(binds.length,1);assert.equal(binds[0].Destination,'/backup');assert.equal(binds[0].RW,false);
 const n=JSON.parse(docker(['network','inspect',networkId]))[0];assert.equal(n.Internal,true);
 const actual=docker(['exec',pg,'psql','-XqAt','-U','restore_admin','-d','postgres','-c','SELECT system_identifier::text FROM pg_control_system();']);
 assert.match(actual,/^\d+$/);assert.notEqual(actual,mainCluster);if(cluster)assert.equal(actual,cluster);else cluster=actual;
}
function sql(db,text,readOnly=true){
 assert.ok(db==='postgres'||targets.has(db));guard();
 return docker(['exec','-i',pg,'psql','-XqAt','-v','ON_ERROR_STOP=1','-U','restore_admin','-d',db],{input:(readOnly?"BEGIN READ ONLY; SET LOCAL timezone='UTC'; ":'')+text+(readOnly?' COMMIT;':'')});
}
const ordered=x=>[...x].sort((a,b)=>(a.table+'.'+a.name).localeCompare(b.table+'.'+b.name));
const identifier=name=>{assert.match(name,/^[a-z_][a-z0-9_]*$/);return '"'+name+'"';};
try{
 for(const entry of manifest.Databases){
  assert.equal(entry.Schema,schemas[entry.Database]);assert.equal(entry.File,entry.Database+'.dump');
  const file=path.join(backup,entry.File);assert.ok(!(await fs.lstat(file)).isSymbolicLink());
  const data=await fs.readFile(file);assert.equal(data.length,entry.Bytes);assert.equal(data.subarray(0,5).toString(),'PGDMP');
  assert.equal(crypto.createHash('sha256').update(data).digest('hex'),entry.SHA256);assert.ok(entry.Snapshot?.Columns.length>0);
 }result.backupFilesVerified=true;pass(manifest.Databases.length+' private PGDMP backups match manifest size/SHA256; no data or credentials printed');
 networkId=docker(['network','create','--internal','--label','aicos.purpose='+purpose,network]);
 const password=crypto.randomBytes(32).toString('hex');
 pg=docker(['run','-d','--name',network+'-postgres','--network',network,'--label','aicos.purpose='+purpose,'--tmpfs','/var/lib/postgresql/data:rw','--mount','type=bind,source='+backup+',target=/backup,readonly','-e','POSTGRES_USER=restore_admin','-e','POSTGRES_DB=postgres','-e','POSTGRES_PASSWORD','postgres:16-alpine'],{env:{POSTGRES_PASSWORD:password}});
 let ready=false;
 for(let i=0;i<60;i++){const probe=run(['exec',pg,'pg_isready','-U','restore_admin'],{deny:true});if(probe.status===0){try{guard();ready=true;break;}catch{}}await new Promise(r=>setTimeout(r,500));}
 assert.ok(ready,'Restore PostgreSQL readiness');result.testCluster=cluster;
 pass('Fresh internal-only/tmpfs/no-host-port cluster differs from main; backup bind is read-only');
 for(const s of Object.values(schemas))sql('postgres','CREATE ROLE '+s+'_svc NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;',false);
 for(const entry of manifest.Databases){
  const s=entry.Schema,db='aicos_dashboard_full_'+s;
  sql('postgres','CREATE DATABASE '+db+';',false);guard();
  stage=s+' table-inventory';
  const toc=docker(['exec',pg,'pg_restore','--list','/backup/'+entry.File]);
  const dumpedTables=[...toc.matchAll(/^\d+;\s+\d+\s+\d+\s+TABLE\s+(?!DATA\b)(\S+)\s+(\S+)\s+/gm)].map(m=>m[1]+'.'+m[2]).sort();
  assert.deepEqual(dumpedTables,Object.keys(entry.Snapshot.Fingerprints).map(t=>s+'.'+t).sort(),'Uninventoried tables in full backup');
  stage=s+' restore';
  docker(['exec',pg,'pg_restore','-U','restore_admin','-d',db,'--exit-on-error','--no-owner','--no-acl','/backup/'+entry.File]);
  stage=s+' schema-comparison';
  const columns=JSON.parse(sql(db,"SELECT json_agg(json_build_object('table',c.relname,'name',a.attname,'type',format_type(a.atttypid,a.atttypmod),'nullable',NOT a.attnotnull) ORDER BY c.relname,a.attnum) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped WHERE n.nspname='"+s+"' AND c.relkind='r';"));
  const constraints=JSON.parse(sql(db,"SELECT json_agg(json_build_object('table',c.relname,'name',k.conname,'definition',pg_get_constraintdef(k.oid),'validated',k.convalidated) ORDER BY c.relname,k.conname) FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='"+s+"';"));
  const indexes=JSON.parse(sql(db,"SELECT json_agg(json_build_object('table',tablename,'name',indexname,'definition',indexdef) ORDER BY tablename,indexname) FROM pg_indexes WHERE schemaname='"+s+"';"));
  for(const [label,actual,expected] of [['columns',columns,entry.Snapshot.Columns],['constraints',constraints,entry.Snapshot.Constraints],['indexes',indexes,entry.Snapshot.Indexes]]){
   stage=s+' '+label;const a=ordered(actual),b=ordered(expected);
   if(label==='constraints')for(let i=0;i<a.length;i++){
    if(a[i].table===b[i]?.table&&a[i].name===b[i]?.name&&a[i].definition!==b[i].definition&&a[i].definition.startsWith('CHECK (')&&b[i].definition.startsWith('CHECK (')){
     const canonical=definition=>{
      assert.ok(!definition.includes(';'),'Unexpected CHECK statement separator');
      return sql(db,'CREATE TEMP TABLE restore_check_compare (LIKE '+identifier(s)+'.'+identifier(a[i].table)+'); ALTER TABLE restore_check_compare ADD CONSTRAINT restored_check '+definition+"; SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='restore_check_compare'::regclass AND conname='restored_check';",false);
     };
     a[i]={...a[i],definition:canonical(a[i].definition)};b[i]={...b[i],definition:canonical(b[i].definition)};
    }
   }
   if(label==='indexes')for(let i=0;i<a.length;i++){
    if(a[i].table===b[i]?.table&&a[i].name===b[i]?.name&&a[i].definition!==b[i].definition){
     const canonical=definition=>{
      assert.ok(!definition.includes(';'),'Unexpected index separator');
      const prefix=/^CREATE (UNIQUE )?INDEX \w+ ON \w+\.\w+ /;assert.ok(prefix.test(definition),'Unexpected index header');
      const ddl=definition.replace(prefix,'CREATE $1INDEX restore_index_canonical ON restore_index_table ');
      return sql(db,'CREATE TEMP TABLE restore_index_table (LIKE '+identifier(s)+'.'+identifier(a[i].table)+'); '+ddl+"; SELECT pg_get_indexdef('restore_index_canonical'::regclass);",false).replace(/pg_temp_\d+\./g,'pg_temp.');
     };
     a[i]={...a[i],definition:canonical(a[i].definition)};b[i]={...b[i],definition:canonical(b[i].definition)};
    }
   }
   if(JSON.stringify(a)!==JSON.stringify(b)){
    const position=a.findIndex((row,i)=>JSON.stringify(row)!==JSON.stringify(b[i]));
    result.schemaDiagnostic={label,actualCount:a.length,expectedCount:b.length,actual:a[position],expected:b[position]};
   }
   assert.deepEqual(a,b);
  }
  stage=s+' row-fingerprints';
  let rows=0;
  for(const [table,expected] of Object.entries(entry.Snapshot.Fingerprints)){
   const actual=JSON.parse(sql(db,"SELECT json_build_object('rows',count(*),'hash',md5(COALESCE(string_agg(row_to_json(t)::text,chr(10) ORDER BY row_to_json(t)::text COLLATE \"C\"),''))) FROM "+identifier(s)+'.'+identifier(table)+' t;'));
   assert.deepEqual(actual,expected);rows+=actual.rows;
  }
  result.databases.push({database:entry.Database,tables:Object.keys(entry.Snapshot.Fingerprints).length,columns:columns.length,rows,columnsConstraintsIndexesMatch:true,allRowCountsAndFingerprintsMatch:true});
  pass(entry.Database+': full data, columns, PK/FK/CHECK/indexes and migration history restored exactly');
 }
 assert.equal(snapshot(),before);result.preexistingContainersUnchanged=true;
 pass('All preexisting container IDs/images/state/start/restarts unchanged during isolated restore');
}catch(error){
 result.failureStage=stage; result.failureType=error.name;
 result.checks.push({name:'Full restore verification stopped; no raw database/backup output persisted',status:'FAIL'});console.log('FAIL '+stage+' ('+error.name+'); no main connection or migration used');process.exitCode=1;
}finally{
 if(pg){const c=inspect(pg);assert.equal(c.Id,pg);assert.equal(c.Config.Labels['aicos.purpose'],purpose);assert.deepEqual(Object.keys(c.NetworkSettings.Networks),[network]);docker(['rm','-f',pg]);}
 if(networkId){const n=JSON.parse(docker(['network','inspect',networkId]))[0];assert.equal(n.Labels['aicos.purpose'],purpose);assert.equal(Object.keys(n.Containers).length,0);docker(['network','rm',networkId]);}
 await fs.writeFile(path.join(backup,'RESTORE_RESULT.json'),JSON.stringify(result,null,2)+'\n');
 await fs.writeFile(path.resolve(includeRuntimeCompatibility?'docs/RUNTIME_COMPATIBILITY_EIGHT_DB_RESTORE_RESULT.json':includeUpstream?'docs/CASHIER_DASHBOARD_SIX_DB_RESTORE_RESULT.json':'docs/CASHIER_DASHBOARD_FULL_RESTORE_RESULT.json'),JSON.stringify(result,null,2)+'\n');
 console.log('RESULT '+result.checks.filter(c=>c.status==='PASS').length+' PASS; '+result.checks.filter(c=>c.status==='FAIL').length+' FAIL; only owned restore container/network removed.');
}
