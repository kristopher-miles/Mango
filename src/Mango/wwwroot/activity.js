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

let activitySnapshot = { ingress: [], recent: [], serving: [] };
let activityLogs, activityLimit = 80, activityLoadingMore = false;
let activityObserved = Date.now(), activityReceived = 0;
let activityFeedError = "";
const activityNodes = new Map();

function updateRequestActivity(view) {
  activitySnapshot = view;
  activityObserved = Date.parse(view.observedAt) || Date.now();
  activityReceived = performance.now();
  activityFeedError = "";
  if (route.page === "activity") renderActivity();
}
function updateActivityHistory(view, key, source) {
  activityLogs = { entries: view.entries, start: Date.parse(view.start), end: Date.parse(view.end), day: view.day,
    zone: view.settings.timeZone, more: view.moreRequests, key: key.toLowerCase(), source: source.toLowerCase() };
  if (route.page === "activity") renderActivity();
}
function updateActivityTimers() {
  if (document.hidden || route.page !== "activity") return;
  const now = activityObserved + (activityFeedError ? 0 : performance.now() - activityReceived);
  for (const cell of document.querySelectorAll("#active-requests [data-arrived]"))
    cell.textContent = activityDuration(Math.max(0, now - Number(cell.dataset.arrived)));
}
function fillActivityRow(row, item, formatTime) {
  const [icon, state] = activityStates[item.state] || ["unknown", "Unknown state"];
  const marker = element("span", null, "request-state");
  marker.append(glyph(icon, state));
  marker.title = state;
  const time = formatTime.format(new Date(item.started));
  const count = n => n == null ? "—" : n.toLocaleString();
  const values = [time, item.model, activityDuration(item.total), activityDuration(item.first), count(item.prompt),
    count(item.output), item.rate == null ? "—" : (item.estimated ? "≈" : "") + item.rate.toFixed(1), activityCache(item.cached, item.prompt)];
  const cells = values.map((value, i) => {
    const cell = element("span", value);
    cell.dataset.label = activityColumns[i + 1];
    return cell;
  });
  if (item.live) cells[2].dataset.arrived = Date.parse(item.started);
  cells[1].title = item.model;
  cells[4].title = item.prompt == null ? "Input token count unavailable" : count(item.prompt) + " input tokens, including reused prompt tokens";
  cells[5].title = item.output == null ? "Output token count unavailable" : count(item.output) + " output tokens";
  cells[6].title = item.estimated ? "Estimated from the first and last generated output" : "Provider generation rate";
  cells[7].title = item.cached == null ? "Cache usage unavailable" : count(item.cached) + " cached prompt tokens";
  const summary = row.firstElementChild;
  summary.replaceChildren(marker, ...cells);
  summary.setAttribute("aria-label", [state, ...values.map((v, i) => activityColumns[i + 1] + " " + v)].join(", "));
  row.dataset.state = item.state;
  const detail = row.lastElementChild;
  detail.replaceChildren(element("strong", state), element("span", [item.reason, item.status && "HTTP " + item.status,
    item.key, item.priority && item.priority + " priority", requestSource(item.source)].filter(Boolean).join(" · ")));
  const tokens = [item.prompt != null && count(item.prompt) + " input tokens", item.cached != null && count(item.cached) + " cached",
    item.output != null && count(item.output) + " output tokens"].filter(Boolean);
  if (tokens.length) detail.append(element("span", tokens.join(" · ")));
  for (const [i, r] of item.routes.entries()) {
    const name = r.name || r.nodeName || memberNames.get(r.node) || (r.node && !/^0{8}-/.test(r.node) ? r.node : "This host");
    const wait = r.queueMilliseconds == null ? null : Math.max(0, r.queueMilliseconds - (r.loadMilliseconds ?? 0));
    detail.append(element("span", [item.routes.length > 1 && "Attempt " + (i + 1), name, r.deployment, r.state || r.outcome,
      wait != null && "Queue " + activityDuration(wait), r.loadMilliseconds != null && "Load " + activityDuration(r.loadMilliseconds),
      r.elapsedMilliseconds != null && "Serving time " + activityDuration(r.elapsedMilliseconds),
      r.reason].filter(Boolean).join(" · ")));
  }
  detail.append(element("span", "Request " + item.id, "muted"));
}
function renderActivity() {
  const active = document.querySelector("#active-requests"), history = document.querySelector("#request-history");
  const rows = activityRows(activitySnapshot, activityLogs?.entries || []);
  const live = rows.filter(row => row.live).sort((a, b) => Date.parse(a.started) - Date.parse(b.started));
  const done = rows.filter(row => {
    if (row.live) return false;
    if (!activityLogs) return true;
    const at = Date.parse(row.started);
    return at >= activityLogs.start && at < activityLogs.end && (!activityLogs.key || row.key?.toLowerCase().includes(activityLogs.key)) &&
      (!activityLogs.source || row.source?.address?.toLowerCase().includes(activityLogs.source));
  }).sort((a, b) => Date.parse(b.finished || b.started) - Date.parse(a.finished || a.started));
  const formatTime = new Intl.DateTimeFormat(undefined, { timeZone: activityLogs?.zone, hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" });
  const wanted = new Set([...live, ...done].map(row => row.id));
  const top = history.getBoundingClientRect().top;
  const anchor = history.scrollTop && [...history.children].find(el => el.getBoundingClientRect().bottom > top);
  const anchorTop = anchor && anchor.getBoundingClientRect().top;
  for (const [id, node] of activityNodes) if (!wanted.has(id)) {node.remove(); activityNodes.delete(id);}
  function populate(parent, items, empty) {
    parent.querySelector(".request-empty")?.remove();
    let next = parent.firstElementChild;
    for (const item of items) {
      let node = activityNodes.get(item.id);
      if (!node) {
        node = element("details", null, "request-row");
        node.append(element("summary", null, "request-grid"), element("div", null, "request-detail"));
        activityNodes.set(item.id, node);
      }
      const signature = JSON.stringify([item, activityLogs?.zone]);
      if (node.activitySignature !== signature) {
        fillActivityRow(node, item, formatTime);
        node.activitySignature = signature;
      }
      if (node !== next) parent.insertBefore(node, next);
      next = node.nextElementSibling;
    }
    if (!items.length) parent.append(element("p", empty, "muted request-empty"));
  }
  populate(active, live, "No active requests.");
  populate(history, done, activityLogs ? "No requests for this day and filters." : "Loading history…");
  if (anchor?.parentElement === history) history.scrollTop += anchor.getBoundingClientRect().top - anchorTop;
  const counts = new Map();
  for (const row of live) counts.set(row.state, (counts.get(row.state) || 0) + 1);
  const status = activityFeedError || (!activityReceived ? "Connecting…" : [...counts].map(([state, n]) =>
    n + " " + activityStates[state][1].toLowerCase()).join(" · "));
  const statusNode = document.querySelector("#requests-status");
  if (statusNode.textContent !== status) statusNode.textContent = status;
  document.querySelector("#history-day").textContent = activityLogs ? activityLogs.day + " · " + activityLogs.zone : "";
  document.querySelector("#history-count").textContent = done.length + (done.length === 1 ? " request" : " requests") + (activityLogs?.more ? " · Scroll for earlier requests" : "");
  updateActivityTimers();
}
if (typeof module !== "undefined" && module.exports) module.exports = { activityRows, activityCache, activityDuration };
else {
  for (const head of document.querySelectorAll(".request-head")) {
    for (const col of activityColumns) head.append(element("span", col === "State" ? "" : col));
  }
  document.addEventListener("mango:route", () => { if (route.page === "activity") renderActivity(); });
  setInterval(updateActivityTimers, 500);
  document.querySelector("#request-history").addEventListener("scroll", async event => {
    const region = event.target;
    if (!activityLogs?.more || activityLoadingMore || region.scrollTop + region.clientHeight < region.scrollHeight - 120) return;
    activityLimit += 80;
    activityLoadingMore = true;
    try { await loadLogs(); } catch (e) { logError(e); } finally { activityLoadingMore = false; }
  });
}
