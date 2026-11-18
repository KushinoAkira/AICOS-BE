// Explicitly approved LOCAL Bill/Cash activation. No migrations, GRANTs, payment POSTs or fake JWTs.
import cp from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import http from 'node:http';
import assert from 'node:assert/strict';
assert.equal(process.argv[2],'--approved-local-bill-cash');
const run=args=>cp.execFileSync('docker',args,{encoding:'utf8',stdio:['pipe','pipe','pipe']}).trim();
const inspect=id=>JSON.parse(run(['inspect',id]))[0];
const env=c=>Object.fromEntries(c.Config.Env.map(s=>{const i=s.indexOf('=');return [s.slice(0,i),s.slice(i+1)];}));
const ps=(code,input)=>{const r=cp.spawnSync('powershell.exe',['-NoProfile','-NonInteractive','-Command',code],{input,encoding:'utf8',windowsHide:true});assert.equal(r.status,0,'Secure local operation failed');return r.stdout.trim();};
const privateDir=dir=>{fs.mkdirSync(dir,{recursive:true});const sid=ps('[Security.Principal.WindowsIdentity]::GetCurrent().User.Value');cp.execFileSync('icacls',[dir,'/inheritance:r','/grant:r','*'+sid+':(OI)(CI)F','*S-1-5-18:(OI)(CI)F','*S-1-5-32-544:(OI)(CI)F','/Q'],{stdio:'pipe'});};
const api=(method,url,body)=>new Promise((resolve,reject)=>{const req=http.request({socketPath:'//./pipe/docker_engine',method,path:url,headers:{'Content-Type':'application/json'}},res=>{let text='';res.on('data',d=>text+=d);res.on('end',()=>res.statusCode>=300?reject(Error('Docker API status '+res.statusCode)):resolve(text?JSON.parse(text):{}));});req.on('error',()=>reject(Error('Docker pipe unavailable')));req.end(body?JSON.stringify(body):undefined);});
const brief=c=>({id:c.Id,image:c.Image,started:c.State.StartedAt,status:c.State.Status,restarts:c.RestartCount});
const names=['order','kitchen','table','payment'],schemas={order:'ordering',kitchen:'kitchen',table:'table_reservation',payment:'payment'};
const old=new Map(),fresh=new Map();const report={date:'2026-11-17',scope:'Approved local Bill/Cash; QR OFF; no SQL writes',checks:[]};
const pass=name=>{report.checks.push({name,status:'PASS'});console.log('PASS '+name);};let phase='preflight';
try {
 const pg=inspect('aicos-main-postgres'),admin=env(pg).POSTGRES_USER||'postgres';
 assert.equal(pg.Config.Labels['com.docker.compose.project'],'aicos-main');
 const sql=(db,text)=>run(['exec',pg.Id,'psql','-U',admin,'-d',db,'-XqAt','-v','ON_ERROR_STOP=1','-c','BEGIN TRANSACTION READ ONLY; '+text+' COMMIT;']);
 assert.equal(sql('aicos_payment','SELECT system_identifier FROM pg_control_system();'),'7703616372368646182');
 for(const n of names){const c=inspect('aicos-main-'+n);old.set(n,c);assert.equal(c.State.Running,true);assert.equal(c.Mounts.length,0);assert.equal(env(c).Database__MigrateOnStartup,'false');assert.equal(c.Config.Labels['com.docker.compose.project'],'aicos-main');}
 const beforeOthers=run(['ps','-aq']).split(/\r?\n/).filter(Boolean).map(id=>inspect(id)).filter(c=>![...old.values()].some(o=>o.Id===c.Id)).map(brief);
 const required={aicos_payment:['payment.session_bills','payment.session_bill_orders','payment.session_bill_attempts','payment.session_bill_events'],aicos_ordering:['ordering.session_checkouts']};
 for(const [db,tables] of Object.entries(required))for(const t of tables)assert.equal(sql(db,'SELECT to_regclass(\''+t+'\');'),t);
 for(const [db,tables] of Object.entries(required))for(const t of tables)assert.equal(sql(db,'SELECT count(*) FROM '+t+';'),'0','Existing Bill state requires separate reconciliation');
 const privileges=[['aicos_ordering','ordering','ordering_svc','session_checkouts',['state','payment_id']],['aicos_payment','payment','payment_svc','session_bills',['status','version','paid_payment_id','paid_at_utc','closed_at_utc','ordering_acknowledged','needs_reconciliation']],['aicos_payment','payment','payment_svc','session_bill_attempts',['status','provider_reference','checkout_url','paid_at_utc']],['aicos_payment','payment','payment_svc','session_bill_events',['delivered_at_utc','next_attempt_at_utc','lease_owner','lease_until_utc','fence']]];
 for(const [db,s,role,t,columns] of privileges){for(const col of columns)assert.equal(sql(db,"SELECT has_column_privilege('"+role+"','"+s+'.'+t+"','"+col+"','UPDATE');"),'t');for(const privilege of ['SELECT','INSERT'])assert.equal(sql(db,"SELECT has_table_privilege('"+role+"','"+s+'.'+t+"','"+privilege+"');"),'t');}
 report.legacyPaidOpenOrders=Number(sql('aicos_ordering',"SELECT count(*) FROM ordering.orders o JOIN ordering.table_sessions s ON s.id=o.table_session_id WHERE s.closed_at_utc IS NULL AND (o.payment_status IN ('Paid','Refunded') OR o.refunded_amount>0);"));
 report.legacyPolicy='Existing already-paid legacy sessions stay blocked by billing.legacy_payment; no reset or money conversion.';
 pass('Exact local cluster, five existing empty Bill tables and required column permissions PASS; legacy money preserved');
 phase='pause four services and backup';for(const n of [...names].reverse())await api('POST','/containers/'+old.get(n).Id+'/stop?t=20');
 const stamp='20261117-'+crypto.randomBytes(5).toString('hex'),backupDir=path.join(process.env.LOCALAPPDATA,'AICOS','private-backups','session-billing-'+stamp);privateDir(backupDir);
 const fingerprint=db=>{const schema=Object.values(schemas).find(s=>'aicos_'+s===db)||db.slice(6);const tables=sql(db,"SELECT tablename FROM pg_tables WHERE schemaname='"+schema+"' ORDER BY tablename;").split(/\r?\n/).filter(Boolean);return tables.map(t=>{assert.match(t,/^[a-z_]+$/);return [t,sql(db,'SELECT count(*),md5(COALESCE(string_agg(row_to_json(r)::text,chr(124) ORDER BY row_to_json(r)::text),chr(124))) FROM '+schema+'."'+t+'" r;')];});};
 const databases=names.map(n=>'aicos_'+schemas[n]),before={};report.backups=[];
 for(const db of databases){before[db]=fingerprint(db);const file=path.join(backupDir,db+'.dump'),fd=fs.openSync(file,'wx');let result;try{result=cp.spawnSync('docker',['exec',pg.Id,'pg_dump','-U',admin,'-d',db,'-Fc'],{stdio:['ignore',fd,'pipe']});}finally{fs.closeSync(fd);}assert.equal(result.status,0,'Backup failed');const bytes=fs.readFileSync(file);assert.ok(bytes.length>100);assert.equal(bytes.subarray(0,5).toString(),'PGDMP');report.backups.push({database:db,file,bytes:bytes.length,sha256:crypto.createHash('sha256').update(bytes).digest('hex')});}
 fs.writeFileSync(path.join(backupDir,'manifest.json'),JSON.stringify(report.backups,null,2));report.backupDirectory=backupDir;
 pass('Four fresh custom-format backups created with private ACL/SHA256 while all target writers stopped');
 phase='configure separate protected machine key';
 const keyDir=path.join(process.env.LOCALAPPDATA,'AICOS','session-billing-local'),keyFile=path.join(keyDir,'machine-key.dpapi');privateDir(keyDir);
 let key;if(fs.existsSync(keyFile)){key=ps('$s=ConvertTo-SecureString ([Console]::In.ReadToEnd());$p=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($s);try{[Console]::Write([Runtime.InteropServices.Marshal]::PtrToStringBSTR($p))}finally{[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p)}',fs.readFileSync(keyFile,'utf8'));}
 else{key=crypto.randomBytes(32).toString('base64');const protectedKey=ps('$s=ConvertTo-SecureString ([Console]::In.ReadToEnd()) -AsPlainText -Force;ConvertFrom-SecureString $s',key);fs.writeFileSync(keyFile,protectedKey,{flag:'wx'});}
 assert.ok(key.length>=32&&!/[\r\n]/.test(key));report.machineKey='Separate DPAPI-protected local credential; not logged';
 pass('Separate machine credential protected with DPAPI; no DB password or JWT changes');
 phase='start coordinated runtime';
 for(const n of names){const c=old.get(n),settings=env(c),next={...settings,SessionBilling__Enabled:'true',SessionBilling__MachineKey:key,SessionBilling__AllowTestProviders:'false'};if(n==='payment')next.SessionBilling__DispatcherEnabled='true';
  assert.equal(next.Database__MigrateOnStartup,'false');if(n==='kitchen'){assert.equal(next.BaristaWorkflow__Enabled,'true');assert.equal(next.BaristaWorkflow__WorkerEnabled,'false');}
  await api('POST','/containers/'+c.Id+'/rename?name=aicos-main-'+n+'-before-billing-'+c.Id.slice(0,8));
  const endpoints=Object.fromEntries(Object.entries(c.NetworkSettings.Networks).map(([net,v])=>[net,{Aliases:v.Aliases}]));
  const made=await api('POST','/containers/create?name=aicos-main-'+n,{...c.Config,Hostname:'',Image:c.Image,Env:Object.entries(next).map(([k,v])=>k+'='+v),HostConfig:c.HostConfig,NetworkingConfig:{EndpointsConfig:endpoints}});fresh.set(n,made.Id);await api('POST','/containers/'+made.Id+'/start');
  const port={order:5204,kitchen:5207,table:5203,payment:5205}[n];let ready=false;for(let i=0;i<120;i++){try{if((await fetch('http://127.0.0.1:'+port+'/health/ready')).status===200){ready=true;break;}}catch{}await new Promise(r=>setTimeout(r,500));}assert.ok(ready,'Service readiness failed: '+n);
  const actual=inspect(made.Id);assert.equal(actual.Image,c.Image);const actualEnv=env(actual);for(const [k,v] of Object.entries(next))assert.equal(actualEnv[k],v,'Environment preservation failed');
  pass(n+' ready on exact existing image; Billing ON, QR OFF, migration OFF; unrelated settings preserved');
 }
 phase='read-only smoke and preservation';
 const tenant=sql('aicos_ordering','SELECT tenant_id FROM ordering.table_sessions ORDER BY id LIMIT 1;');
 const occupancy='http://127.0.0.1:5204/internal/session-billing/occupancy?tenantId='+tenant;
 assert.equal((await fetch(occupancy,{headers:{'X-Aicos-Billing-Key':key}})).status,200);
 assert.equal((await fetch(occupancy)).status,401);
 for(const url of ['http://127.0.0.1:8081/api/v1/orders/_info','http://127.0.0.1:8081/api/v1/payments/_info','http://127.0.0.1:8081/api/v1/kitchen/_info'])assert.equal((await fetch(url)).status,200);
 assert.equal((await fetch('http://127.0.0.1:8081/api/v1/payments/bills?sessionId='+crypto.randomUUID())).status,401);
 for(const db of databases)assert.deepEqual(fingerprint(db),before[db],'Data changed during activation: '+db);
 for(const prior of beforeOthers)assert.deepEqual(brief(inspect(prior.id)),prior);
 report.originalDataUnchanged=true;report.otherContainersUnchanged=true;
 pass('Machine auth works; Gateway/health PASS; all four database fingerprints and other containers unchanged');
 for(const n of names){assert.equal(inspect(old.get(n).Id).State.Running,false);assert.equal(inspect(old.get(n).Id).Mounts.length,0);run(['rm',old.get(n).Id]);}
 report.status='PASS';report.paymentSmoke='Read-only only; no login, JWT creation, payment POST or session close';
} catch(error){report.status='FAIL';report.failedPhase=phase;report.safeError=String(error.message).replace(/eyJ[A-Za-z0-9_.-]{40,}/g,'[redacted]').slice(0,180);console.log('FAIL '+phase+': '+report.safeError);process.exitCode=1;}
finally{report.newContainers=Object.fromEntries(fresh);fs.writeFileSync('docs/LOCAL_BILL_CASH_ACTIVATION_RESULT.json',JSON.stringify(report,null,2)+'\n');}