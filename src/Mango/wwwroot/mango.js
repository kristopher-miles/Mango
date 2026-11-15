"use strict";

const fragmentToken = new URLSearchParams(location.hash.slice(1)).get("session");
if (fragmentToken !== null) {
  history.replaceState(null, "", location.pathname + location.search);
  if (/^[A-F0-9]{64}$/.test(fragmentToken)) sessionStorage.setItem("mango-session", fragmentToken);
}
const token = sessionStorage.getItem("mango-session");
addEventListener("hashchange", () => {
  const session = new URLSearchParams(location.hash.slice(1)).get("session");
  if (session === null) return;
  history.replaceState(null, "", location.pathname + location.search);
  if (/^[A-F0-9]{64}$/.test(session)) { sessionStorage.setItem("mango-session", session); location.reload(); }
});
const workspace = document.querySelector("#workspace");
const status = document.querySelector("#status");
const startupForm = document.querySelector("#startup");
const nodePortForm = document.querySelector("#node-port");
const maintenanceForm = document.querySelector("#maintenance");
const providerForm = document.querySelector("#provider-form");
const providerStatus = document.querySelector("#provider-status");
const aliasRatingForm = document.querySelector("#alias-rating-form");
const profileNames = { lmStudio: "LM Studio", unslothStudio: "Unsloth Studio", unslothRun: "Unsloth run",
  llamaSingle: "llama.cpp single model", llamaEmbedding: "llama.cpp embeddings", llamaRouter: "llama.cpp router", generic: "Generic endpoint" };
const operationNames = { chat: "Chat", embeddings: "Embeddings", images: "Images", transcription: "Transcription", speech: "Speech", systemOne: "System One" };
const servingFields = ["servedContext", "embeddingDimensions", "embeddingFamily", "mediaTokensPerItem"];
const stateNames = { contributor: "Contributing", withdrawing: "Withdrawing", cooldown: "Cooling down", manualLeech: "Contribution off", fault: "Protection needs attention" };
const benchmarkNames = { never: "Never", now: "Now", whenConvenient: "When convenient" };
const writers = new Map();
let view, settingsWriter, startupWriter;
let portConflictShown = false;
const administration = { token: null, expiresAt: null, timer: null, locking: false, pendingRevocation: null };

async function api(path, body, method = body == null ? "GET" : "PUT", administrationToken) {
  if (!token) throw new Error("Open Mango again to start a local session.");
  if (administration.token && administration.expiresAt && Date.parse(administration.expiresAt) <= Date.now()) setAdministration(null);
  const headers = { Authorization: `Bearer ${token}` };
  if (administrationToken || administration.token) headers["Mango-Admin"] = administrationToken || administration.token;
  const sentAdministration = headers["Mango-Admin"];
  if (body != null) headers["Content-Type"] = "application/json";
  const response = await fetch(`/api/local/${path}`, { method, headers,
    body: body != null ? JSON.stringify(body) : undefined, cache: "no-store" });
  if (!response.ok) {
    const details = await response.json().catch(() => ({}));
    const error = Object.assign(new Error(details.error || details.detail || (response.status === 401
      ? "Your local session has ended. Open Mango again." : `Mango returned HTTP ${response.status}.`)),
      { status: response.status, code: details.code });
    if (error.code === "admin_required" && administration.token === sentAdministration) setAdministration(null);
    if (error.code === "local_session_required" || response.status === 401 && !details.error && !details.detail) {
      for (const writer of writers.values()) writer.pause("Local session ended · reopen Mango");
      showSessionError(error);
    }
    throw error;
  }
  return response.status === 204 ? undefined : response.json();
}

