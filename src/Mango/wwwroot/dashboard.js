"use strict";

const requestsStatus = document.querySelector("#requests-status");
const requestsResources = document.querySelector("#requests-resources");
const ingressActivity = document.querySelector("#ingress-activity");
const servingActivity = document.querySelector("#serving-activity");
const publicForm = document.querySelector("#public-form");
const publicState = document.querySelector("#public-state");
const publicCheck = document.querySelector("#public-check");
const publicFeedback = document.querySelector("#public-feedback");
let dashboard;
let dashboardRetry;

const attemptStates = { waiting: "Queued", running: "Running", rejected: "Rejected", stopped: "Stopped",
  failed: "Failed", uncertain: "Stop unconfirmed", completed: "Completed" };

function requestSource(source) {
  if (!source?.address) return "Source unknown";
  const prefix = { proxyObserved: "Proxy observed", trustedProxy: "Client via trusted proxy", direct: "Direct client" }[source.provenance] || "Observed";
  return `${prefix} ${source.address}`;
}

function requestDurations(record) {
  const time = [];
  if (record.queueMilliseconds != null) time.push(`Wait ${(record.queueMilliseconds / 1000).toFixed(2)} s`);
  if (record.loadMilliseconds != null) time.push(`Load ${(record.loadMilliseconds / 1000).toFixed(2)} s`);
  return time.join(" · ");
}

function renderRequests(view) {
  const total = `${view.waiting} waiting · ${view.running} running on this host`;
  if (requestsStatus.textContent !== total) requestsStatus.textContent = total;
  const resource = view.localResources;
  requestsResources.textContent = resource ? [stateNames[resource.state], resource.reason].filter(Boolean).join(" · ") : "";
  const received = [...view.ingress, ...[...view.recent].reverse()];
  ingressActivity.replaceChildren(...(received.length ? received.map(request => {
    const row = element("li");
    row.append(element("strong", `${request.alias} · ${operationNames[request.operation]} · ${request.priority} priority`));
    row.append(element("p", [request.keyName, requestSource(request.source), request.id].filter(Boolean).join(" · "), "muted"));
    if (request.reason) row.append(element("p", request.reason));
    if (!request.attempts.length && !request.finished) row.append(element("p", "Finding an eligible placement."));
    if (request.attempts.length) {
      const attempts = element("ol", undefined, "request-attempts");
      request.attempts.forEach((attempt, index) => {
        const node = memberNames.get(attempt.node) || (attempt.node === "00000000-0000-0000-0000-000000000000" ? "This host" : attempt.node);
        const state = attempt.state === "waiting" ? "Waiting for serving admission or output" : attemptStates[attempt.state];
        attempts.append(element("li", [index ? "Rerouted" : "Selected", node, state, attempt.reason,
          requestDurations(attempt)].filter(Boolean).join(" · ")));
      });
      row.append(attempts);
    }
    return row;
  }) : [element("li", "No recent requests received here.")]));
  servingActivity.replaceChildren(...(view.serving.length ? view.serving.map(attempt => {
    const row = element("li");
    row.append(element("strong", `${attempt.alias} · ${attempt.loading ? "Loading" : attemptStates[attempt.state]} · ${attempt.priority} priority`));
    row.append(element("p", [attempt.reason, requestDurations(attempt)].filter(Boolean).join(" · ")));
    row.append(element("p", `${requestSource(attempt.source)} · ${attempt.request}`, "muted"));
    return row;
  }) : [element("li", "No recent attempts served here.")]));
}

async function watchDashboard() {
  if (dashboard || document.visibilityState !== "visible" || !token) return;
  const controller = new AbortController();
  dashboard = controller;
  let reconnect = true;
  try {
    const response = await fetch("/api/local/network/events", {
      headers: { Authorization: `Bearer ${token}` }, signal: controller.signal, cache: "no-store"
    });
    if (!response.ok || !response.body) {
      reconnect = response.status !== 401 && response.status !== 429;
      throw new Error(response.status === 429 ? "Close another Mango dashboard to connect." : "The live feed is unavailable.");
    }
    let pending = "";
    const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
    while (true) {
      const part = await reader.read();
      if (part.done) break;
      pending += part.value;
      for (let end = pending.indexOf("\n"); end >= 0; end = pending.indexOf("\n")) {
        const frame = JSON.parse(pending.slice(0, end));
        pending = pending.slice(end + 1);
        if (frame.network) loadNetwork().catch(error => networkFeedback.textContent = error.message);
        if (frame.requests) renderRequests(frame.requests);
      }
    }
  } catch (error) {
    if (!controller.signal.aborted) requestsStatus.textContent = error.message;
  } finally {
    if (dashboard === controller) {
      dashboard = null;
      if (reconnect && !controller.signal.aborted && document.visibilityState === "visible")
        dashboardRetry = setTimeout(watchDashboard, 2000);
    }
  }
}

function stopDashboard() {
  clearTimeout(dashboardRetry);
  dashboard?.abort();
  dashboard = null;
}

addEventListener("pagehide", stopDashboard);
addEventListener("pageshow", watchDashboard);
document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible") {
    watchDashboard();
    if (token) loadNetwork().catch(error => networkFeedback.textContent = error.message);
  } else stopDashboard();
});

async function loadPublic() {
  const view = await api("public-ingress");
  const fields = publicForm.elements;
  fields.enabled.checked = view.settings.enabled;
  fields.port.value = view.settings.port;
  fields.httpsOrigin.value = view.settings.httpsOrigin || "";
  fields.trustedProxy.value = view.settings.trustedProxy || "";
  publicCheck.disabled = !view.listening || !view.settings.httpsOrigin;
  const state = view.listening ? `Local upstream listening at ${view.upstream}.` :
    view.settings.enabled ? "Public upstream is configured and could not listen." : "Public upstream is off.";
  const check = !view.lastCheck ? "HTTPS reachability has not been checked from this node." :
    `HTTPS ${view.lastCheck.reached ? "reached" : "not verified"} from this node at ${timeLabel(view.lastCheck.at)}.`;
  publicState.textContent = [state, view.error, view.settings.enabled ? check : ""].filter(Boolean).join(" ");
}

publicForm.addEventListener("submit", event => {
  event.preventDefault();
  const fields = publicForm.elements;
  submit(publicForm, publicFeedback, async () => {
    await api("public-ingress", { enabled: fields.enabled.checked, port: fields.port.valueAsNumber,
      httpsOrigin: fields.httpsOrigin.value, trustedProxy: fields.trustedProxy.value });
    await loadPublic();
  });
});

publicCheck.addEventListener("click", async () => {
  publicCheck.disabled = true;
  publicFeedback.classList.remove("error");
  publicFeedback.textContent = "Checking the HTTPS endpoint from this node…";
  try {
    const check = await api("public-ingress/check", {}, "POST");
    await loadPublic();
    publicFeedback.textContent = check.reached ? "The HTTPS endpoint reached this Mango upstream." :
      "The HTTPS endpoint could not be verified. Check the proxy and certificate.";
  } catch (error) {
    publicFeedback.classList.add("error");
    publicFeedback.textContent = error.message;
    publicCheck.disabled = false;
  }
});

if (token) {
  watchDashboard();
  loadPublic().catch(error => publicState.textContent = error.message);
}
