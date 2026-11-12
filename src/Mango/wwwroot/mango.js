"use strict";

const fragmentToken = new URLSearchParams(location.hash.slice(1)).get("session");
if (fragmentToken !== null) {
  history.replaceState(null, "", location.pathname + location.search);
  if (/^[A-F0-9]{64}$/.test(fragmentToken)) sessionStorage.setItem("mango-session", fragmentToken);
}
const token = sessionStorage.getItem("mango-session");
const workspace = document.querySelector("#workspace");
const status = document.querySelector("#status");
const settingsForm = document.querySelector("#settings");
const startupForm = document.querySelector("#startup");
const reserveWarning = document.querySelector("#reserve-warning");
const reserveConsent = settingsForm.elements.acceptReducedReserve;
const numbers = [
  "sampleSeconds", "cooldownSeconds", "cpuCutoffPercent", "gpuCutoffPercent",
  "ramCutoffPercent", "vramCutoffPercent", "ramReserveMiB", "vramReserveMiB"
];
let view;
let dirty = false;

async function api(path, body) {
  if (!token) throw new Error("Open Mango again to start a local session.");
  const headers = { Authorization: `Bearer ${token}` };
  if (body) headers["Content-Type"] = "application/json";
  const response = await fetch(`/api/local/${path}`, {
    method: body ? "PUT" : "GET",
    headers,
    body: body ? JSON.stringify(body) : undefined,
    cache: "no-store"
  });
  if (response.status === 401) throw new Error("Your local session has ended. Open Mango again.");
  if (!response.ok) {
    const details = await response.json().catch(() => ({}));
    throw new Error(details.error || details.detail || `Mango returned HTTP ${response.status}.`);
  }
  return response.status === 204 ? undefined : response.json();
}

function needsConsent() {
  const inputs = settingsForm.elements;
  return !view.settings.reducedReserveConsent && (
    inputs.ramReserveMiB.value !== "" && inputs.ramReserveMiB.valueAsNumber < view.defaultRamReserveMiB ||
    inputs.vramReserveMiB.value !== "" && inputs.vramReserveMiB.valueAsNumber < view.defaultVramReserveMiB
  );
}

function showReserveWarning() {
  const needed = needsConsent();
  reserveWarning.hidden = !needed;
  reserveConsent.required = needed;
  if (!needed) reserveConsent.checked = false;
}

async function loadSettings() {
  view = await api("settings");
  const inputs = settingsForm.elements;
  inputs.role.value = view.settings.role;
  inputs.activityMonitoring.checked = view.settings.activityMonitoring;
  inputs.benchmarkPolicy.value = view.settings.benchmarkPolicy;
  for (const key of numbers) inputs[key].value = view.settings[key] ?? "";
  startupForm.hidden = !view.startupAvailable;
  startupForm.elements.enabled.checked = view.startsAtLogin;
  document.querySelector("#role-status").textContent = view.settings.role === "leech" ? "Leech" : "Contributor";
  document.querySelector("#data-directory").textContent = view.dataDirectory;
  document.querySelector("#ram-default").textContent =
    `Default ${view.defaultRamReserveMiB} MiB · Effective ${view.effectiveRamReserveMiB} MiB`;
  document.querySelector("#vram-default").textContent =
    `Default ${view.defaultVramReserveMiB} MiB · Effective ${view.effectiveVramReserveMiB} MiB`;
  const recovery = document.querySelector("#recovery");
  recovery.textContent = view.recoveryWarning || "";
  recovery.hidden = !view.recoveryWarning;
  showReserveWarning();
  workspace.hidden = false;
  status.hidden = true;
}

async function submit(form, feedback, operation) {
  const button = form.querySelector("button");
  button.disabled = true;
  feedback.classList.remove("error");
  feedback.textContent = "Saving…";
  try {
    await operation();
    feedback.textContent = "Saved.";
  } catch (error) {
    feedback.classList.add("error");
    feedback.textContent = error.message;
  } finally {
    button.disabled = false;
  }
}

settingsForm.addEventListener("input", () => {
  dirty = true;
  showReserveWarning();
});
settingsForm.addEventListener("submit", event => {
  event.preventDefault();
  const inputs = settingsForm.elements;
  const settings = {
    ...view.settings,
    role: inputs.role.value,
    activityMonitoring: inputs.activityMonitoring.checked,
    benchmarkPolicy: inputs.benchmarkPolicy.value
  };
  for (const key of numbers) settings[key] = inputs[key].value === "" ? null : inputs[key].valueAsNumber;
  submit(settingsForm, document.querySelector("#save-status"), async () => {
    await api("settings", { settings, acceptReducedReserve: reserveConsent.checked });
    dirty = false;
    await loadSettings();
  });
});
startupForm.addEventListener("submit", event => {
  event.preventDefault();
  submit(startupForm, document.querySelector("#startup-status"), () =>
    api("startup", { enabled: startupForm.elements.enabled.checked }));
});

function showSessionError(error) {
  status.hidden = false;
  status.classList.add("error");
  status.textContent = error.message;
}

document.addEventListener("visibilitychange", () => {
  if (view && !dirty && document.visibilityState === "visible") loadSettings().catch(showSessionError);
});
loadSettings().catch(showSessionError);