function element(tag, content, className) {
  const node = document.createElement(tag);
  if (content != null) node.textContent = content;
  if (className) node.className = className;
  return node;
}
function action(label, handler) {
  const button = element("button", label, "secondary");
  button.type = "button";
  button.addEventListener("click", handler);
  return button;
}
function timeLabel(time) { return time ? new Date(time).toLocaleString() : "Unobserved"; }
function sameRevision(left, right) { return !!left && !!right && left.time === right.time && left.writer === right.writer; }
function labeledInput(label, name, type, value) {
  const wrapper = element("label", label);
  const input = element("input");
  Object.assign(input, { name, type, value: value ?? "" });
  if (type === "number") input.step = "1";
  wrapper.append(input);
  return wrapper;
}
function inputValue(input) {
  if (input.hasAttribute("data-role-switch")) return input.checked ? "contributor" : "leech";
  if (input.type === "checkbox") return input.checked;
  if (input.type === "number") return input.value === "" ? null : Number.isFinite(input.valueAsNumber) ? input.valueAsNumber : null;
  return input.value;
}
function fillInput(input, value) {
  if (document.activeElement === input) return;
  if (input.hasAttribute("data-role-switch")) input.checked = value === "contributor";
  else if (input.type === "checkbox") input.checked = !!value;
  else input.value = value ?? "";
}

function pendingSummary() {
  const pending = [...writers.values()].filter(writer => writer.pending);
  const button = document.querySelector("#pending-edits");
  button.hidden = pending.length === 0;
  button.textContent = `${pending.length} unfinished ${pending.length === 1 ? "change" : "changes"}`;
  if (document.querySelector("#pending-dialog").open) renderPending();
}
function renderPending() {
  document.querySelector("#pending-list").replaceChildren(...[...writers.values()].filter(w => w.pending).map(writer => {
    const li = element("li");
    const link = element("a", writer.label);
    link.href = `#/${writer.page}`;
    link.addEventListener("click", () => document.querySelector("#pending-dialog").close());
    li.append(link, element("p", writer.state.message, "muted"));
    return li;
  }));
}
document.querySelector("#pending-edits").addEventListener("click", () => { renderPending(); document.querySelector("#pending-dialog").showModal(); });
addEventListener("beforeunload", event => {
  if ([...writers.values()].some(w => w.pending)) { event.preventDefault(); event.returnValue = ""; }
});

