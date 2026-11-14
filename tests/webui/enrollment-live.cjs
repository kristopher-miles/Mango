// Run explicitly against the built Windows host; creates and stops two disposable installations.
const assert = require('node:assert/strict');
const { spawn, execFileSync } = require('node:child_process');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const net = require('node:net');
const { connectNetwork } = require('../../src/Mango/wwwroot/enrollment.js');
const dotnet = process.env.DOTNET || 'dotnet';
const dll = path.resolve(process.argv[2] || 'tests/Mango.Tests/bin/Debug/net10.0-windows/Mango.dll');
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function port() {
  const server = net.createServer();
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const value = server.address().port;
  await new Promise(resolve => server.close(resolve));
  return value;
}
(async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'Mango-enrollment-ui-'));
  const hosts = [];
  try {
    for (const name of ['creator', 'joiner']) {
      const directory = path.join(root, name), peerPort = await port();
      const child = spawn(dotnet, [dll, '--headless', '--data-dir', directory, '--port', String(await port()), '--peer-port', String(peerPort)], { windowsHide: true, stdio: 'ignore' });
      hosts.push({ child, directory, peerPort });
      let url;
      for (let attempt = 0; attempt < 30; attempt++) {
        try { url = new URL(execFileSync(dotnet, [dll, '--url', '--data-dir', directory], { windowsHide: true, stdio: ['ignore', 'pipe', 'ignore'], timeout: 3000 }).toString().trim()); break; }
        catch { await pause(250); }
      }
      assert.ok(url, `${name} started`);
      const host = hosts.at(-1), token = new URLSearchParams(url.hash.slice(1)).get('session');
      host.request = async (route, body) => {
        const headers = { Authorization: `Bearer ${token}` };
        if (host.admin) headers['Mango-Admin'] = host.admin;
        if (body != null) headers['Content-Type'] = 'application/json';
        const response = await fetch(`${url.origin}/api/local/${route}`, { method: body == null ? 'GET' : 'PUT', headers, body: body == null ? undefined : JSON.stringify(body) });
        const result = response.status === 204 ? undefined : await response.json();
        if (!response.ok) throw Object.assign(new Error(result?.error || `HTTP ${response.status}`), { status: response.status, code: result?.code });
        return result;
      };
    }
    const [creator, joiner] = hosts;
    const fields = { name: 'Creator fixture', password: 'Disposable UI enrollment 2026!', role: 'leech', benchmarkPolicy: 'never' };
    const created = await connectNetwork('create', { ...fields }, creator.request);
    assert.ok(created.session?.token, created.unlockError?.message);
    assert.ok(Date.parse(created.session.expiresAt) > Date.now());
    creator.admin = created.session.token;
    assert.equal((await creator.request('network')).unlocked, true);
    await creator.request('network/queue', { waitSeconds: 301, expectedWaitSeconds: 300 });
    const network = await creator.request('network');
    const address = network.tailnet.addresses.find(value => !new URL(value).hostname.includes(':'));
    assert.ok(address, 'Live IPv4 Tailscale address is available');
    const joined = await connectNetwork('join', { ...fields, name: 'Joiner fixture', address, network: created.setup.identity.id }, joiner.request);
    assert.ok(joined.session?.token, joined.unlockError?.message);
    joiner.admin = joined.session.token;
    assert.equal((await joiner.request('network')).unlocked, true);
    const wait = (await joiner.request('network')).queueWaitSeconds;
    await joiner.request('network/queue', { waitSeconds: 302, expectedWaitSeconds: wait });
    assert.equal((await joiner.request('network')).queueWaitSeconds, 302);
    await joiner.request('network/lock', {});
    await assert.rejects(joiner.request('network/queue', { waitSeconds: 303 }), error => error.code === 'admin_required');
    console.log('Live frontend enrollment helper: Create and Join automatically unlocked; immediate protected queue edits and server-side lock passed. Two processes on one physical Windows host.');
  } finally {
    for (const host of hosts) {
      try { execFileSync(dotnet, [dll, '--quit', '--data-dir', host.directory], { windowsHide: true, stdio: 'ignore', timeout: 5000 }); }
      catch { host.child.kill(); }
    }
  }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
