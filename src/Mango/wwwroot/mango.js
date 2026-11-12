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
let networkAdmin = null;

async function api(path, body, method = body ? "PUT" : "GET") {
  if (!token) throw new Error("Open Mango again to start a local session.");
  const headers = { Authorization: `Bearer ${token}` };
  if (networkAdmin) headers["Mango-Admin"] = networkAdmin;
  if (body) headers["Content-Type"] = "application/json";
  const response = await fetch(`/api/local/${path}`, {
    method,
    headers,
    body: body ? JSON.stringify(body) : undefined,
    cache: "no-store"
  });
  if (!response.ok) {
    const details = await response.json().catch(() => ({}));
    throw new Error(details.error || details.detail || (response.status === 401
      ? "Your local session has ended. Open Mango again." : `Mango returned HTTP ${response.status}.`));
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
  for (const choice of document.querySelectorAll("[data-benchmark-choice]"))
    if (!choice.dataset.changed) choice.value = view.settings.benchmarkPolicy;
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
const providerForm = document.querySelector("#provider-form");
const providerStatus = document.querySelector("#provider-status");
const aliasRatingForm = document.querySelector("#alias-rating-form");
const profileNames = {
  lmStudio: "LM Studio",
  unslothStudio: "Unsloth Studio",
  unslothRun: "Unsloth run",
  llamaSingle: "llama.cpp single model",
  llamaEmbedding: "llama.cpp embeddings",
  llamaRouter: "llama.cpp router"
};
const operationNames = {
  chat: "Chat", embeddings: "Embeddings", images: "Images",
  transcription: "Transcription", speech: "Speech", systemOne: "System One"
};
let editedProvider = null;

function element(tag, content, className) {
  const node = document.createElement(tag);
  if (content !== undefined) node.textContent = content;
  if (className) node.className = className;
  return node;
}

function action(label, handler) {
  const button = element("button", label, "secondary");
  button.type = "button";
  button.addEventListener("click", handler);
  return button;
}

async function providerAction(button, message, operation, success = "Saved.") {
  button.disabled = true;
  providerStatus.classList.remove("error");
  providerStatus.textContent = message;
  try {
    await operation();
    await loadProviders();
    providerStatus.textContent = success;
  } catch (error) {
    providerStatus.classList.add("error");
    providerStatus.textContent = error.message;
  } finally {
    button.disabled = false;
  }
}

function showProviderFields() {
  const fields = providerForm.elements;
  const processAllowed = fields.mode.value === "managed" &&
    ["llamaSingle", "llamaEmbedding"].includes(fields.profile.value);
  document.querySelector("#provider-process").hidden = !processAllowed;
  fields.address.placeholder = fields.profile.value === "lmStudio" ? "http://127.0.0.1:1234" :
    fields.profile.value.startsWith("unsloth") ? "http://127.0.0.1:8888" : "http://127.0.0.1:8080";
}

function editProvider(endpoint) {
  providerForm.reset();
  editedProvider = endpoint?.id ?? null;
  document.querySelector("#provider-form-title").textContent = endpoint ? "Edit provider" : "Register a provider";
  document.querySelector("#provider-cancel").hidden = !endpoint;
  document.querySelector("#provider-clear-token").hidden = !endpoint?.credentialReference;
  if (endpoint) {
    const fields = providerForm.elements;
    for (const key of ["profile", "mode", "address", "executable", "modelFile"])
      fields[key].value = endpoint[key] ?? "";
    fields.token.focus();
  }
  showProviderFields();
}

providerForm.addEventListener("change", showProviderFields);
providerForm.addEventListener("submit", event => {
  event.preventDefault();
  const fields = providerForm.elements;
  const process = !document.querySelector("#provider-process").hidden;
  const update = {
    id: editedProvider, profile: fields.profile.value, mode: fields.mode.value,
    address: fields.address.value, token: fields.token.value || null, clearToken: fields.clearToken.checked,
    executable: process ? fields.executable.value : null, modelFile: process ? fields.modelFile.value : null
  };
  providerAction(event.submitter, "Checking the provider…", async () => {
    await api("providers", update);
    editProvider(null);
  });
});
document.querySelector("#provider-cancel").addEventListener("click", () => editProvider(null));
document.querySelector("#provider-refresh").addEventListener("click", event =>
  providerAction(event.currentTarget, "Refreshing the provider catalog…", () => api("providers/refresh", {}), "Catalog refreshed."));
document.querySelector("#clear-selection").addEventListener("click", event =>
  providerAction(event.currentTarget, "Clearing selection…", () => api("providers/selection", { id: null })));

function providerRow(endpoint) {
  const row = element("div", undefined, "provider-row");
  const name = profileNames[endpoint.profile] ?? endpoint.profile;
  const access = endpoint.mode === "managed" ? "Managed" : "Attached";
  row.append(element("strong", `${name} · ${access}${endpoint.version ? " · " + endpoint.version : ""}`));
  row.append(element("p", `${endpoint.address} · ${endpoint.online ? "Connected" : endpoint.error || "Awaiting inventory"}`, "muted"));
  if (endpoint.credentialReference) row.append(element("p", "Provider token saved", "muted"));
  if (endpoint.mode === "managed" && !endpoint.canLoad && !endpoint.canUnload && !endpoint.canStop)
    row.append(element("p", "No lifecycle operation is available for this registration.", "muted"));
  const buttons = element("div", undefined, "actions");
  buttons.append(action("Edit provider", () => editProvider(endpoint)));
  buttons.append(action("Refresh provider", event => providerAction(event.currentTarget, "Refreshing provider…",
    () => api("providers/refresh", { id: endpoint.id }), "Provider refreshed.")));
  if (endpoint.canStop)
    buttons.append(action("Stop owned process", event => providerAction(event.currentTarget, "Stopping provider…",
      () => api(`providers/${endpoint.id}/stop`, {}), "Process stopped.")));
  else
    buttons.append(action("Remove provider", event => providerAction(event.currentTarget, "Removing provider…",
      () => api(`providers/${endpoint.id}`, undefined, "DELETE"), "Provider removed.")));
  row.append(buttons);
  return row;
}

function labeledInput(label, name, type, value) {
  const wrapper = element("label", label);
  const input = element("input");
  input.name = name;
  input.type = type;
  input.value = value ?? "";
  if (type === "number") input.step = "1";
  wrapper.append(input);
  return wrapper;
}

function ratingRow(label, rating) {
  const row = element("div", undefined, "rating-row");
  const labelField = labeledInput("Capability label", "capability", "text", label);
  labelField.lastElementChild.maxLength = 100;
  labelField.lastElementChild.required = true;
  const numberField = labeledInput("Rating (0–5)", "rating", "number", rating);
  numberField.lastElementChild.min = "0";
  numberField.lastElementChild.max = "5";
  row.append(labelField, numberField, action("Remove rating", () => row.remove()));
  return row;
}

function modelRow(deployment, endpoint, selected, expanded) {
  const model = deployment.model;
  const status = !deployment.online ? "Provider unavailable" : !model.installed ? "Installation unconfirmed" :
    !deployment.instance ? "Installed · unloaded" : deployment.busy || deployment.instance.busy ? "Busy" :
      deployment.eligible ? "Eligible" : "Limits or protocol unconfirmed";
  const details = element("details", undefined, "deployment");
  details.dataset.model = deployment.id;
  details.open = expanded;
  details.append(element("summary", `${deployment.alias} · ${status}${deployment.id === selected ? " · Selected" : ""}`));
  details.append(element("p", [model.displayName, model.variant, model.format, profileNames[endpoint.profile]].filter(Boolean).join(" · ")));
  details.append(element("p", `Provider ID ${model.nativeId}`, "muted"));
  details.append(element("p", `Input ${model.inputs.join(", ") || "unreported"} · Output ${model.outputs.join(", ") || "unreported"}`, "muted"));
  details.append(element("p", `${model.operations.map(key => operationNames[key]).join(", ") || "Protocol unconfirmed"}${model.clientTools ? " · Client function tools" : ""}${model.structuredOutput ? " · Structured output" : ""}`, "muted"));
  details.append(element("p", `Served context ${deployment.context ?? "unknown"}${deployment.contextSource ? " (" + deployment.contextSource + ")" : ""} · Model maximum ${model.theoreticalContext ?? "unknown"}`, "muted"));
  if (model.operations.includes("embeddings"))
    details.append(element("p", `Embedding dimensions ${deployment.embeddingDimensions ?? "unknown"} · Encoding ${model.embeddingEncodings.join(", ")}`, "muted"));

  const buttons = element("div", undefined, "actions");
  if (deployment.eligible && deployment.id !== selected)
    buttons.append(action("Use for local inference", event => providerAction(event.currentTarget, "Selecting deployment…",
      () => api("providers/selection", { id: deployment.id }))));
  const load = !deployment.instance;
  if (model.installed && (load ? endpoint.canLoad && model.supportsLoad : endpoint.canUnload && model.supportsUnload))
    buttons.append(action(load ? "Load model" : "Unload model", event =>
      providerAction(event.currentTarget, load ? "Loading model…" : "Unloading model…",
        () => api("providers/lifecycle", { id: deployment.id, load }), load ? "Load observed." : "Unload observed.")));
  details.append(buttons);

  const form = element("form");
  const fields = element("div", undefined, "inputs");
  for (const [label, key, type] of [
    ["Alias", "alias", "text"], ["Fallback served context (tokens)", "servedContext", "number"],
    ["Fallback embedding dimensions", "embeddingDimensions", "number"],
    ["Embedding family", "embeddingFamily", "text"], ["Media token budget per item", "mediaTokensPerItem", "number"]
  ]) {
    const field = labeledInput(label, key, type, deployment.options[key]);
    if (type === "number") field.lastElementChild.min = "1";
    else field.lastElementChild.maxLength = 200;
    if (key === "alias") field.lastElementChild.required = true;
    if ((key === "embeddingDimensions" || key === "embeddingFamily") && !model.operations.includes("embeddings"))
      field.hidden = true;
    if (key === "mediaTokensPerItem" && !model.inputs.some(modality => modality === "image" || modality === "audio"))
      field.hidden = true;
    fields.append(field);
  }
  form.append(fields);
  const ratings = element("fieldset");
  ratings.append(element("legend", "Capability ratings"));
  const rows = element("div");
  for (const [label, rating] of Object.entries(deployment.options.ratings))
    rows.append(ratingRow(label, rating));
  ratings.append(rows, action("Add rating", () => {
    const row = ratingRow("", null);
    rows.append(row);
    row.querySelector("input").focus();
  }));
  form.append(ratings, element("button", "Save model"));
  form.addEventListener("submit", event => {
    event.preventDefault();
    const input = form.elements;
    const values = Array.from(rows.children, row => {
      const [label, rating] = row.querySelectorAll("input");
      return [label.value.trim(), rating.value === "" ? null : rating.valueAsNumber];
    });
    if (new Set(values.map(pair => pair[0])).size !== values.length) {
      providerStatus.classList.add("error");
      providerStatus.textContent = "Use each capability label once.";
      return;
    }
    const update = { id: deployment.id, alias: input.alias.value,
      revision: deployment.options.revision, ratings: Object.fromEntries(values),
      embeddingFamily: input.embeddingFamily.value || null };
    for (const key of ["servedContext", "embeddingDimensions", "mediaTokensPerItem"])
      update[key] = input[key].value === "" ? null : input[key].valueAsNumber;
    providerAction(event.submitter, "Saving model settings…", () => api("providers/models", update));
  });
  details.append(form);
  return details;
}

async function loadProviders() {
  const catalog = await api("providers");
  const endpoints = new Map(catalog.endpoints.map(endpoint => [endpoint.id, endpoint]));
  document.querySelector("#provider-count").textContent = endpoints.size ? String(endpoints.size) : "Not configured";
  const recovery = document.querySelector("#providers-recovery");
  recovery.textContent = catalog.recoveryWarning ?? "";
  recovery.hidden = !catalog.recoveryWarning;
  document.querySelector("#provider-list").replaceChildren(...catalog.endpoints.map(providerRow));
  const selected = catalog.models.find(model => model.id === catalog.selected);
  document.querySelector("#selection-status").textContent = selected
    ? `${selected.alias} · ${selected.eligible ? "Ready" : "Unavailable"}` : "Choose an eligible model below.";
  document.querySelector("#clear-selection").hidden = !catalog.selected;
  const list = document.querySelector("#deployment-list");
  const expanded = new Set(Array.from(list.querySelectorAll("details[open]"), details => details.dataset.model));
  list.replaceChildren(...catalog.models.map(model => modelRow(model, endpoints.get(model.endpointId),
    catalog.selected, expanded.has(model.id))));
  const aliases = Array.from(new Set(catalog.models.map(model => model.alias))).sort();
  aliasRatingForm.elements.alias.replaceChildren(...aliases.map(alias => element("option", alias)));
  aliasRatingForm.hidden = aliases.length === 0;
}

aliasRatingForm.addEventListener("submit", event => {
  event.preventDefault();
  const fields = aliasRatingForm.elements;
  const value = fields.rating.value === "" ? null : fields.rating.valueAsNumber;
  providerAction(event.submitter, "Applying the alias rating…", () =>
    api("providers/ratings", { alias: fields.alias.value, ratings: { [fields.label.value.trim()]: value } }));
});

loadSettings().then(loadProviders).catch(showSessionError);
