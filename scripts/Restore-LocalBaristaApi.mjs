// Restore the previously approved local Barista API only; never runs SQL mutations or migrations.
import cp from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import assert from 'node:assert/strict';
const run=args=>cp.execFileSync('docker',args,{encoding:'utf8',stdio:['pipe','pipe','pipe']}).trim();
const inspect=id=>JSON.parse(run(['inspect',id]))[0];
const env=c=>Object.fromEntries(c.Config.Env.map(s=>{const i=s.indexOf('=');return [s.slice(0,i),s.slice(i+1)];}));
const brief=c=>({id:c.Id,image:c.Image,started:c.State.StartedAt,status:c.State.Status,restarts:c.RestartCount});
const api=(method,path,body)=>new Promise((resolve,reject)=>{
 const r=http.request({socketPath:'//./pipe/docker_engine',method,path,headers:{'Content-Type':'application/json'}},res=>{
  let text='';res.on('data',d=>text+=d);res.on('end',()=>{if(res.statusCode>=300)return reject(Error('Docker API failed: '+res.statusCode));resolve(text?JSON.parse(text):{});});
 });r.on('error',()=>reject(Error('Docker API unavailable')));r.end(body?JSON.stringify(body):undefined);
});
const report={date:'2026-11-17',scope:'local Barista API only',checks:[]};
const pass=name=>{report.checks.push({name,status:'PASS'});console.log('PASS '+name);};
let phase='preflight',replacement,old;
try {
 old=inspect('aicos-main-kitchen');const pg=inspect('aicos-main-postgres');
 assert.equal(old.Config.Labels['com.docker.compose.project'],'aicos-main');assert.equal(old.Mounts.length,0);
 assert.equal(old.State.Running,true);const settings=env(old),admin=env(pg).POSTGRES_USER||'postgres';
 assert.equal(settings.Database__MigrateOnStartup,'false');assert.equal(settings.BaristaWorkflow__WorkerEnabled,'false');
 assert.equal(settings.SessionBilling__Enabled,'false');assert.equal(settings.KitchenAdmission__ReceiverEnabled,'false');
 assert.equal(settings.CupServing__ApiEnabled,'false');
 const sql=text=>run(['exec',pg.Id,'psql','-U',admin,'-d','aicos_kitchen','-XAt','-v','ON_ERROR_STOP=1','-c',"BEGIN TRANSACTION READ ONLY; "+text+' COMMIT;']);
 assert.match(sql('SELECT system_identifier FROM pg_control_system();'),/7703616372368646182/);
 assert.match(sql("SELECT migration_id FROM kitchen.__ef_migrations_history WHERE migration_id='20261114041318_KitchenBaristaWorkflow';"),/KitchenBaristaWorkflow/);
 const tables=sql("SELECT table_name FROM information_schema.tables WHERE table_schema='kitchen' ORDER BY table_name;").split(/\r?\n/).filter(x=>/^[a-z_]+$/.test(x));
 for(const name of ['barista_acceptances','kitchen_print_jobs','kitchen_tickets','kitchen_ticket_items'])assert.ok(tables.includes(name));
 const fingerprints=()=>tables.map(t=>sql('SELECT count(*),md5(COALESCE(string_agg(row_to_json(r)::text,chr(124) ORDER BY row_to_json(r)::text),chr(124))) FROM kitchen."'+t+'" r;'));
 const before=fingerprints();const ids=run(['ps','-aq']).split(/\r?\n/).filter(Boolean);const others=ids.filter(id=>inspect(id).Id!==old.Id).map(id=>brief(inspect(id)));
 report.originalImage=old.Image;report.previousKitchenId=old.Id;pass('Verified local cluster/schema/migration; no mounted data; startup migration and workers OFF');
 const next={...settings,BaristaWorkflow__Enabled:'true',BaristaWorkflow__WorkerEnabled:'false'};
 for(const key of Object.keys(settings))if(key!=='BaristaWorkflow__Enabled')assert.equal(next[key],settings[key]);
 phase='replace Kitchen runtime';
 await api('POST','/containers/'+old.Id+'/stop?t=20');
 const fallback='aicos-main-kitchen-before-barista-'+old.Id.slice(0,8);
 await api('POST','/containers/'+old.Id+'/rename?name='+fallback);
 const endpoints=Object.fromEntries(Object.entries(old.NetworkSettings.Networks).map(([name,value])=>[name,{Aliases:value.Aliases}]));
 const created=await api('POST','/containers/create?name=aicos-main-kitchen',{
  ...old.Config,Hostname:'',Image:old.Image,Env:Object.entries(next).map(([k,v])=>k+'='+v),
  HostConfig:old.HostConfig,NetworkingConfig:{EndpointsConfig:endpoints}
 });replacement=created.Id;await api('POST','/containers/'+replacement+'/start');
 async function until(action,label){for(let i=0;i<120;i++){try{if(await action())return;}catch{}await new Promise(r=>setTimeout(r,500));}throw Error(label+' failed');}
 phase='health and read-only verification';
 await until(async()=>(await fetch('http://127.0.0.1:5207/health/ready')).status===200,'Kitchen health');
 await until(async()=>(await fetch('http://127.0.0.1:8081/api/v1/kitchen/_info')).status===200,'Gateway route');
 const current=inspect(replacement);assert.equal(current.Image,old.Image);const actual=env(current);
 for(const [key,value] of Object.entries(next))assert.equal(actual[key],value,'Environment preservation failed');
 pass('Only Kitchen recreated on identical image; credentials/JWT/ports/other flags preserved; Barista API ON, print worker OFF');
 for(const path of ['/api/v1/kitchen/barista/tickets','/api/v1/kitchen/barista/summary','/api/v1/kitchen/barista/table-summary']) {
  assert.equal((await fetch('http://127.0.0.1:8081'+path)).status,401);
  assert.equal((await fetch('http://127.0.0.1:8081'+path,{headers:{Authorization:'Bearer invalid-token'}})).status,401);
 }
 pass('Kitchen/Gateway health 200; Barista GET authentication rejects missing/invalid token with 401');
 assert.deepEqual(fingerprints(),before);for(const prior of others)assert.deepEqual(brief(inspect(prior.id)),prior);
 report.databaseRowsUnchanged=true;report.otherContainersUnchanged=true;pass('All Kitchen table row fingerprints and all other container snapshots unchanged');
 assert.equal(inspect(old.Id).State.Running,false);assert.equal(inspect(old.Id).Mounts.length,0);run(['rm',old.Id]);
 report.newKitchenId=replacement;report.status='PASS';
} catch(error) {
 report.status='FAIL';report.failedPhase=phase;report.newKitchenId=replacement??null;
 console.log('FAIL stopped at '+phase+'. No automatic schema/data rollback.');process.exitCode=1;
} finally {
 fs.writeFileSync('docs/BARISTA_API_RUNTIME_RESTORE_RESULT.json',JSON.stringify(report,null,2)+'\n');
}