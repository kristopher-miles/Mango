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
