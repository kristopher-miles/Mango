const { test } = require('node:test');
const assert = require('node:assert/strict');
const { matchingModels, inventoryName } = require('../../src/Mango/wwwroot/model-inventory.js');

test('the unfiltered inventory includes every deployment, including peers without observations', () => {
  const models = Array.from({ length: 50 }, (_, i) => ({ id: 'deployment-' + i }));
  models[0].desired = { alias: 'Peer draft' };
  assert.deepEqual(matchingModels(models, '', '', []).map(row => row.item), models);
  assert.equal(inventoryName(models[1]), 'deployment-1');
  assert.equal(matchingModels(models, 'peer draft', 'unknown', [])[0].item, models[0]);
  assert.equal(matchingModels(models, 'deployment-49', '', [])[0].item, models[49]);
});

test('view filters combine reported operations, input capabilities and residency across owners', () => {
  const local = { id: 'one', local: { instance: { busy: true }, model: { inputs: ['image'], operations: ['chat'], tools: true } } };
  const peer = { id: 'two', observed: { loaded: true, operations: ['images'], clientTools: true, providerIdentifiers: ['Peer/Native'] } };
  const unloaded = { id: 'three', local: { model: { operations: ['chat'] } } };
  const models = [local, peer, unloaded], before = structuredClone(models);
  const ids = (query, mode, filters) => matchingModels(models, query, mode, filters).map(row => row.item.id);
  assert.deepEqual(ids('', 'loaded', ['tools']), ['one', 'two']);
  assert.deepEqual(ids('', 'loaded', ['tools', 'imageInput']), ['one']);
  assert.deepEqual(ids('peer/native', '', ['images']), ['two']);
  assert.deepEqual(ids('', 'unloaded', ['chat']), ['three']);
  assert.deepEqual(models, before);
});
