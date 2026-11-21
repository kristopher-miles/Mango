const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('Overview copies a bound inference URL and opens Keys without unlocking', async () => {
  const nodes = new Map(), copied = [], routes = [];
  const get = selector => {
    if (!nodes.has(selector)) nodes.set(selector, { textContent: '', handlers: {},
      addEventListener(type, handler) { this.handlers[type] = handler; } });
    return nodes.get(selector);
  };
  const context = vm.createContext({ document: { querySelector: get, addEventListener() {} }, addEventListener() {},
    navigator: { clipboard: { writeText: async url => copied.push(url) } }, navigate: route => routes.push(route) });
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../../src/Mango/wwwroot/dashboard.js'), 'utf8'), context);
  const address = 'http://100.64.0.1:53781', ipv6 = 'http://[fd7a:115c:a1e0::1]:53781';
  const network = { localNode: { address }, tailnet: { addresses: [ipv6, address] } };
  const button = get('#overview-url-copy'), url = get('#overview-url');
  context.renderInferenceAddress({ ...network, localNode: null });
  assert.equal(get('#overview-inference').hidden, true);
  context.renderInferenceAddress({ ...network, reenroll: true });
  assert.equal(get('#overview-inference').hidden, true);
  context.renderInferenceAddress(network);
  assert.equal(get('#overview-inference').hidden, false);
  assert.equal(button.hidden, false);
  await button.handlers.click({ detail: 1 });
  assert.deepEqual(copied, [`${address}/v1`]);
  assert.equal(get('#overview-url-feedback').textContent, 'URL copied.');
  assert.deepEqual(routes, []);
  await button.handlers.click({ detail: 2 });
  button.handlers.dblclick();
  assert.equal(copied.length, 1);
  assert.deepEqual(routes, ['network/keys']);
  context.renderInferenceAddress({ ...network, tailnet: { addresses: [ipv6] } });
  assert.equal(url.textContent, `${ipv6}/v1`);
  await button.handlers.click({ detail: 0 });
  assert.equal(copied.at(-1), `${ipv6}/v1`);
  context.renderInferenceAddress({ ...network, tailnet: { addresses: [] } });
  assert.equal(button.hidden, true);
  assert.equal(url.textContent, '');
  assert.equal(get('#overview-url-offline').hidden, false);
});

test('logging writes editable policy fields and its guard, never computed read metadata', async () => {
  const node = { addEventListener() {} };
  let writer, sent;
  const initial = { mode: 'sparse', operational: true, timeZone: 'UTC', rawDays: 7, aggregateDays: 30,
    capMiB: 1024, revision: { time: 1, writer: 'fixture' }, valid: true };
  const context = {
    document: { querySelector: () => node, addEventListener() {} }, addEventListener() {},
    recordWriter: (_, options) => { writer = options; return {}; },
    // Stop at binding: this check exercises the actual domain write callback, not a synthetic DOM.
    bindPreferences() { throw new Error('writer captured'); },
    api: async (_, body) => { if (body) sent = body; return { settings: { ...initial, rawDays: 8 } }; }
  };
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../../src/Mango/wwwroot/logs.js'), 'utf8'), context);
  assert.throws(() => context.logRender({ settings: initial }), /writer captured/);
  const accepted = await writer.write({ baseline: initial, changes: { rawDays: 8 } });
  assert.equal(accepted.rawDays, 8);
  assert.equal(sent.rawDays, 8);
  assert.equal(sent.valid, undefined);
  assert.equal(sent.revision, undefined);
  assert.deepEqual(Object.keys(sent).sort(), ['mode', 'operational', 'timeZone', 'rawDays', 'aggregateDays', 'capMiB', 'expectedRevision'].sort());
  assert.deepEqual(sent.expectedRevision, initial.revision);
});

test('changing to a provider without models clears the previous qualification and its feedback binding', () => {
  const nodes = new Map();
  const get = selector => {
    if (!nodes.has(selector)) nodes.set(selector, { textContent: 'Previous model', dataset: { feedback: 'old' },
      addEventListener() {}, replaceChildren() { this.textContent = ''; } });
    return nodes.get(selector);
  };
  get('#resource-form').elements = { target: { addEventListener() {} } };
  const oldWriter = { bindings: [{}] };
  const context = { document: { querySelector: get, addEventListener() {} }, ResourceHistory: class {},
    servingNode: 'local', servingModel: null, localOwner: () => true, oldWriter };
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../../src/Mango/wwwroot/resources.js'), 'utf8'), context);
  vm.runInContext('budgetWriter = oldWriter; resourceView = { models: [] }; renderQualification();', context);
  assert.equal(get('#resource-form').hidden, true);
  assert.equal(get('#qualification-title').textContent, 'Model qualification');
  assert.equal(get('#resource-feedback').textContent, '');
  assert.equal(get('#resource-feedback').dataset.feedback, undefined);
  assert.equal(oldWriter.bindings.length, 0);
});

test('a peer deployment sharing a local ID shows its advertised slots and never binds the local budget', () => {
  const nodes = new Map();
  const get = selector => {
    if (!nodes.has(selector)) nodes.set(selector, { dataset: {}, textContent: '', addEventListener() {},
      append(node) { this.textContent += node.textContent; }, replaceChildren() {} });
    return nodes.get(selector);
  };
  get('#resource-form').elements = { target: { addEventListener() {} } };
  const item = { id: 'same', observed: { displayName: 'Peer model', workloads: [
    { limit: { operation: 'images', units: 1024, items: 2, mediaItems: 0 }, slots: 1 }
  ] } };
  const context = { document: { querySelector: get, addEventListener() {} }, ResourceHistory: class {},
    servingNode: 'peer', servingModel: item.id, localOwner: () => false, servingModels: () => [item],
    inventoryName: value => value.observed.displayName, networkView: { nodes: [{ id: 'peer', name: 'Peer host' }] },
    operationNames: { images: 'Images' }, element: (_, textContent) => ({ textContent }) };
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../../src/Mango/wwwroot/resources.js'), 'utf8'), context);
  vm.runInContext('resourceView = { models: [{ id: "same", identifier: "Local model" }] }; renderQualification();', context);
  assert.equal(get('#qualification-title').textContent, 'Peer model');
  assert.match(get('#qualification-empty').textContent, /Load peaks are managed on Peer host/);
  assert.match(get('#qualification-empty').textContent, /Images · 1 slot/);
  assert.equal(get('#resource-form').hidden, true);
  assert.equal(get('#resource-retry').disabled, true);
});
