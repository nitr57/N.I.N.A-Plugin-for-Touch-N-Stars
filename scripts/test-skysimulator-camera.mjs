// Connection/exposure smoke test only. It never starts guiding or calibrates.
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { setTimeout as sleep } from 'node:timers/promises';
const base = process.argv[2] || 'http://127.0.0.1:5000';
async function api(route, method = 'GET') {
  const response = await fetch(`${base}/api/phd2/${route}`, {method, signal: AbortSignal.timeout(15000)});
  const result = await response.json();
  assert.equal(result.Success, true, JSON.stringify(result));
  return result.Response;
}
const equipment = (await api('get-current-equipment')).CurrentEquipment;
assert.match(equipment.camera?.name || '', /guide camera sky simulator/i);
assert.match(equipment.mount?.name || '', /mount sky simulator/i);
assert.equal(equipment.camera.connected, true);
assert.equal(equipment.mount.connected, true);
assert.ok(['Stopped', 'Selected'].includes((await api('status')).AppState));
try {
  await api('start-looping', 'POST');
  await sleep(4000);
  assert.equal((await api('status')).AppState, 'Looping');
  const image = await fetch(`${base}/api/phd2/current-image?gamma=0.5`, {signal: AbortSignal.timeout(15000)});
  assert.equal(image.status, 200);
  assert.match(image.headers.get('content-type'), /image\/jpeg/);
  const bytes = Buffer.from(await image.arrayBuffer());
  assert.ok(bytes.length > 1000);
  assert.equal(bytes.readUInt16BE(0), 0xffd8);
  await fs.writeFile(new URL('./skysimulator-camera-test.jpg', import.meta.url), bytes);
  console.log(`SkySimulator Alpaca guide camera and mount connected; received JPEG (${bytes.length} bytes).`);
} finally {
  await api('stop-guiding', 'POST');
  for (let i = 0; i < 20; i++) {
    if (['Stopped', 'Selected'].includes((await api('status')).AppState)) break;
    await sleep(500);
  }
  assert.ok(['Stopped', 'Selected'].includes((await api('status')).AppState));
}
