"use strict";

const logQuery = document.querySelector("#logs-query");
const logSettings = document.querySelector("#logs-settings");
const logClear = document.querySelector("#logs-clear");
const logFeedback = document.querySelector("#logs-feedback");
const logCollect = document.querySelector("#logs-collect");
const logExport = document.querySelector("#logs-export");
let logViewVersion = 0;
let logPolicyWriter, logFilterTimer;
let logCollectionTimer;
let followToday = true;

const coverageLabels = { received: "Received", unavailable: "Unavailable", off: "Request logging off",
  error: "Collection error", beyondRetention: "Beyond retention" };

function logList(selector, items, row) {
  document.querySelector(selector).replaceChildren(...items.map(row));
}

function logUsage(row) {
  return element("li", `${row.name} · ${logCount(row.requests, "request")} · ${row.bytesIn} B in · ${row.bytesOut} B out`);
}

function logCount(value, unit) { return `${value} ${unit}${value === 1 ? "" : "s"}`; }

function logDetail(entry) {
  const row = element("tr");
  const work = [entry.kind, entry.task ? operationNames[entry.task] : "", entry.alias, entry.keyName,
    entry.source?.address ? requestSource(entry.source) : ""].filter(Boolean);
  const route = entry.routes?.at(-1);
  const destination = [route?.name || entry.nodeName, entry.deployment, entry.outcome, entry.reason,
    entry.routes?.length > 1 ? `${entry.routes.length - 1} reroutes` : "",
    entry.input?.exact === false || entry.output?.exact === false ? "Incomplete payload" : ""].filter(Boolean);
  const durations = [
    [entry.elapsedMilliseconds, "elapsed"], [entry.queueMilliseconds, "queue"],
    [entry.loadMilliseconds, "load"], [entry.firstTokenMilliseconds, "first token"], [entry.firstOutputMilliseconds, "first output"]
  ].filter(([value]) => value != null).map(([value, label]) => `${label} ${Math.round(value)} ms`);
  if (entry.tokensPerSecond != null) durations.push(`${entry.tokensPerSecond.toFixed(1)} tokens/s`);
  if (entry.audioSeconds != null) durations.push(`${entry.audioSeconds.toFixed(2)} audio s`);
  row.append(element("td", timeLabel(entry.at)), element("td", work.join(" · ")),
    element("td", destination.join(" · ")), element("td", durations.join(" · ") || "Unavailable"));
  return row;
}

function logRender(view) {
  logPolicyWriter = recordWriter("logging", { value: view.settings, label: "Logging policy", page: "system/logging",
    requiresAdmin: () => !!networkView?.identity,
    read: async () => (await api("logs")).settings,
    write: async ({ baseline, changes }) => {
      const next = { ...baseline, ...changes };
      await api("logs/settings", { mode: next.mode, operational: next.operational, timeZone: next.timeZone,
        rawDays: next.rawDays, aggregateDays: next.aggregateDays, capMiB: next.capMiB, expectedRevision: baseline.revision });
      return (await api("logs")).settings;
    } });
  bindPreferences(logPolicyWriter, logSettings, document.querySelector("#logging-feedback"));
  logSettings.querySelector("fieldset").disabled = !view.canManage;
  logClear.querySelector("fieldset").disabled = !view.canManage;
  document.querySelector("#logs-policy-help").textContent = view.canManage
    ? "Retention and logging mode are applied by each enrolled node."
    : "Use Unlock in the top bar to change the network policy.";
  document.querySelector("#logs-clear-status").textContent = !view.clear.generation
    ? "Each node clears its own retained logs when it receives the deletion."
    : !view.clear.localApplied ? "Local deletion is pending."
      : view.clear.pending.length ? `Pending acknowledgement from ${view.clear.pending.map(peer => peer.name).join(", ")}.`
        : "All currently enrolled nodes acknowledged the clear.";
  document.querySelector("#logs-storage").textContent = [
    `${(view.store.bytes / 1048576).toFixed(2)} MiB stored.`,
    view.store.shortened ? "The byte cap shortened retention." : "",
    view.store.droppedBytes || view.store.droppedRecords
      ? `${view.store.droppedRecords} records and ${view.store.droppedBytes} payload bytes dropped.` : "",
    view.store.error
  ].filter(Boolean).join(" ");
  document.querySelector("#logs-coverage-note").textContent = [
    `${view.day} · ${view.settings.timeZone}`,
    view.collection.running ? `Collecting ${view.collection.runningDay}…` :
      view.collection.collectedAt ? `Last collection ${timeLabel(view.collection.collectedAt)}` : "Local and previously received records",
    view.collection.error
  ].filter(Boolean).join(" · ");
  logList("#logs-coverage", view.collection.sources, source =>
    element("li", [source.name, coverageLabels[source.coverage], source.shortened ? "Shortened by byte cap" : "",
      source.dropped ? "Incomplete capture" : "", source.fromCache ? "Cached records available" : ""].filter(Boolean).join(" · ")));
  logList("#logs-keys", view.byKey, logUsage);
  logList("#logs-sources", view.bySource, logUsage);
  updateActivityHistory(view, logQuery.elements.key.value, logQuery.elements.source.value);
  logList("#logs-entries", view.entries.filter(entry => entry.kind === "benchmark" || entry.kind === "operation").slice(0, 80), logDetail);
  document.querySelector("#logs-count").textContent =
    `${logCount(view.requests, "client request")} · ${logCount(view.attempts, "serving attempt")}.${view.capped ? " View limited to 10,000 records. The download includes all available records." : ""}`;
  logCollect.disabled = view.collection.running;
  clearTimeout(logCollectionTimer);
  if (view.collection.running && !document.hidden)
    logCollectionTimer = setTimeout(() => loadLogs().catch(logError), 1500);
}

