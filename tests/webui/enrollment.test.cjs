const { test } = require('node:test');
const assert = require('node:assert/strict');
const { connectNetwork } = require('../../src/Mango/wwwroot/enrollment.js');

for (const kind of ['create', 'join']) test(`${kind} uses the entered password once for setup and immediately signs in`, async () => {
  const calls = [], fields = { name: 'Fixture', password: 'transient fixture password' };
  const result = await connectNetwork(kind, fields, async (path, input) => {
    calls.push({ path, input: structuredClone(input) });
    return path.endsWith('unlock') ? { token: 'ordinary-session', expiresAt: '2030-01-01' } : { node: 'fixture-node' };
  });
  assert.deepEqual(calls.map(call => call.path), [`network/${kind}`, 'network/unlock']);
  assert.equal(calls[1].input.password, calls[0].input.password);
  assert.equal(result.session.token, 'ordinary-session');
  assert.equal(result.setup.node, 'fixture-node');
  assert.equal(fields.password, undefined);
});

test('failed enrollment never unlocks; setup-success and unlock-failure are distinct', async () => {
  const paths = [];
  await assert.rejects(connectNetwork('join', { password: 'fixture' }, async path => {
    paths.push(path); throw Object.assign(new Error('Wrong network'), { status: 400 });
  }), /Wrong network/);
  assert.deepEqual(paths, ['network/join']);
  const result = await connectNetwork('create', { password: 'fixture' }, async path => {
    if (path.endsWith('unlock')) throw new Error('Temporarily disconnected');
    return { node: 'created', recoveryRecord: 'one-time-record' };
  });
  assert.equal(result.setup.recoveryRecord, 'one-time-record');
  assert.match(result.unlockError.message, /disconnected/);
  assert.equal(result.session, undefined);
});

test('a lost setup response checks membership and does not replay creation or fabricate recovery', async () => {
  const calls = [];
  const result = await connectNetwork('create', { password: 'fixture' }, async path => {
    calls.push(path);
    if (path.endsWith('create')) throw new Error('Response lost');
    return path === 'network' ? { localNode: { id: 'created' }, identity: { id: 'network' } } : { token: 'session' };
  });
  assert.deepEqual(calls, ['network/create', 'network', 'network/unlock']);
  assert.equal(result.interrupted, true);
  assert.equal(result.setup.recoveryRecord, undefined);
  assert.equal(result.session.token, 'session');
});
