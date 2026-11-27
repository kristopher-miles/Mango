"use strict";

const activityStates = {
  waiting: ["busy", "Queued"], loading: ["download", "Loading model"], placing: ["network", "Finding capacity"],
  running: ["bolt", "Running"], streaming: ["activity", "Streaming"], completed: ["check", "Completed"],
  cancelled: ["close", "Cancelled"], stopped: ["close", "Stopped"], rejected: ["warning", "Rejected"],
  failed: ["error", "Failed"], uncertain: ["unknown", "Stop unconfirmed"]
};
const activityColumns = ["State", "Started", "Model", "Total", "First token", "In tokens", "Out tokens", "tok/s", "Prompt cache"];

// Ingress owns the client outcome. A serving attempt may finish while ingress is still rerouting.
function activityRows(snapshot, entries) {
  const groups = new Map();
  function group(id) {
    if (!groups.has(id)) groups.set(id, { id, attempts: new Map() });
    return groups.get(id);
  }
  function attempt(g, id, fields) {
    const prior = g.attempts.get(id) || {};
    g.attempts.set(id, { ...prior, ...fields });
  }
  for (const e of entries) {
    if (e.kind === "request") group(e.request || e.id).log = e;
    if (e.kind === "attempt") attempt(group(e.request || e.id), e.attempt || e.id, { log: e });
  }
  for (const s of snapshot.serving) attempt(group(s.request), s.attempt, { serving: s });
  for (const s of [...snapshot.recent, ...snapshot.ingress]) group(s.id).ingress = s;
  return [...groups.values()].map(g => {
    const { ingress, log } = g;
    const routes = ingress?.attempts?.length ? ingress.attempts : log?.routes || [];
    const last = routes.at(-1);
    const attempts = [...g.attempts.values()].sort((a, b) =>
      Date.parse(b.serving?.finished || b.log?.completedAt || b.log?.at || b.serving?.arrived) -
      Date.parse(a.serving?.finished || a.log?.completedAt || a.log?.at || a.serving?.arrived));
    const selected = last ? g.attempts.get(last.attempt) : attempts.find(a => a.serving && !a.serving.finished) || attempts[0];
    const s = selected?.serving, a = selected?.log;
    const arrival = log?.at || ingress?.arrived || s?.arrived || a?.requestArrived;
    const started = arrival || a?.at;
    const finished = log?.completedAt || ingress?.finished || s?.finished || a?.completedAt;
    const live = ingress ? !ingress.finished && !log : !log && !!s && !s.finished && !a;
    const usage = field => log?.usage?.[field] ?? ingress?.usage?.[field] ?? s?.usage?.[field] ?? a?.usage?.[field];
    // Older attempts stored provider-relative TTFT and sometimes a routing bound for input.
    const counted = record => record?.promptCountSource === "provider usage" ||
      (record?.promptCountSource === "provider" && record.task === "chat") ? record.promptTokens : null;
    const prompt = usage("promptTokens") ?? counted(log) ?? counted(a);
    const first = usage("firstTokenMilliseconds") ?? log?.firstTokenMilliseconds;
    let state = log?.outcome || last?.state || last?.outcome || s?.state || a?.outcome;
    if (live) {
      if (first != null) state = "streaming";
      else if (s?.loading) state = "loading";
      else if (s?.state === "running" || ingress?.outputStarted) state = "running";
      else state = last || s ? "waiting" : "placing";
    } else if (ingress?.cancelled || log?.outcome === "cancelled") state = "cancelled";
    else if (!state || state === "waiting" || state === "running") state = "failed";
    return {
      id: g.id, live, started, finished, state, model: log?.alias || ingress?.alias || s?.alias || a?.alias || "Unknown model",
      total: log?.elapsedMilliseconds ?? (finished && arrival ? Math.max(0, Date.parse(finished) - Date.parse(arrival)) : null),
      first, prompt, output: usage("outputTokens") ?? log?.outputTokens ?? a?.outputTokens,
      rate: usage("tokensPerSecond") ?? log?.tokensPerSecond ?? a?.tokensPerSecond, estimated: usage("estimatedRate"),
      cached: usage("cachedTokens"), key: log?.keyName || ingress?.keyName || s?.keyName || a?.keyName,
      source: log?.source || ingress?.source || s?.source || a?.source,
      priority: log?.priority || ingress?.priority || s?.priority || a?.priority,
      reason: log?.reason || ingress?.reason || last?.reason || s?.reason || a?.reason,
      status: log?.status ?? a?.status, routes: routes.length ? routes.map(r => {
        const leg = g.attempts.get(r.attempt);
        return { ...leg?.log, ...leg?.serving, ...r, queueMilliseconds: r.queueMilliseconds ?? leg?.serving?.queueMilliseconds ?? leg?.log?.queueMilliseconds,
          loadMilliseconds: r.loadMilliseconds ?? leg?.serving?.loadMilliseconds ?? leg?.log?.loadMilliseconds };
      }) : attempts.map(leg => ({ ...leg.log, ...leg.serving }))
    };
  });
}

