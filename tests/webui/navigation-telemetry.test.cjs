const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { parseRoute, pages } = require('../../src/Mango/wwwroot/shell.js');
const { ResourceHistory } = require('../../src/Mango/wwwroot/telemetry.js');

test('all page routes, legacy links and object links resolve without exposing session data', () => {
  for (const page of Object.keys(pages)) assert.equal(parseRoute(`#/${page}`).page, page);
  assert.equal(parseRoute('#private-network').page, 'network/nodes');
  assert.equal(parseRoute('#settings').page, 'protection/activity');
  assert.equal(parseRoute('#session=SECRET').page, 'overview');
  assert.deepEqual(parseRoute('#/serving/node%20one/provider?model=model1&operation=chat'),
    { page: 'serving', group: 'serving', node: 'node one', provider: 'provider', model: 'model1', operation: 'chat' });
  assert.equal(parseRoute('#/serving/%E0%A4%A').node, null);
});

test('the shell has one page per route and no static save or hidden editor controls', () => {
  const root = path.resolve(__dirname, '../../src/Mango/wwwroot');
  const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
  const declared = [...html.matchAll(/<section data-page="([^"]+)"/g)].map(match => match[1]);
  assert.deepEqual(declared.sort(), Object.keys(pages).sort());
  const ids = [...html.matchAll(/\bid="([^"]+)"/g)].map(match => match[1]);
  assert.equal(new Set(ids).size, ids.length);
  assert.doesNotMatch(html, /<details\b|<button[^>]*>\s*(Save|Apply)/i);
  for (const script of ['mango.js', 'network-catalog.js', 'network.js', 'resources.js', 'logs.js'])
    assert.doesNotMatch(fs.readFileSync(path.join(root, script), 'utf8'), /(?:action|element)\([^\n]*(?:Save model|Save provider|Edit requested|Edit provider)/);
  for (const match of html.matchAll(/(?:src|href)="\/([^"#]+)(?:#[^"]*)?"/g))
    assert.ok(fs.existsSync(path.join(root, match[1])), `Packaged asset exists: ${match[1]}`);
});

const sample = (seconds, changes = {}) => ({ at: new Date(seconds * 1000).toISOString(), totalCpu: 70, externalCpu: 30,
  totalGpu: null, totalRam: 1000, freeRam: 400,
  devices: [{ id: 'integrated', integrated: true, totalBytes: 200, usedBytes: 100 },
    { id: 'a', integrated: false, totalBytes: 100, usedBytes: 25 },
    { id: 'b', integrated: false, totalBytes: 200, usedBytes: null }], ...changes });

test('history is earned, ordered, timestamp-deduplicated and bounded', () => {
  const history = new ResourceHistory();
  assert.deepEqual(history.series('cpu'), []);
  assert.equal(history.add(null), false);
  history.add(sample(0));
  assert.equal(history.add(sample(0)), false);
  assert.equal(history.add(sample(-1)), false);
  for (let i = 1; i <= 400; i++) history.add(sample(i * 5));
  assert.equal(history.samples.length, 120);
  assert.ok(history.samples[0].at >= history.samples.at(-1).at - 600000);
});

test('totals, unavailable values, GPU ownership and pause/failure gaps remain truthful', () => {
  const history = new ResourceHistory();
  history.add(sample(0));
  assert.equal(history.series('cpu')[0].value, 70);
  assert.equal(history.series('gpu')[0].value, null);
  assert.equal(history.series('ram')[0].value, 60);
  assert.equal(history.series('vram', 'a')[0].value, 25);
  assert.equal(history.series('vram', 'b')[0].value, null);
  assert.equal(history.series('vram', 'integrated')[0].value, null);
  history.pause(); history.add(sample(5));
  assert.equal(history.series('cpu')[1].gap, true);
  history.fail('Offline');
  assert.equal(history.error, 'Offline');
  assert.equal(history.samples.length, 2);
  history.add(sample(10));
  assert.equal(history.series('cpu')[2].gap, true);
  assert.equal(history.error, null);
  history.add(sample(100));
  assert.equal(history.series('cpu')[3].gap, true);
});


test('freshness uses observation time and configured sampling interval, including repeated old observations', () => {
  const history = new ResourceHistory();
  assert.equal(history.status(1000, 2).stale, true);
  history.add(sample(10));
  assert.equal(history.status(25000, 2).stale, false);
  assert.equal(history.status(25001, 2).stale, true);
  assert.equal(history.status(25001, 20).stale, false);
  history.fail('Disconnected');
  history.add(sample(10));
  assert.equal(history.status(26000, 2).stale, true);
  assert.equal(history.error, null);
  history.add(sample(27));
  assert.equal(history.series('cpu').at(-1).gap, true);
});
