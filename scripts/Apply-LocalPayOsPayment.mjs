// Payment-only local replacement; secrets stay in memory, SQL is read-only, no business API call.
import cp from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import http from 'node:http';
import assert from 'node:assert/strict';

const mode = process.argv[2] || '--check';
assert.ok(['--check', '--apply'].includes(mode));
const reportPath = 'docs/PAYOS_LOCAL_RUNTIME_RESULT.json';
assert.ok(mode !== '--apply' || !fs.existsSync(reportPath), 'Existing rollout evidence requires review; no repeat apply or overwrite.');
const expectedOld = 'b48d6cec0579fe48e2fd413f3e83cf26a4cd756c409546727450aa97559eaf21';
const expectedImage = 'sha256:97a20b743bc2c4ffccef8ece325d3a877cffff6daefb2ff1e84ac84462b0d5b0';
const replacementImage = 'sha256:e5b000fe72658203a23a92e380591f43d721acb0e8803ec2b784952df19b8fee';
const run = args => cp.execFileSync('docker', args, { encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'] }).trim();
const inspect = id => JSON.parse(run(['inspect', id]))[0];
const settings = c => Object.fromEntries(c.Config.Env.map(value => { const at = value.indexOf('='); return [value.slice(0, at), value.slice(at + 1)]; }));
const brief = c => ({ id: c.Id, image: c.Image, started: c.State.StartedAt, status: c.State.Status, restarts: c.RestartCount });
const hash = value => crypto.createHash('sha256').update(value).digest('hex');
const report = { status: 'FAIL', scope: 'Payments local only; no feature activation', startedUtc: new Date().toISOString(), checks: [] };
const pass = name => { report.checks.push(name); console.log('PASS ' + name); };
const api = (method, route, body) => new Promise((resolve, reject) => {
  const request = http.request({ socketPath: '//./pipe/docker_engine', method, path: route, headers: { 'Content-Type': 'application/json' } }, response => {
    let data = ''; response.on('data', chunk => data += chunk);
    response.on('end', () => response.statusCode >= 300 ? reject(Error('Docker operation failed')) : resolve(data ? JSON.parse(data) : {}));
  });
  request.on('error', () => reject(Error('Docker unavailable')));
  request.end(body ? JSON.stringify(body) : undefined);
});
let phase = 'preflight';
try {
  const old = inspect('aicos-main-payment');
  assert.equal(old.Id, expectedOld); assert.equal(old.Image, expectedImage); assert.equal(old.State.Running, true);
  assert.equal(old.Mounts.length, 0); assert.deepEqual(Object.keys(old.NetworkSettings.Networks), ['aicos-main_default']);
  assert.equal(old.Config.Labels['com.docker.compose.service'], 'payment');
  const env = settings(old);
  assert.equal(env.Database__MigrateOnStartup, 'false'); assert.equal(env.BackgroundJobs__ExpiryEnabled, 'false');
  assert.equal(env.CashierShifts__DispatcherEnabled, 'false');
  assert.ok(env.Payments__PayOs__StaffPaymentsEnabled !== 'true');
  assert.ok(env.TakeawayQr__Enabled !== 'true');
  const image = JSON.parse(run(['image', 'inspect', replacementImage]))[0];
  assert.equal(image.Config.Labels['aicos.change'], 'payos-staff-readonly');
  const pg = inspect('aicos-main-postgres'); assert.equal(pg.State.Running, true);
  const admin = settings(pg).POSTGRES_USER || 'postgres';
  const sql = (database, text) => run(['exec', pg.Id, 'psql', '-U', admin, '-d', database, '-XAt', '-v', 'ON_ERROR_STOP=1', '-c', 'BEGIN READ ONLY; ' + text + ' COMMIT;'])
    .split(/\r?\n/).filter(line => !['BEGIN', 'COMMIT', ''].includes(line));
  assert.deepEqual(sql('aicos_payment', 'SELECT system_identifier::text FROM pg_control_system();'), ['7703616372368646182']);
  assert.deepEqual(sql('aicos_payment', "SELECT count(*) FROM payment.session_bill_events WHERE kind='PAID' AND delivered_at_utc IS NULL;"), ['0']);
  assert.deepEqual(sql('aicos_payment', "SELECT count(*) FROM payment.payment_providers WHERE code='PayOS';"), ['0']);
  assert.deepEqual(sql('aicos_payment', 'SELECT count(*) FROM pg_stat_activity WHERE pid<>pg_backend_pid() AND backend_xid IS NOT NULL;'), ['0']);
  const databases = sql('aicos_payment', "SELECT datname FROM pg_database WHERE datallowconn AND datname ~ '^aicos_[a-z_]+$' ORDER BY datname;");
  const snapshot = () => Object.fromEntries(databases.map(database => {
    const tables = sql(database, "SELECT table_schema||'.'||table_name FROM information_schema.tables WHERE table_type='BASE TABLE' AND table_schema !~ '^pg_' AND table_schema<>'information_schema' ORDER BY 1;");
    assert.ok(tables.every(name => /^[a-z_]+\.[a-z_]+$/.test(name)));
    const rows = tables.length ? sql(database, tables.map(name => {
      const [schema, table] = name.split('.');
      return `SELECT '${name}',count(*),md5(COALESCE(string_agg(row_to_json(r)::text,chr(124) ORDER BY row_to_json(r)::text),chr(124))) FROM "${schema}"."${table}" r;`;
    }).join(' ')) : [];
    return [database, { tables: tables.length, hash: hash(JSON.stringify(rows)) }];
  }));
  const before = snapshot();
  const others = run(['ps', '-aq', '--no-trunc']).split(/\r?\n/).filter(id => id && id !== old.Id).map(id => brief(inspect(id)));
  const fallback = 'aicos-main-payment-before-payos-' + old.Id.slice(0, 8);
  assert.ok(!run(['ps', '-a', '--format', '{{.Names}}']).split(/\r?\n/).includes(fallback));
  report.before = brief(old); report.fingerprintsBefore = before;
  pass('Exact image/container/cluster verified; no outstanding PAID dispatch, PayOS provider or active writer; migrations/creation OFF');
  if (mode === '--check') { console.log('CHECK ONLY: no container, backup or database change'); process.exit(0); }

  phase = 'preserve original and backup';
  await api('POST', '/containers/' + old.Id + '/stop?t=20');
  assert.deepEqual(snapshot(), before);
  const directory = path.join(process.env.LOCALAPPDATA, 'AICOS', 'backups', 'payment-payos-' + Date.now());
  fs.mkdirSync(directory, { recursive: true });
  const temporaryDump = '/tmp/aicos-payment-payos-' + crypto.randomUUID() + '.dump';
  const dump = path.join(directory, 'aicos_payment.dump');
  run(['exec', pg.Id, 'pg_dump', '-U', admin, '-d', 'aicos_payment', '-Fc', '-f', temporaryDump]);
  assert.ok(run(['exec', pg.Id, 'pg_restore', '--list', temporaryDump]).includes('session_bills'));
  run(['cp', pg.Id + ':' + temporaryDump, dump]);
  report.backup = { path: dump, sha256: hash(fs.readFileSync(dump)), toc: 'PASS', restoreDrill: 'NOT RUN in this gate' };
  assert.match(temporaryDump, /^\/tmp\/aicos-payment-payos-[0-9a-f-]+\.dump$/);
  run(['exec', pg.Id, 'rm', temporaryDump]);
  fs.writeFileSync('docs/PAYOS_LOCAL_RUNTIME_RESULT.json', JSON.stringify(report, null, 2) + '\n');
  await api('POST', '/containers/' + old.Id + '/rename?name=' + fallback);
  report.fallbackName = fallback;
  phase = 'replace Payments only';
  const endpoints = Object.fromEntries(Object.entries(old.NetworkSettings.Networks).map(([name, value]) => [name, { Aliases: value.Aliases.filter(alias => alias !== old.Id && alias !== old.Id.slice(0, 12)) }]));
  const made = await api('POST', '/containers/create?name=aicos-main-payment', {
    ...old.Config, Hostname: '', Image: replacementImage, HostConfig: old.HostConfig,
    Labels: { ...old.Config.Labels, 'aicos.change': 'payos-staff-readonly', 'org.opencontainers.image.revision': 'local-payos-staff-readonly-20261120' },
    NetworkingConfig: { EndpointsConfig: endpoints }
  });
  report.replacementId = made.Id;
  fs.writeFileSync('docs/PAYOS_LOCAL_RUNTIME_RESULT.json', JSON.stringify(report, null, 2) + '\n');
  await api('POST', '/containers/' + made.Id + '/start');
  phase = 'read-only smoke';
  for (const url of ['http://127.0.0.1:5205/health/live', 'http://127.0.0.1:5205/health/ready', 'http://127.0.0.1:8081/api/v1/payments/_info']) {
    let ready = false;
    for (let attempt = 0; attempt < 40; attempt++) {
      try { if ((await fetch(url, { redirect: 'manual', signal: AbortSignal.timeout(1500) })).status === 200) { ready = true; break; } } catch { /* bounded startup probe */ }
      await new Promise(resolve => setTimeout(resolve, 500));
    }
    assert.ok(ready);
  }
  for (const origin of ['http://127.0.0.1:5205', 'http://127.0.0.1:8081']) {
    const response = await fetch(origin + '/api/v1/payments/payos/return', { redirect: 'manual', headers: { 'X-Aicos-Return-Read-Only': '1' }, signal: AbortSignal.timeout(3000) });
    assert.equal(response.status, 302); assert.equal(response.headers.get('x-aicos-return-mode'), 'read-only');
    assert.ok(response.headers.get('cache-control')?.includes('no-store'));
    assert.equal((await fetch(origin + '/api/v1/payments/bills?sessionId=' + crypto.randomUUID(), { signal: AbortSignal.timeout(3000) })).status, 401);
  }
  const current = inspect(made.Id);
  assert.ok(hash(JSON.stringify(current.Config.Env)) === hash(JSON.stringify(old.Config.Env)));
  assert.deepEqual(current.HostConfig.PortBindings, old.HostConfig.PortBindings);
  assert.deepEqual(Object.keys(current.NetworkSettings.Networks), Object.keys(old.NetworkSettings.Networks));
  assert.equal(settings(current).Database__MigrateOnStartup, 'false');
  const after = snapshot();
  assert.deepEqual(after, before);
  for (const other of others) assert.deepEqual(brief(inspect(other.id)), other);
  assert.equal(inspect(old.Id).State.Running, false);
  report.replacement = brief(current); report.fingerprintsAfter = after; report.fingerprintsUnchanged = true;
  report.otherContainersUnchanged = others.length; report.status = 'PASS';
  pass('Health/Gateway/capability/auth PASS; original credentials/flags/ports retained; all service DB fingerprints unchanged');
} catch {
  report.failedPhase = phase; process.exitCode = 1;
  console.log('FAIL at ' + phase + '; no automatic rollback or further service action. Secrets/raw errors suppressed.');
} finally {
  if (mode !== '--check') fs.writeFileSync('docs/PAYOS_LOCAL_RUNTIME_RESULT.json', JSON.stringify(report, null, 2) + '\n');
}