function feedbackFor(writer, target) {
  if (!target) return;
  const key = `${writer.state.kind}:${writer.state.message}:${[...writer.conflicts].join(",")}`;
  if (target.dataset.feedback === key) return;
  target.dataset.feedback = key;
  target.classList.toggle("error", ["error", "conflict", "incomplete"].includes(writer.state.kind));
  target.replaceChildren(element("span", writer.state.message));
  if (writer.conflicts.size) {
    const values = [...writer.conflicts].filter(key => key !== "token").map(key => `${key}: current ${JSON.stringify(writer.baseline[key])}; entered ${JSON.stringify(writer.value[key])}`);
    target.append(element("pre", values.join("\n")), action("Use current values", () => writer.resolve(true)), action("Keep my changes", () => writer.resolve(false)));
  } else if (writer.state.kind === "error") target.append(action("Retry", () => writer.flush()));
  else if (writer.state.kind === "paused" && writer.requiresAdmin?.()) target.append(action("Unlock", openAdministration));
}
function recordWriter(key, options) {
  const protectedWrite = payload => {
    if (options.requiresAdmin?.() && !administration.token) throw Object.assign(new Error("Unlock administration to update"), { code: "admin_required" });
    return options.write(payload);
  };
  if (writers.has(key)) {
    const writer = writers.get(key);
    writer.write = protectedWrite; writer.read = options.read; writer.requiresAdmin = options.requiresAdmin;
    writer.validate = options.validate || (() => null);
    writer.reviewKeys = options.reviewKeys || [];
    writer.onChanged = options.changed;
    writer.label = options.label; writer.page = options.page;
    writer.refresh(options.value);
    return writer;
  }
  const writer = new MangoPreferences.PreferenceWriter({ ...options, write: protectedWrite, changed: current => {
    current.onChanged?.(current);
    for (const binding of current.bindings || []) {
      for (const input of binding.inputs()) {
        const name = input.dataset.setting || input.name;
        if (name && !current.invalid.has(name)) fillInput(input, current.value[name]);
        input.setAttribute("aria-invalid", String(current.invalid.has(name)));
      }
      feedbackFor(current, binding.feedback);
    }
    pendingSummary();
  } });
  writer.label = options.label; writer.page = options.page;
  writer.onChanged = options.changed;
  writer.requiresAdmin = options.requiresAdmin;
  writer.bindings = [];
  writers.set(key, writer);
  if (options.requiresAdmin?.() && !administration.token) writer.pause();
  return writer;
}
function bindPreferences(writer, root, feedback, { inputs = () => [...root.querySelectorAll("input[name],select[name],textarea[name],input[data-setting],select[data-setting]")], exclude = [] } = {}) {
  const binding = { feedback, inputs: () => inputs().filter(input => !exclude.includes(input.name)) };
  writer.bindings = writer.bindings.filter(old => old.feedback !== feedback || old.inputs()[0] !== binding.inputs()[0]);
  writer.bindings.push(binding);
  for (const input of binding.inputs()) {
    const name = input.dataset.setting || input.name;
    if (!writer.invalid.has(name)) fillInput(input, writer.value[name]);
    if (input.preferenceWriter === writer) continue;
    input.preferenceWriter = writer;
    const changed = event => {
      if (event.isComposing || input.composing) return;
      const immediate = input.type === "checkbox" || input.tagName === "SELECT";
      const valid = input.validity.valid && (!input.required || input.type === "checkbox" || String(input.value).trim().length > 0);
      writer.change(name, inputValue(input), { immediate, valid });
    };
    if (input.preferenceHandler) {
      for (const [type, handler] of input.preferenceHandler) input.removeEventListener(type, handler);
    }
    const handlers = [[input.type === "checkbox" || input.tagName === "SELECT" ? "change" : "input", changed], ["compositionstart", () => input.composing = true],
      ["compositionend", event => { input.composing = false; changed(event); }],
      ["blur", () => { if (!input.composing) writer.flush(); }],
      ["keydown", event => { if (event.key === "Enter" && !event.isComposing && input.tagName !== "TEXTAREA") { event.preventDefault(); writer.flush(); } }]];
    for (const [type, handler] of handlers) input.addEventListener(type, handler);
    input.preferenceHandler = handlers;
  }
  feedbackFor(writer, feedback);
  if (root.tagName === "FORM" && !root.preferenceSubmit) { root.addEventListener("submit", event => event.preventDefault()); root.preferenceSubmit = true; }
}
async function perform(button, feedback, work, message = "Working…") {
  button.disabled = true;
  feedback.classList.remove("error"); feedback.textContent = message;
  try { feedback.textContent = await work() || "Done."; }
  catch (error) { feedback.classList.add("error"); feedback.textContent = error.message; }
  finally { button.disabled = false; }
}
function showSessionError(error) { status.hidden = false; status.classList.add("error"); status.textContent = error.message; }

