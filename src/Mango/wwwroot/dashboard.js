"use strict";

const publicForm = document.querySelector("#public-form");
const publicState = document.querySelector("#public-state");
const publicCheck = document.querySelector("#public-check");
const publicFeedback = document.querySelector("#public-feedback");
const inferenceUrl = document.querySelector("#overview-url");
const copyInferenceUrl = document.querySelector("#overview-url-copy");
const inferenceUrlFeedback = document.querySelector("#overview-url-feedback");
let dashboard;
let dashboardRetry;

function renderInferenceAddress(network) {
  const joined = network.localNode && !network.reenroll;
  const addresses = network.tailnet.addresses;
  const origin = addresses.includes(network.localNode?.address) ? network.localNode.address : addresses[0];
  const url = joined && origin ? `${origin}/v1` : "";
  document.querySelector("#overview-inference").hidden = !joined;
  document.querySelector("#overview-url-offline").hidden = !!url;
  copyInferenceUrl.hidden = !url;
  if (inferenceUrl.textContent !== url) {
    inferenceUrl.textContent = url;
    inferenceUrlFeedback.textContent = "";
  }
}
copyInferenceUrl.addEventListener("click", async event => {
  if (event.detail > 1) return;
  const url = inferenceUrl.textContent;
  try {
    await navigator.clipboard.writeText(url);
    if (inferenceUrl.textContent === url) inferenceUrlFeedback.textContent = "URL copied.";
  } catch {
    if (inferenceUrl.textContent === url) inferenceUrlFeedback.textContent = "Could not copy the URL.";
  }
});
copyInferenceUrl.addEventListener("dblclick", () => navigate("network/keys"));

function requestSource(source) {
  if (!source?.address) return "Source unknown";
  const prefix = { proxyObserved: "Proxy observed", trustedProxy: "Client via trusted proxy", direct: "Direct client" }[source.provenance] || "Observed";
  return `${prefix} ${source.address}`;
}

let overviewRequestSignature;
function renderOverviewRequests(view) {
  if (route.page !== "overview" || view.running == null) return;
  setText(document.querySelector("#overview-running"), String(view.running));
  setText(document.querySelector("#overview-waiting"), String(view.waiting));
  const recent = [...view.ingress, ...view.recent.slice(-4).reverse()].slice(0, 4).map(request => [
    request.alias, `${operationNames[request.operation]} · ${request.finished ? "Finished" : request.attempts.at(-1)?.state || "Finding capacity"}${request.reason ? ` · ${request.reason}` : ""}`]);
  const signature = JSON.stringify(recent);
  if (overviewRequestSignature === signature) return;
  overviewRequestSignature = signature;
  document.querySelector("#overview-activity").replaceChildren(...(recent.length ? recent.map(([name, description]) => {
    const row = element("li");
    row.append(element("strong", name), element("p", description, "muted"));
    return row;
  }) : [element("li", "No recent work.", "muted")]));
}
document.addEventListener("mango:route", () => renderOverviewRequests(activitySnapshot));

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
      if (part.done) throw new Error("Live feed disconnected. Reconnecting…");
      pending += part.value;
      for (let end = pending.indexOf("\n"); end >= 0; end = pending.indexOf("\n")) {
        const frame = JSON.parse(pending.slice(0, end));
        pending = pending.slice(end + 1);
        if (frame.network) loadNetwork().catch(error => networkFeedback.textContent = error.message);
        if (frame.requests) {
          updateRequestActivity(frame.requests);
          renderOverviewRequests(frame.requests);
        }
      }
    }
  } catch (error) {
    if (!controller.signal.aborted) {
      activityFeedError = error.message;
      updateActivityTimers();
      requestActivityRender();
    }
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

let publicWriter;
async function loadPublic() {
  const view = await api("public-ingress");
  publicWriter = recordWriter("public", { value: view.settings, label: "Public access", page: "network/public",
    read: async () => (await api("public-ingress")).settings,
    write: async ({ baseline, changes }) => {
      await api("public-ingress", { ...baseline, ...changes, expected: baseline });
      const accepted = await api("public-ingress");
      renderPublicState(accepted);
      return accepted.settings;
    } });
  bindPreferences(publicWriter, publicForm, publicFeedback);
  renderPublicState(view);
}
function renderPublicState(view) {
  publicCheck.disabled = !view.listening || !view.settings.httpsOrigin;
  const state = view.listening ? `Local upstream listening at ${view.upstream}.` :
    view.settings.enabled ? "Public upstream is configured and could not listen." : "Public upstream is off.";
  const check = !view.lastCheck ? "HTTPS reachability has not been checked from this node." :
    `HTTPS ${view.lastCheck.reached ? "reached" : "not verified"} from this node at ${timeLabel(view.lastCheck.at)}.`;
  publicState.textContent = [state, view.error, view.settings.enabled ? check : ""].filter(Boolean).join(" ");
}

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
