const { test } = require("node:test");
const assert = require("node:assert/strict");
const { parseRoute } = require("../../src/Mango/wwwroot/shell.js");
const { activityRows, activityCache } = require("../../src/Mango/wwwroot/activity.js");

const started = "2026-11-27T20:00:00Z", finished = "2026-11-27T20:00:04Z";
const usage = { promptTokens: 128, outputTokens: 40, cachedTokens: 96, firstTokenMilliseconds: 500, tokensPerSecond: 12 };
const active = { id: "req", alias: "chat", arrived: started, usage, attempts: [{ attempt: "second", state: "running" }] };
const service = { request: "req", attempt: "second", arrived: started, alias: "chat", state: "running", usage };
const empty = { ingress: [], serving: [], recent: [] };

test("old Activity links share one destination", () => {
  for (const route of ["activity", "activity/live", "activity/history", "logging", "live-requests"])
    assert.equal(parseRoute("#/" + route).page, "activity");
});
test("one logical row across live ingress, service, retries and eventual logging", () => {
  const oldAttempt = { kind: "attempt", request: "req", attempt: "first", outputTokens: 990, at: started };
  const [live, duplicate] = activityRows({ ...empty, serving: [service], ingress: [active] }, [oldAttempt]);
  assert.equal(duplicate, undefined);
  assert.equal(live.state, "streaming");
  assert.equal(live.live, true);
  assert.equal(live.prompt, 128);
  assert.equal(live.output, 40);
  assert.equal(activityCache(live.cached, live.prompt), "75%");
  const terminal = { ...active, finished, attempts: [{ attempt: "second", state: "completed" }] };
  const log = { kind: "request", request: "req", at: started, completedAt: finished, outcome: "completed", elapsedMilliseconds: 4050, usage };
  const [done] = activityRows({ ...empty, recent: [terminal], serving: [{ ...service, finished }] }, [log, oldAttempt]);
  assert.equal(done.live, false);
  assert.equal(done.id, live.id);
  assert.equal(done.total, 4050);
  assert.equal(done.state, "completed");
});
test("old serving records, partials, estimates, misses and unavailable counters remain distinct", () => {
  const attempt = { id: "old", kind: "attempt", alias: "model", at: started, elapsedMilliseconds: 300,
    outcome: "stopped", promptTokens: 1024, promptCountSource: "routing estimate", outputTokens: 0 };
  const [row] = activityRows(empty, [attempt]);
  assert.equal(row.live, false);
  assert.equal(row.total, null);
  assert.equal(row.prompt, null);
  assert.equal(row.output, 0);
  assert.equal(activityCache(row.cached, row.prompt), "—");
  assert.equal(activityCache(0, 120), "Miss");
  assert.equal(activityCache(56, null), "56 tok");
});

test("Activity keeps client identity and distinguishes buffered estimates from streamed timing", () => {
  const source = { address: "100.64.0.1", provenance: "direct" };
  const ingress = { ...active, keyName: "Editor", source, outputStarted: true, usage: {} };
  const serving = { ...service, usage: { firstTokenMilliseconds: 250, estimatedFirstToken: true } };
  const [live] = activityRows({ ...empty, ingress: [ingress], serving: [serving] }, []);
  assert.equal(live.state, "running");
  assert.equal(live.key, "Editor");
  assert.deepEqual(live.source, source);
  assert.equal(live.first, 250);
  assert.equal(live.estimatedFirst, true);

  const [old] = activityRows(empty, [{ kind: "attempt", request: "req", at: started, outcome: "completed",
    firstTokenMilliseconds: 120, keyName: "Editor", source }]);
  assert.equal(old.first, 120);
  assert.equal(old.providerFirst, true);
  assert.equal(old.key, live.key);
  assert.deepEqual(old.source, source);
});

test("identical feed frames and history polls schedule no redraw, including after navigation", () => {
  const fs = require("node:fs"), vm = require("node:vm");
  const jobs = [];
  const context = vm.createContext({
    module: { exports: {} }, performance: { now: () => 1000 }, route: { page: "activity" },
    document: { hidden: false }, requestAnimationFrame: callback => jobs.push(callback)
  });
  vm.runInContext(fs.readFileSync(require.resolve("../../src/Mango/wwwroot/activity.js"), "utf8"), context);
  const history = { day: "2026-11-27", start: started, end: finished, settings: { timeZone: "UTC" }, entries: [] };
  context.updateRequestActivity({ ...empty, observedAt: started });
  context.updateActivityHistory(history, "", "");
  assert.equal(jobs.length, 1, "coalesce the two initial inputs into one frame");
  context.route.page = "overview";
  jobs.pop()();
  context.route.page = "activity";
  context.updateRequestActivity({ ...empty, observedAt: finished });
  context.updateActivityHistory(structuredClone(history), "", "");
  assert.equal(jobs.length, 0);
  context.updateRequestActivity({ ...empty, ingress: [active] });
  assert.equal(jobs.length, 1);
  context.document.hidden = true;
  jobs.pop()();
  context.updateActivityHistory(history, "filtered", "");
  assert.equal(jobs.length, 0);
});

test("the elapsed clock stops for empty, hidden and disconnected activity", () => {
  const fs = require("node:fs"), vm = require("node:vm");
  const intervals = new Set();
  const cell = { textContent: "" };
  const context = vm.createContext({
    module: { exports: {} }, document: { hidden: false }, route: { page: "activity" }, cell,
    performance: { now: () => 0 }, setText: (node, value) => { node.textContent = value; },
    setInterval: callback => { intervals.add(callback); return callback; }, clearInterval: handle => intervals.delete(handle)
  });
  vm.runInContext(fs.readFileSync(require.resolve("../../src/Mango/wwwroot/activity.js"), "utf8"), context);
  context.updateActivityTimers();
  assert.equal(intervals.size, 0);
  vm.runInContext("activityObserved = 5000; activityReceived = 0; activityClocks.set(cell, 1000)", context);
  context.updateActivityTimers();
  context.updateActivityTimers();
  assert.equal(intervals.size, 1);
  assert.equal(cell.textContent, "4.00 s");
  context.document.hidden = true;
  context.updateActivityTimers();
  assert.equal(intervals.size, 0);
  context.document.hidden = false;
  vm.runInContext('activityFeedError = "disconnected"', context);
  context.updateActivityTimers();
  assert.equal(intervals.size, 0);
});