const activityDuration = ms => ms == null ? "—" : ms < 1000 ? Math.round(ms) + " ms" : (ms / 1000).toFixed(2) + " s";
function activityCache(count, prompt) {
  if (count == null) return "—";
  if (count === 0) return "Miss";
  return prompt >= count ? (100 * count / prompt).toLocaleString(undefined, { maximumFractionDigits: 1 }) + "%" : count.toLocaleString() + " tok";
}

const emptyActivity = { ingress: [], recent: [], serving: [] };
let activitySnapshot = emptyActivity, snapshotSignature, historySignature;
let activityLogs, activityLimit = 80, activityLoadingMore = false;
let activityObserved = Date.now(), activityReceived = null, activityFeedError = "";
let activityFrame, activityTimer, activityTimeFormat, activityTimeZone;
let renderedHistory, recentHistorySignature;
const activityNodes = new Map();
const activityClocks = new Map();

function requestActivityRender() {
  if (!activityFrame && !document.hidden && route.page === "activity")
    activityFrame = requestAnimationFrame(renderActivity);
}

function updateRequestActivity(view) {
  const signature = JSON.stringify([view.ingress, view.recent, view.serving]);
  const changed = signature !== snapshotSignature || !!activityFeedError;
  snapshotSignature = signature;
  activitySnapshot = view;
  activityObserved = Date.parse(view.observedAt) || Date.now();
  activityReceived = performance.now();
  activityFeedError = "";
  if (changed) requestActivityRender();
}
function updateActivityHistory(view, key, source) {
  const entries = view.entries.filter(e => e.kind === "request" || e.kind === "attempt");
  const signature = JSON.stringify([entries, view.start, view.end, view.day, view.settings.timeZone, view.moreRequests, key, source]);
  if (signature === historySignature) return;
  historySignature = signature;
  const byRequest = new Map();
  for (const entry of entries) {
    const id = entry.request || entry.id;
    if (!byRequest.has(id)) byRequest.set(id, []);
    byRequest.get(id).push(entry);
  }
  activityLogs = { byRequest, rows: activityRows(emptyActivity, entries), start: Date.parse(view.start), end: Date.parse(view.end),
    day: view.day, zone: view.settings.timeZone, more: view.moreRequests, key: key.toLowerCase(), source: source.toLowerCase() };
  requestActivityRender();
}
function updateActivityTimers() {
  const ticking = route.page === "activity" && !document.hidden && !activityFeedError && activityClocks.size > 0;
  if (!ticking) {
    clearInterval(activityTimer);
    activityTimer = null;
    return;
  }
  if (!activityTimer) {
    const tick = () => {
      const now = activityObserved + performance.now() - activityReceived;
      for (const [cell, arrived] of activityClocks)
        setText(cell, activityDuration(Math.max(0, now - arrived)));
    };
    tick();
    activityTimer = setInterval(tick, 500);
  }
}
function fillActivityRow(row, item) {
  const summary = row.firstElementChild;
  const [icon, state] = activityStates[item.state] || ["unknown", "Unknown state"];
  const marker = summary.firstElementChild;
  if (row.dataset.state !== item.state) {
    marker.replaceChildren(glyph(icon, state));
    marker.title = state;
    row.dataset.state = item.state;
  }
  const arrived = Date.parse(item.started);
  const time = Number.isFinite(arrived) ? activityTimeFormat.format(arrived) : "—";
  const count = n => n == null ? "—" : n.toLocaleString();
  const values = [time, item.model, item.live && Number.isFinite(arrived) ? null : activityDuration(item.total), activityDuration(item.first), count(item.prompt),
    count(item.output), item.rate == null ? "—" : (item.estimated ? "≈" : "") + item.rate.toFixed(1), activityCache(item.cached, item.prompt)];
  const titles = ["", item.model, "", "", item.prompt == null ? "Input token count unavailable" : count(item.prompt) + " input tokens, including reused prompt tokens",
    item.output == null ? "Output token count unavailable" : count(item.output) + " output tokens",
    item.estimated ? "Estimated from the first and last generated output" : "Provider generation rate",
    item.cached == null ? "Cache usage unavailable" : count(item.cached) + " cached prompt tokens"];
  values.forEach((value, i) => {
    const cell = summary.children[i + 1];
    if (value !== null) setText(cell.lastChild, value);
    if (cell.title !== titles[i]) cell.title = titles[i];
  });
  const clock = summary.children[3].lastChild;
  if (item.live && Number.isFinite(arrived)) {
    if (activityClocks.get(clock) !== arrived) setText(clock, activityDuration(Math.max(0, activityObserved - arrived)));
    activityClocks.set(clock, arrived);
  }
  else activityClocks.delete(clock);
  row.activityItem = item;
  if (row.open) fillActivityDetail(row);
}
function fillActivityDetail(row) {
  const item = row.activityItem, detail = row.lastElementChild;
  const state = (activityStates[item.state] || ["unknown", "Unknown state"])[1];
  const lines = [state, [item.reason, item.status && "HTTP " + item.status, item.key, item.priority && item.priority + " priority",
    requestSource(item.source)].filter(Boolean).join(" · ")];
  const tokens = [item.prompt != null && item.prompt.toLocaleString() + " input tokens", item.cached != null && item.cached.toLocaleString() + " cached",
    item.output != null && item.output.toLocaleString() + " output tokens"].filter(Boolean);
  if (tokens.length) lines.push(tokens.join(" · "));
  for (const [i, r] of item.routes.entries()) {
    const name = r.name || r.nodeName || memberNames.get(r.node) || (r.node && !/^0{8}-/.test(r.node) ? r.node : "This host");
    const wait = r.queueMilliseconds == null ? null : Math.max(0, r.queueMilliseconds - (r.loadMilliseconds ?? 0));
    lines.push([item.routes.length > 1 && "Attempt " + (i + 1), name, r.deployment, r.state || r.outcome,
      wait != null && "Queue " + activityDuration(wait), r.loadMilliseconds != null && "Load " + activityDuration(r.loadMilliseconds),
      r.elapsedMilliseconds != null && "Serving time " + activityDuration(r.elapsedMilliseconds), r.reason].filter(Boolean).join(" · "));
  }
  lines.push("Request " + item.id);
  lines.forEach((line, index) => {
    if (!detail.children[index]) detail.append(element(index ? "span" : "strong"));
    setText(detail.children[index], line);
  });
  while (detail.children.length > lines.length) detail.lastElementChild.remove();
}
function populateActivity(parent, items, empty, removed) {
  let next = parent.firstElementChild;
  if (next?.classList.contains("request-empty")) {
    if (!items.length) { setText(next, empty); return; }
    next.remove();
    next = null;
  }
  for (const item of items) {
    let row = activityNodes.get(item.id);
    if (!row) {
      row = element("details", null, "request-row");
      const summary = element("summary", null, "request-grid");
      summary.append(element("span", null, "request-state"));
      for (const label of activityColumns.slice(1)) {
        const cell = element("span");
        cell.dataset.label = label;
        cell.append(element("span", label + " ", "sr-only"), document.createTextNode(""));
        summary.append(cell);
      }
      row.append(summary, element("div", null, "request-detail"));
      row.addEventListener("toggle", () => { if (row.open) fillActivityDetail(row); });
      activityNodes.set(item.id, row);
    }
    fillActivityRow(row, item);
    if (row !== next) parent.insertBefore(row, next);
    next = row.nextElementSibling;
  }
  while (next) {
    const unused = next;
    next = next.nextElementSibling;
    unused.remove();
    removed.push(unused);
  }
  if (!items.length) parent.append(element("p", empty, "muted request-empty"));
}
function renderActivity() {
  activityFrame = null;
  if (document.hidden || route.page !== "activity") return;
  const { ingress, recent, serving } = activitySnapshot;
  const ids = new Set([...ingress.map(r => r.id), ...recent.map(r => r.id), ...serving.map(r => r.request)]);
  const overlapping = [...ids].flatMap(id => activityLogs?.byRequest.get(id) || []);
  const rows = activityRows(activitySnapshot, overlapping);
  const live = rows.filter(row => row.live).sort((a, b) => Date.parse(a.started) - Date.parse(b.started));
  const recentDone = rows.filter(row => !row.live);
  const zone = activityLogs?.zone;
  if (!activityTimeFormat || zone !== activityTimeZone) {
    activityTimeZone = zone;
    activityTimeFormat = new Intl.DateTimeFormat(undefined, { timeZone: zone, hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" });
  }
  // A streaming token changes only the live list. Retained rows are already grouped.
  const historyKey = JSON.stringify([recentDone, live.filter(row => activityLogs?.byRequest.has(row.id)).map(row => row.id)]);
  const historyChanged = renderedHistory !== activityLogs || recentHistorySignature !== historyKey;
  const history = document.querySelector("#request-history");
  let anchor, offset;
  if (historyChanged && history.scrollTop) {
    const top = history.getBoundingClientRect().top;
    anchor = Array.from(history.children).find(el => el.getBoundingClientRect().bottom > top);
    if (anchor) offset = anchor.getBoundingClientRect().top - top;
  }
  const removed = [];
  const focus = document.activeElement;
  populateActivity(document.querySelector("#active-requests"), live, "No active requests.", removed);
  if (historyChanged) {
    const done = [...recentDone, ...(activityLogs?.rows || []).filter(row => !ids.has(row.id))].filter(row => {
      if (!activityLogs) return true;
      const at = Date.parse(row.started);
      return at >= activityLogs.start && at < activityLogs.end && (!activityLogs.key || row.key?.toLowerCase().includes(activityLogs.key)) &&
        (!activityLogs.source || row.source?.address?.toLowerCase().includes(activityLogs.source));
    }).sort((a, b) => Date.parse(b.finished || b.started) - Date.parse(a.finished || a.started));
    populateActivity(history, done, activityLogs ? "No requests for this day and filters." : "Loading history…", removed);
    if (renderedHistory === activityLogs && recentHistorySignature !== undefined && followToday) nextActivityLogPoll = 0;
    if (anchor?.parentElement === history) history.scrollTop += anchor.getBoundingClientRect().top - history.getBoundingClientRect().top - offset;
    setText(document.querySelector("#history-day"), activityLogs ? activityLogs.day + " · " + zone : "");
    setText(document.querySelector("#history-count"), done.length + (done.length === 1 ? " request" : " requests") + (activityLogs?.more ? " · Scroll for earlier requests" : ""));
    recentHistorySignature = historyKey;
    renderedHistory = activityLogs;
  }
  if (focus !== document.body && focus?.isConnected && document.activeElement === document.body) focus.focus({ preventScroll: true });
  for (const node of removed) if (!node.parentElement) {
    activityClocks.delete(node.firstElementChild.children[3].lastChild);
    activityNodes.delete(node.activityItem.id);
  }
  const counts = new Map();
  for (const row of live) counts.set(row.state, (counts.get(row.state) || 0) + 1);
  setText(document.querySelector("#requests-status"), activityFeedError || (activityReceived === null ? "Connecting…" : [...counts].map(([state, n]) =>
    n + " " + activityStates[state][1].toLowerCase()).join(" · ")));
  updateActivityTimers();
}
if (typeof module !== "undefined" && module.exports) module.exports = { activityRows, activityCache, activityDuration };
else {
  for (const head of document.querySelectorAll(".request-head"))
    for (const col of activityColumns) head.append(element("span", col === "State" ? "" : col));
  for (const event of ["mango:route", "visibilitychange"])
    document.addEventListener(event, () => { updateActivityTimers(); requestActivityRender(); });
  document.querySelector("#request-history").addEventListener("scroll", async event => {
    const region = event.target;
    if (!activityLogs?.more || activityLoadingMore || region.scrollTop + region.clientHeight < region.scrollHeight - 120) return;
    activityLimit += 80;
    activityLoadingMore = true;
    try { await loadLogs(); } catch (e) { logError(e); } finally { activityLoadingMore = false; }
  });
}