function syncLocalPreferences() {
  const settings = settingsWriter.value;
  document.querySelector("#rail-contribution").textContent = settings.role === "contributor" ? "Contribution on" : "Contribution off";
  const protection = document.querySelector("#overview-protection");
  protection.title = settings.activityMonitoring ? "Activity protection on" : "Activity protection off";
  protection.setAttribute("aria-label", protection.title);
  protection.querySelector("use").setAttribute("href", `/glyphs.svg#${settings.activityMonitoring ? "protection" : "protection-off"}`);
  protection.classList.toggle("muted", !settings.activityMonitoring);
  const reduced = !settingsWriter.baseline.reducedReserveConsent && (
    settings.ramReserveMiB != null && settings.ramReserveMiB < view.defaultRamReserveMiB ||
    settings.vramReserveMiB != null && settings.vramReserveMiB < view.defaultVramReserveMiB);
  document.querySelector("#reserve-warning").hidden = !reduced;
  const fields = document.querySelector("#settings-memory").elements;
  fields.acceptReducedReserve.required = reduced;
  for (const [name, minimum] of [["ramReserveMiB", view.defaultRamReserveMiB], ["vramReserveMiB", view.defaultVramReserveMiB]]) {
    if (!settingsWriter.dirty.has(name)) continue;
    if (!fields[name].validity.valid || !settingsWriter.baseline.reducedReserveConsent && !fields.acceptReducedReserve.checked && settings[name] != null && settings[name] < minimum)
      settingsWriter.invalid.add(name);
    else settingsWriter.invalid.delete(name);
  }
}
async function loadSettings() {
  view = await api("settings");
  renderNodePort(view.nodePort);
  settingsWriter = recordWriter("settings", { value: view.settings, label: "Local preferences", page: "protection/activity",
    read: async () => (await api("settings")).settings,
    write: async ({ changes, expected }) => {
      const accepted = await api("settings", { changes, expected,
        acceptReducedReserve: document.querySelector("#settings-memory").elements.acceptReducedReserve.checked }, "PATCH");
      view.settings = accepted;
      const actual = await api("settings");
      document.querySelector("#ram-default").textContent = `Default ${actual.defaultRamReserveMiB} MiB · Effective ${actual.effectiveRamReserveMiB} MiB`;
      document.querySelector("#vram-default").textContent = `Default ${actual.defaultVramReserveMiB} MiB · Effective ${actual.effectiveVramReserveMiB} MiB`;
      return accepted;
    },
    validate: values => !values.reducedReserveConsent && !document.querySelector("#settings-memory").elements.acceptReducedReserve.checked && (
      values.ramReserveMiB != null && values.ramReserveMiB < view.defaultRamReserveMiB ||
      values.vramReserveMiB != null && values.vramReserveMiB < view.defaultVramReserveMiB) ? "Acknowledge the reduced reserve before it takes effect." : null,
    changed: () => { if (settingsWriter) syncLocalPreferences(); }
  });
  for (const [id, feedback] of [["settings-activity", "protection-feedback"], ["settings-memory", "memory-feedback"], ["settings-benchmarks", "benchmark-feedback"]])
    bindPreferences(settingsWriter, document.querySelector(`#${id}`), document.querySelector(`#${feedback}`), { exclude: ["acceptReducedReserve"] });
  for (const input of document.querySelectorAll("[data-setting]")) {
    const feedback = input.closest("section")?.querySelector(`[data-setting-status="${input.dataset.setting}"]`);
    bindPreferences(settingsWriter, input.parentElement, feedback, { inputs: () => [input] });
  }
  maintenanceForm.elements.deferred.dataset.setting = "maintenanceDeferred";
  maintenanceForm.elements.daily.dataset.setting = "dailyUpdates";
  bindPreferences(settingsWriter, maintenanceForm, document.querySelector("#maintenance-status"));
  for (const choice of document.querySelectorAll("[data-benchmark-choice], [data-role-choice]"))
    if (!choice.dataset.changed) choice.value = choice.hasAttribute("data-role-choice") ? view.settings.role : view.settings.benchmarkPolicy;
  startupForm.hidden = !view.startupAvailable;
  document.querySelector("#startup-unavailable").hidden = view.startupAvailable;
  startupWriter = recordWriter("startup", { value: { enabled: view.startsAtLogin }, label: "Start at login", page: "system/startup",
    write: async ({ changes }) => {
      try { await api("startup", changes); }
      catch (error) { startupWriter.baseline = { enabled: (await api("settings")).startsAtLogin }; throw error; }
      return { enabled: (await api("settings")).startsAtLogin };
    } });
  bindPreferences(startupWriter, startupForm, document.querySelector("#startup-status"));
  document.querySelector("#data-directory").textContent = view.dataDirectory;
  document.querySelector("#ram-default").textContent = `Default ${view.defaultRamReserveMiB} MiB · Effective ${view.effectiveRamReserveMiB} MiB`;
  document.querySelector("#vram-default").textContent = `Default ${view.defaultVramReserveMiB} MiB · Effective ${view.effectiveVramReserveMiB} MiB`;
  document.querySelector("#recovery").textContent = view.recoveryWarning || "";
  document.querySelector("#recovery").hidden = !view.recoveryWarning;
  syncLocalPreferences();
  workspace.hidden = false; status.hidden = true;
}
function renderNodePort(state) {
  const warnings = [];
  if (state.unavailableLoopbackPort !== null)
    warnings.push(`Local port ${state.unavailableLoopbackPort} is unavailable. Mango opened settings on a temporary port.`);
  if (state.tailnetPortUnavailable)
    warnings.push(`Tailscale port ${state.tailnetPort} is unavailable.`);
  const notice = document.querySelector("#node-port-conflict");
  notice.textContent = warnings.join(" "); notice.hidden = warnings.length === 0;
  document.querySelector("#node-port-current").textContent = state.unavailableLoopbackPort === null
    ? `Local port ${state.loopbackPort} · Tailscale port ${state.tailnetPort}` : `Tailscale port ${state.tailnetPort}`;
  nodePortForm.elements.port.defaultValue = state.value;
  if (warnings.length && !portConflictShown) {
    portConflictShown = true;
    if (route.page !== "system/ports") navigate("system/ports");
  }
}
nodePortForm.addEventListener("submit", event => {
  event.preventDefault();
  const port = nodePortForm.elements.port.valueAsNumber;
  perform(event.submitter, document.querySelector("#node-port-feedback"), async () => {
    await api("node-port", { port });
    await loadSettings();
    nodePortForm.reset();
    return `Port ${port} saved. Quit and reopen Mango to apply it.`;
  });
});
document.querySelector("#settings-memory").elements.acceptReducedReserve.addEventListener("change", () => { syncLocalPreferences(); settingsWriter?.flush(); });
async function loadRelease() {
  const state = await api("release");
  maintenanceForm.elements.daily.disabled = !state.canUpdate;
  document.querySelector("#release-status").textContent = `${state.release.version} · Protocol window ${state.release.window} · ${state.updates}${state.draining ? " · Draining for maintenance" : ""}`;
}
const updateAction = document.querySelector("#update-action");
const updatePartial = document.querySelector("#update-partial");
document.querySelector("#update-check").addEventListener("click", event => perform(event.currentTarget, updateAction, async () => {
  await api("release/check", null, "POST"); await loadRelease(); return "Release check complete.";
}));
document.querySelector("#update-share").addEventListener("click", event => perform(event.currentTarget, updateAction, async () => {
  const result = await api("release/share", { allowPartial: updatePartial.checked }, "POST");
  updatePartial.checked = false;
  document.querySelector("#update-partial-label").hidden = !result.needsOverride;
  const detail = result.peers.map(peer => `${peer.name}: ${peer.state}`).join(" · ");
  return result.needsOverride ? `Mango ${result.version}: no updates queued. Some peers could not verify eligibility. ${detail}` : `Mango ${result.version}: ${detail || "No other nodes are enrolled."}`;
}));

