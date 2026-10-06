// Run only against the isolated PHD2 built-in simulator fixture.
// Usage: node scripts/test-phd2-ai-pi.mjs http://127.0.0.1:5000 96
import net from 'node:net';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const base = process.argv[2] || 'http://127.0.0.1:5000';
const instance = Number(process.argv[3] || 96);
if (!Number.isInteger(instance) || instance < 2 || instance > 100)
  throw new Error('Use a dedicated test instance between 2 and 100; never instance 1.');
const host = new URL(base).hostname;
const socket = net.createConnection({ host, port: 4399 + instance });
await new Promise((resolve, reject) => { socket.once('connect', resolve); socket.once('error', reject); });
let id = 0, buffer = '';
const pending = new Map();
const steps = [];
socket.on('data', (chunk) => {
  buffer += chunk;
  let end;
  while ((end = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, end);
    buffer = buffer.slice(end + 1);
    if (!line.trim()) continue;
    const message = JSON.parse(line);
    if (message.Event === 'GuideStep') steps.push(message);
    if (pending.has(message.id)) {
      const request = pending.get(message.id);
      pending.delete(message.id);
      clearTimeout(request.timer);
      if (message.error) request.reject(new Error(message.error.message));
      else request.resolve(message.result);
    }
  }
});
socket.on('error', (error) => {
  for (const request of pending.values()) { clearTimeout(request.timer); request.reject(error); }
  pending.clear();
});
function rpc(method, params) {
  return new Promise((resolve, reject) => {
    const call = ++id;
    const timer = setTimeout(() => { pending.delete(call); reject(new Error('RPC timeout: ' + method)); }, 10000);
    pending.set(call, { resolve, reject, timer });
    socket.write(JSON.stringify({ jsonrpc: '2.0', id: call, method, params }) + '\n');
  });
}
async function http(method, route, data, expected = 200) {
  const response = await fetch(base + '/api/phd2/' + route, {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: data == null ? undefined : JSON.stringify(data),
    signal: AbortSignal.timeout(15000),
  });
  const envelope = await response.json();
  assert.equal(response.status, expected, JSON.stringify(envelope));
  assert.equal(envelope.Success, expected === 200, JSON.stringify(envelope));
  return envelope.Response;
}
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
async function until(check, timeout = 30000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) {
    const result = await check();
    if (result) return result;
    await sleep(1500);
  }
  throw new Error('Timed out waiting for test state');
}
let guarded = false;
let originalStorageDirectory = '';
const report = { started: new Date().toISOString(), instance, base };
try {
  const equipment = await rpc('get_current_equipment');
  assert.match(equipment.camera?.name || '', /Simulator/i);
  assert.match(equipment.mount?.name || '', /On.camera/i);
  const profile = await rpc('get_profile');
  assert.match(profile.name, /AI Simulator Test/);
  guarded = true;
  await http('POST', 'disconnect', {});
  await http('POST', 'connect', { instance, hostname: 'localhost' });
  const initialAI = await http('GET', 'ai/status');
  if (initialAI.storage_directory) {
    originalStorageDirectory = initialAI.storage_directory;
    const testDirectory = originalStorageDirectory + '/storage-test-' + Date.now();
    const created = await fetch(base + '/api/filesystem/directory', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ path: testDirectory }), signal: AbortSignal.timeout(15000),
    });
    assert.equal(created.ok, true);
    await http('PUT', 'ai/directory', { path: 'relative-folder' }, 409);
    await http('PUT', 'ai/directory', { path: testDirectory + '/does-not-exist' }, 409);
    const changed = await http('PUT', 'ai/directory', { path: testDirectory });
    assert.equal(changed.storage_directory, testDirectory);
    assert.ok(changed.model_directory.startsWith(testDirectory + '/instance-' + instance + '/'));
    const listing = await fetch(base + '/api/filesystem/browse?path=' + encodeURIComponent(changed.model_directory));
    assert.equal(listing.ok, true);
    assert.equal((await listing.json()).currentPath, changed.model_directory);
    report.storageDirectory = testDirectory;
    console.log('Existing Touch-N-Stars file API and profile storage folder verified.');
  }
  await http('PUT', 'ai/gain', { gain: 2 }, 400);
  await http('POST', 'ai/training/start', { duration_sec: '90', period_sec: 30 }, 400);
  await http('PUT', 'ai/mode', { mode: 'disabled' });
  await rpc('set_connected', { connected: true });
  await rpc('set_exposure', { exposure: 1000 });
  await rpc('loop');
  await sleep(3500);
  await rpc('find_star');
  await rpc('guide', { settle: { pixels: 1.5, time: 1, timeout: 30 }, recalibrate: false });
  await until(async () => (await rpc('get_app_state')) === 'Guiding');
  await sleep(4000);

  await http('POST', 'ai/training/start', { duration_sec: 90, period_sec: 30 });
  await sleep(3500);
  assert.equal((await http('POST', 'ai/training/cancel')).state, 'cancelled');
  await http('POST', 'ai/training/start', { duration_sec: 90, period_sec: 30 });
  await http('POST', 'ai/training/start', { duration_sec: 90, period_sec: 30 }, 409);
  if (originalStorageDirectory)
    await http('PUT', 'ai/directory', { path: originalStorageDirectory }, 409);
  await http('POST', 'ai/recording/start', { mode: 'passive', duration_sec: 60 }, 409);
  console.log('Recording ordinary guiding for 90 seconds...');
  const training = await until(async () => {
    const status = await http('GET', 'ai/training/status');
    if (status.state === 'failed') throw new Error(status.error);
    return status.state === 'complete' ? status : false;
  }, 120000);
  assert.ok(training.fit_frames >= 30);
  assert.ok(training.training_cycles >= 2);
  report.training = training;
  if (report.storageDirectory) {
    assert.ok(training.model_path.startsWith(report.storageDirectory + '/'));
    assert.ok(training.recording_path.startsWith(report.storageDirectory + '/'));
  }
  console.log('Native training complete:', training.fit_frames, 'frames');
  const status = await http('GET', 'ai/status');
  assert.equal(status.mode, 'disabled');
  assert.equal(status.model_loaded, true);
  assert.equal(status.fingerprint_ok, true);
  const library = await http('GET', 'ai/models');
  assert.ok(library.models.some((model) => model.path === status.model_path));
  const exported = status.model_directory + '/export-test-' + Date.now() + '.json';
  await http('POST', 'ai/models/export', { path: exported });
  await http('POST', 'ai/models/export', { path: exported }, 409);
  const imported = await http('POST', 'ai/models/import', { path: exported });
  await http('POST', 'ai/models/select', { path: imported.path });

  await http('PUT', 'ai/mode', { mode: 'shadow' });
  await sleep(22000);
  const shadow = steps.filter((step) => step.AIMode === 'shadow');
  assert.ok(shadow.length >= 5);
  assert.ok(shadow.every((step) => step.AIRAContribution === 0 && step.AIDecContribution === 0));
  console.log('Shadow verified:', shadow.length, 'unchanged frames');
  await http('PUT', 'ai/gain', { gain: 0.1 });
  await http('PUT', 'ai/mode', { mode: 'active' });
  await sleep(40000);
  const active = steps.filter((step) => step.AIMode === 'active');
  assert.ok(active.length >= 15);
  assert.ok(active.some((step) => Math.abs(step.AIRAContribution) > 0));
  assert.ok(active.every((step) => step.AIDecContribution === 0 &&
    Math.abs(step.AIRAContribution) <= Math.min(0.5, 0.5 * Math.max(Math.abs(step.AIBaseRA), 0.1)) + 1e-6));
  report.shadowFrames = shadow.length;
  report.activeFrames = active.length;
  report.maxActiveContribution = Math.max(...active.map((step) => Math.abs(step.AIRAContribution)));
  console.log('Active verified:', active.length, 'bounded RA frames');

  await http('PUT', 'ai/mode', { mode: 'disabled' });
  await http('POST', 'ai/recording/stop');
  await rpc('stop_capture');
  await until(async () => ['Stopped', 'Selected'].includes(await rpc('get_app_state')));
  await rpc('set_connected', { connected: false });
  await http('POST', 'ai/training/fit', { recording_path: training.recording_path, period_sec: 30 });
  await until(async () => {
    const result = await http('GET', 'ai/training/status');
    if (result.state === 'failed') throw new Error(result.error);
    return result.state === 'complete';
  });
  await http('POST', 'ai/models/unload');
  assert.equal((await http('GET', 'ai/status')).model_loaded, false);
  await http('POST', 'ai/models/select', { path: status.model_path });
  report.success = true;
  report.finished = new Date().toISOString();
  await fs.writeFile(new URL('./ai-pi-test-result.json', import.meta.url), JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify(report, null, 2));
} finally {
  if (guarded) {
    await http('PUT', 'ai/mode', { mode: 'disabled' }).catch(() => {});
    await http('POST', 'ai/training/cancel').catch(() => {});
    await rpc('stop_capture').catch(() => {});
    await until(async () => ['Stopped', 'Selected'].includes(await rpc('get_app_state')), 10000).catch(() => {});
    await rpc('set_connected', { connected: false }).catch(() => {});
    if (originalStorageDirectory)
      await http('PUT', 'ai/directory', { path: originalStorageDirectory }).catch(() => {});
    await http('POST', 'disconnect', {}).catch(() => {});
    await http('POST', 'connect', { instance: 1, hostname: 'localhost' }).catch(() => {});
  }
  socket.destroy();
}
