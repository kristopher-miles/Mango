const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

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