async function refreshPage() {
  if (!token || document.hidden) return;
  const jobs = [];
  if (route.page === "overview" || route.group === "protection" || route.group === "serving") jobs.push(loadResources().catch(error => document.querySelector("#resource-feedback").textContent = error.message));
  if (route.group === "serving") jobs.push(loadProviders().catch(error => providerStatus.textContent = error.message));
  if (route.group === "network" || route.group === "serving") jobs.push(loadNetwork().catch(error => document.querySelector("#network-status").textContent = error.message));
  if (route.page === "system/updates") jobs.push(loadRelease().catch(error => document.querySelector("#release-status").textContent = error.message));
  if (["activity/history", "system/logging", "system/data"].includes(route.page)) jobs.push(loadLogs().catch(logError));
  await Promise.allSettled(jobs);
}
document.addEventListener("DOMContentLoaded", async () => {
  const jobs = [[loadSettings, showSessionError], [loadNetwork, e => document.querySelector("#network-status").textContent = e.message],
    [loadProviders, e => providerStatus.textContent = e.message], [loadResources, e => document.querySelector("#resource-feedback").textContent = e.message],
    [loadPublic, e => document.querySelector("#public-state").textContent = e.message], [loadLogs, logError], [loadRelease, e => document.querySelector("#release-status").textContent = e.message]];
  await Promise.allSettled(jobs.map(([work, failure]) => work().catch(failure)));
  if (token) watchDashboard();
});
document.addEventListener("mango:route", () => {
  for (const writer of writers.values()) if (writer.pending && ["pending", "updating"].includes(writer.state.kind)) writer.flush();
  refreshPage();
});
document.addEventListener("visibilitychange", () => { if (!document.hidden) { loadNetwork().catch(error => networkFeedback.textContent = error.message); loadSettings().catch(showSessionError); refreshPage(); } });
setInterval(refreshPage, 5000);