function logError(error) {
  logFeedback.classList.add("error");
  logFeedback.textContent = error.message;
}

async function loadLogs() {
  const version = ++logViewVersion;
  const values = new FormData(logQuery);
  if (followToday) values.delete("day");
  const query = new URLSearchParams([...values].filter(([, value]) => value));
  query.set("limit", activityLimit);
  const view = await api(`logs?${query}`);
  if (version !== logViewVersion) return;
  fillInput(logQuery.elements.day, view.day);
  logRender(view);
}

logQuery.addEventListener("submit", event => {
  event.preventDefault();
  logFeedback.classList.remove("error");
  logFeedback.textContent = "";
  loadLogs().catch(logError);
});

logQuery.addEventListener("input", event => {
  if (event.isComposing) return;
  logViewVersion++;
  if (event.target.name === "day") followToday = !event.target.value;
  activityLimit = 80;
  document.querySelector("#request-history").scrollTop = 0;
  clearTimeout(logFilterTimer);
  logFilterTimer = setTimeout(() => loadLogs().catch(logError), 350);
});

logClear.addEventListener("submit", event => {
  event.preventDefault();
  perform(event.submitter, document.querySelector("#clear-feedback"), async () => {
    await api("logs/clear", { confirm: logClear.elements.confirm.checked }, "POST");
    logClear.reset();
    await loadLogs();
    return "Clear requested. Acknowledgements are shown above.";
  });
});

logCollect.addEventListener("click", async () => {
  logCollect.disabled = true;
  logFeedback.classList.remove("error");
  logFeedback.textContent = "";
  try {
    await api("logs/collect", { day: logQuery.elements.day.value }, "POST");
    await loadLogs();
  } catch (error) {
    logError(error);
    logCollect.disabled = false;
  }
});

logExport.addEventListener("click", async () => {
  logExport.disabled = true;
  logFeedback.classList.remove("error");
  logFeedback.textContent = "Preparing the available records and retained payloads…";
  try {
    const response = await fetch(`/api/local/logs/export?day=${encodeURIComponent(logQuery.elements.day.value)}`,
      { headers: { Authorization: `Bearer ${token}` }, cache: "no-store" });
    if (!response.ok) throw new Error("Mango could not export this day.");
    const url = URL.createObjectURL(await response.blob());
    const link = element("a");
    link.href = url;
    link.download = `mango-logs-${logQuery.elements.day.value}.zip`;
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 30000);
    logFeedback.textContent = "Download ready.";
  } catch (error) {
    logError(error);
  } finally {
    logExport.disabled = false;
  }
});

addEventListener("pagehide", () => clearTimeout(logCollectionTimer));
document.addEventListener("mango:administration", () => {
  const locked = !!networkView?.identity && !administration.token;
  logSettings.querySelector("fieldset").disabled = locked;
  logClear.querySelector("fieldset").disabled = locked;
});
