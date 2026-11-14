const { test } = require('node:test');
const assert = require('node:assert/strict');
const { PreferenceWriter } = require('../../src/Mango/wwwroot/preferences.js');
const deferred = () => { let resolve; const promise = new Promise(r => resolve = r); return { promise, resolve }; };

test('rapid changes preserve the latest intent, including a return to the initial value', async () => {
  let server = { enabled: false, count: 2 };
  const first = deferred();
  const calls = [];
  const writer = new PreferenceWriter({ value: server, write: async ({ changes }) => {
    calls.push(changes);
    if (calls.length === 1) await first.promise;
    return server = { ...server, ...changes };
  }});
  writer.change('enabled', true);
  const saving = writer.flush();
  writer.change('enabled', false);
  writer.change('count', 3);
  writer.change('count', 4);
  writer.refresh({ enabled: true, count: 99 });
  assert.equal(writer.value.count, 4);
  first.resolve();
  await saving;
  await writer.flush();
  assert.deepEqual(server, { enabled: false, count: 4 });
  assert.equal(calls.length, 2);
  assert.equal(writer.pending, false);
});

test('an invalid field does not stop a valid independent preference', async () => {
  let server = { count: 2, enabled: false };
  const writer = new PreferenceWriter({ value: server, write: async ({ changes }) => server = { ...server, ...changes } });
  writer.change('count', '', { valid: false });
  writer.change('enabled', true);
  await writer.flush();
  assert.deepEqual(server, { count: 2, enabled: true });
  assert.equal(writer.value.count, '');
  assert.equal(writer.state.kind, 'incomplete');
  writer.change('count', 5);
  await writer.flush();
  assert.equal(server.count, 5);
});

test('overlapping conflicts require a deliberate choice and keep the draft', async () => {
  let server = { alias: 'other', context: 2048 };
  const writer = new PreferenceWriter({ value: { alias: 'before', context: 2048 }, read: async () => server,
    write: async ({ changes, expected }) => {
      if (server.alias !== expected.alias) throw Object.assign(new Error('conflict'), { status: 409, code: 'conflict' });
      return server = { ...server, ...changes };
    } });
  writer.change('alias', 'mine');
  await writer.flush();
  assert.equal(writer.state.kind, 'conflict');
  assert.equal(writer.value.alias, 'mine');
  assert.equal(writer.baseline.alias, 'other');
  await writer.resolve(false);
  assert.equal(server.alias, 'mine');
});

test('disjoint revision conflict rebases and retries without overwriting the other edit', async () => {
  let server = { alias: 'before', context: 4096, revision: 2 };
  const writer = new PreferenceWriter({ value: { alias: 'before', context: 2048, revision: 1 }, read: async () => server,
    write: async ({ changes, baseline }) => {
      if (baseline.revision !== server.revision) throw Object.assign(new Error('conflict'), { status: 409, code: 'conflict' });
      return server = { ...server, ...changes, revision: 3 };
    } });
  writer.change('alias', 'mine');
  await writer.flush();
  await writer.flush();
  assert.deepEqual(server, { alias: 'mine', context: 4096, revision: 3 });
});

test('failed and locked writes are retained, paused queues send nothing until resumed', async () => {
  let attempts = 0;
  const writer = new PreferenceWriter({ value: { alias: 'old' }, write: async ({ changes }) => {
    if (++attempts === 1) throw new Error('Disconnected');
    return { alias: changes.alias };
  } });
  writer.change('alias', 'new');
  await writer.flush();
  assert.equal(writer.state.kind, 'error');
  assert.equal(writer.pending, true);
  writer.pause();
  await writer.flush();
  assert.equal(attempts, 1);
  await writer.resume();
  assert.equal(writer.pending, false);
  assert.equal(writer.baseline.alias, 'new');
});

test('a busy response and a synchronous failure do not produce an automatic retry loop', async () => {
  let attempts = 0;
  const writer = new PreferenceWriter({ value: { count: 1 }, read: async () => ({ count: 1 }), write: () => {
    attempts++;
    throw Object.assign(new Error('Wait for active work'), { status: 409 });
  } });
  writer.change('count', 2);
  await writer.flush();
  assert.equal(attempts, 1);
  assert.equal(writer.running, null);
  assert.equal(writer.state.kind, 'error');
  assert.equal(writer.value.count, 2);
});


test('changed hardware requires review even when the edited field itself is unchanged', async () => {
  let calls = 0;
  const current = { units: 10, configuration: 'new-device', revision: 2 };
  const writer = new PreferenceWriter({ value: { units: 10, configuration: 'old-device', revision: 1 }, reviewKeys: ['configuration'], read: async () => current,
    write: async ({ changes, baseline }) => {
      calls++;
      if (baseline.revision !== current.revision) throw Object.assign(new Error('Limits changed'), { status: 409, code: 'conflict' });
      return { ...current, ...changes };
    } });
  writer.change('units', 20);
  await writer.flush();
  assert.equal(calls, 1);
  assert.equal(writer.state.kind, 'conflict');
  assert.equal(writer.value.units, 20);
  assert.equal(writer.baseline.configuration, 'new-device');
  await writer.resolve(true);
  assert.equal(calls, 1);
  assert.equal(writer.value.units, 10);
  assert.equal(writer.pending, false);
});
