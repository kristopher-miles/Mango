"use strict";

let catalogView, catalogLoading = false;
let servingNode = null, servingProvider = null, servingModel = null, modelPage = 0, addingProvider = false;
let modelCategory = "all";
let currentProviderWriter, selectionWriter;
const modelCards = new Map();
const providerFields = ["profile", "mode", "address", "executable", "modelFile", "strataControl", "strataModels"];
const modelFields = ["alias", ...servingFields];
const modelPageSize = 5;
const modelCategories = document.querySelector("#model-categories");
const lmLinkWarning = "For best resource management, run Mango separately on the device hosting LM Link models. Mango may behave in unexpected ways when remote resources are incorrectly served as local.";

function localOwner(owner) { return !owner || owner === "local" || owner === networkView?.localNode?.id; }
function intentLabel(desired, report) {
  if (!sameRevision(desired?.revision, report?.revision)) return "Pending host application";
  const text = { applied: "Applied", pending: "Waiting for host resources", localAction: "Local approval required", rejected: "Rejected by host" }[report.state] || "Unobserved";
  return report.reason ? `${text} · ${report.reason}` : text;
}
function selectOptions(select, choices, selected) {
  const key = JSON.stringify(choices);
  if (select.dataset.choices !== key) {
    select.replaceChildren(...choices.map(([value, text]) => { const option = element("option", text); option.value = value; return option; }));
    select.dataset.choices = key;
  }
  select.value = choices.some(([value]) => value === selected) ? selected : choices[0]?.[0] ?? "";
}
function selectedProvider() {
  if (localOwner(servingNode)) return localProviders().find(item => item.id === servingProvider);
  return networkView?.providerIntents.find(item => item.owner === servingNode && item.id === servingProvider);
}
function localProviders() {
  const endpoints = catalogView?.endpoints || [];
  return [...endpoints, ...(networkView?.providerIntents || []).filter(item => item.owner === networkView.localNode?.id && item.enabled && !endpoints.some(endpoint => endpoint.id === item.id))
    .map(item => ({ ...item, mode: "attached", pendingRegistration: true }))];
}
function providerRecord(endpoint, local) {
  return local ? { profile: endpoint.profile, mode: endpoint.mode, address: endpoint.address, executable: endpoint.executable || "", modelFile: endpoint.modelFile || "",
    strataControl: endpoint.strataControl || "", strataModels: endpoint.strataModels || "",
    token: "", clearToken: false, configurationRevision: endpoint.configurationRevision }
    : { profile: endpoint.profile, address: endpoint.address, enabled: endpoint.enabled, revision: endpoint.revision };
}
function wireProfile(profile) { return profile === "strata" ? "generic" : profile; }
function providerNeedsAdministration(owner, id, writer) {
  if (!networkView?.identity) return false;
  if (!localOwner(owner)) return true;
  if (!writer?.dirty.has("profile") && !writer?.dirty.has("address")) return false;
  const desired = networkView.providerIntents.find(item => item.owner === networkView.localNode?.id && item.id === id);
  return !(desired?.enabled && wireProfile(writer.value.profile) === desired.profile && writer.value.address === desired.address);
}
async function providerAction(button, message, work, success = "Updated.") {
  return perform(button, providerStatus, async () => { await work(); await loadProviders(true); await Promise.allSettled([loadNetwork(), loadResources()]); return success; }, message);
}
function showProviderAddress(fields) {
  fields.address.required = fields.profile.value === "generic";
  fields.address.placeholder = fields.profile.value === "generic" ? "Enter provider origin" : fields.profile.value === "strata" ? "http://127.0.0.1:18765" : fields.profile.value === "lmStudio" ? "http://127.0.0.1:1234" : fields.profile.value.startsWith("unsloth") ? "http://127.0.0.1:8888" : "http://127.0.0.1:8080";
}
function showProviderFields() {
  const local = localOwner(servingNode), fields = providerForm.elements;
  const strata = fields.profile.value === "strata";
  fields.profile.querySelector('option[value="strata"]').hidden = !local;
  for (const key of ["mode", "token", "clearToken"]) fields[key].closest("label").hidden = !local || strata;
  fields.clearToken.closest("label").hidden = !local || strata || addingProvider || !selectedProvider()?.credentialReference;
  fields.enabled.closest("label").hidden = local;
  document.querySelector("#provider-process").hidden = !local || fields.mode.value !== "managed" || !["llamaSingle", "llamaEmbedding"].includes(fields.profile.value);
  document.querySelector("#provider-strata").hidden = !local || !strata;
  document.querySelector("#provider-help").textContent = strata
    ? "Mango manages a private Strata runtime and reserves this computer while it loads or serves. Loading may briefly slow Windows. Choose a prepared model below."
    : !addingProvider && selectedProvider()?.pendingRegistration
      ? "Review the requested address and choose this computer's permissions before connecting. Credentials stay on this computer."
      : "Enter an address for a generic endpoint. Other profiles have local defaults. Mango saves provider tokens for the current Windows user.";
  showProviderAddress(fields);
}
const enabledLabel = element("label", null, "checkbox"), enabledInput = element("input");
Object.assign(enabledInput, { name: "enabled", type: "checkbox", checked: true });
enabledLabel.append(enabledInput, "Enable provider request"); providerForm.querySelector(".inputs").append(enabledLabel);
const createProviderButton = element("button", "Connect provider"); createProviderButton.type = "submit";
providerForm.querySelector(".actions").prepend(createProviderButton);
providerForm.querySelector(".actions").append(document.querySelector("#provider-extra-actions"), document.querySelector("#provider-clear-token"));
document.querySelector("#provider-extra-actions").classList.remove("actions");
providerForm.addEventListener("change", showProviderFields);
for (const button of providerForm.querySelectorAll("[data-strata-folder]")) {
  button.addEventListener("click", () => perform(button, providerStatus, async () => {
    const record = providerForm.dataset.record;
    const { folder } = await api("providers/strata/folder", {}, "POST");
    if (!folder || record !== providerForm.dataset.record) return;
    const input = providerForm.elements[button.dataset.strataFolder];
    input.value = folder;
    input.dispatchEvent(new Event("change", { bubbles: true }));
    return "Strata folder selected.";
  }, "Choose a folder in Windows…"));
}
providerForm.addEventListener("submit", event => {
  event.preventDefault();
  if (!providerForm.dataset.record.startsWith("new:")) { currentProviderWriter?.flush(); return; }
  const fields = providerForm.elements, owner = servingNode, local = localOwner(owner);
  const endpoint = selectedProvider();
  const update = Object.fromEntries(providerFields.map(key => [key, fields[key].value]));
  const process = !document.querySelector("#provider-process").hidden;
  const strata = update.profile === "strata";
  providerAction(createProviderButton, "Connecting provider…", async () => {
    if (local) {
      const result = await api("providers", { ...update, id: endpoint?.pendingRegistration ? endpoint.id : null,
        mode: strata ? "managed" : update.mode, token: strata ? null : fields.token.value || null, clearToken: false,
        executable: process ? update.executable : null, modelFile: process ? update.modelFile : null,
        strataControl: strata ? update.strataControl : null, strataModels: strata ? update.strataModels : null });
      servingProvider = result.id;
    } else {
      const before = new Set(networkView.providerIntents.map(item => item.id));
      await api("network/providers", { owner, id: null, revision: null, profile: update.profile, address: update.address, enabled: true });
      const after = await api("network"); networkView = after;
      servingProvider = after.providerIntents.find(item => item.owner === owner && !before.has(item.id))?.id;
    }
    fields.token.value = ""; addingProvider = false;
  }, local ? "Provider connected. Choose a model below." : "Provider requested. The owning computer may need to approve its connection.");
});
function renderProvider() {
  const endpoint = selectedProvider(), local = localOwner(servingNode), fields = providerForm.elements;
  const desired = networkView?.providerIntents.find(item => item.owner === servingNode && item.id === servingProvider);
  const observed = networkView?.catalogs.find(item => item.node === servingNode)?.providers.find(item => item.id === servingProvider);
  const newRegistration = addingProvider || !endpoint || endpoint.pendingRegistration;
  const approval = !addingProvider && endpoint?.pendingRegistration;
  const key = newRegistration ? `new:${servingNode}:${approval ? endpoint.id : ""}` : `provider:${local ? "local" : servingNode}:${endpoint.id}`;
  if (providerForm.dataset.record !== key) {
    providerForm.dataset.record = key;
    currentProviderWriter?.bindings.splice(0);
    currentProviderWriter = null;
    providerForm.reset();
    if (endpoint && !addingProvider) for (const name of [...providerFields, "enabled"]) fillInput(fields[name], endpoint[name]);
    fields.token.value = "";
    providerStatus.textContent = ""; delete providerStatus.dataset.feedback;
  }
  document.querySelector("#provider-heading").textContent = approval ? "Approve this connection" : newRegistration ? "Connect a provider" : profileNames[endpoint.profile];
  document.querySelector("#provider-form-title").textContent = newRegistration ? "New provider" : "Provider settings";
  const connection = document.querySelector("#provider-connection");
  connection.textContent = approval ? "Local approval required" : newRegistration ? "New connection" : local
    ? endpoint.loadingSince ? `Preparing Strata · ${Math.max(0, Math.floor((Date.now() - Date.parse(endpoint.loadingSince)) / 1000))}s`
      : endpoint.hostReserved ? "Computer reserved for Strata" : endpoint.online ? "Connected" : "Unreachable"
    : observed?.reachable ? "Reached at last observation" : "Unconfirmed";
  connection.className = `badge ${!newRegistration && (local ? endpoint.online : observed?.reachable) ? "good" : "warn"}`;
  document.querySelector("#provider-applied").textContent = newRegistration ? local ? "Credentials and process permissions stay on this computer." : "This requests a connection on the selected computer. Credentials and lifecycle permission are configured there."
    : local ? [endpoint.error, endpoint.exclusiveHost ? "Private loopback credential" : endpoint.credentialReference ? "Credential stored for this Windows user" : "No stored credential", desired ? `${intentLabel(desired, observed?.intent)} · Requested ${profileNames[desired.profile]} at ${desired.address}${desired.enabled ? "" : " · Disabled"}` : ""].filter(Boolean).join(" · ")
      : `${intentLabel(endpoint, observed?.intent)} · Applied address ${observed?.address || "unobserved"} · Observed ${timeLabel(observed?.observedAt)}`;
  createProviderButton.hidden = !newRegistration;
  createProviderButton.textContent = approval ? "Approve requested connection" : "Connect provider";
  fields.profile.disabled = fields.address.disabled = !!approval;
  document.querySelector("#provider-refresh").hidden = !local || newRegistration;
  document.querySelector("#provider-clear-token").hidden = !local || !endpoint?.credentialReference;
  if (!newRegistration) {
    const id = endpoint.id, owner = servingNode;
    currentProviderWriter = recordWriter(key, { value: providerRecord(endpoint, local), label: `${profileNames[endpoint.profile]} provider`, page: `serving/${owner}/${id}`,
      requiresAdmin: () => providerNeedsAdministration(owner, id, writers.get(key)),
      read: async () => {
        const source = local ? (await api("providers")).endpoints : (await api("network")).providerIntents;
        const item = source.find(item => item.id === id); if (!item) throw new Error("This provider was removed. Keep the draft or connect another provider.");
        return providerRecord(item, local);
      },
      write: async ({ baseline, changes }) => {
        const next = { ...baseline, ...changes };
        if (local) {
          const process = next.mode === "managed" && ["llamaSingle", "llamaEmbedding"].includes(next.profile);
          const strata = next.profile === "strata";
          const accepted = await api("providers", { id, ...Object.fromEntries(providerFields.map(name => [name, next[name]])),
            mode: strata ? "managed" : next.mode, token: strata ? null : next.token || null, clearToken: !strata && next.clearToken, expectedConfiguration: baseline.configurationRevision,
            executable: process ? next.executable || null : null, modelFile: process ? next.modelFile || null : null,
            strataControl: strata ? next.strataControl || null : null, strataModels: strata ? next.strataModels || null : null });
          if (providerForm.dataset.record === key) fields.token.value = "";
          return providerRecord(accepted, true);
        }
        await api("network/providers", { owner, id, revision: baseline.revision, profile: next.profile, address: next.address, enabled: next.enabled });
        return providerRecord((await api("network")).providerIntents.find(item => item.id === id), false);
      } });
    bindPreferences(currentProviderWriter, providerForm, providerStatus, { inputs: () => [...providerForm.querySelectorAll("input,select")].filter(input =>
      local ? input.name !== "enabled" && input.name !== "token" : ["profile", "address", "enabled"].includes(input.name)) });
    fields.token.onblur = async () => {
      if (!fields.token.value || !currentProviderWriter) return;
      const writer = currentProviderWriter, secret = fields.token.value;
      fields.token.value = "";
      writer.change("token", secret);
      const version = writer.versions.get("token");
      await writer.flush();
      if (writer.dirty.has("token") && ["pending", "updating"].includes(writer.state.kind)) await writer.flush();
      if (writer.versions.get("token") !== version) return;
      const failed = writer.dirty.has("token");
      writer.dirty.delete("token"); writer.invalid.delete("token"); writer.conflicts.delete("token");
      if (failed) writer.report("error", "Credential was not updated. Re-enter it after resolving the provider error.");
      else writer.changed(writer);
    };
    fields.token.onkeydown = event => { if (event.key === "Enter") { event.preventDefault(); fields.token.onblur(); } };
  } else {
    for (const input of providerForm.querySelectorAll("input,select")) {
      for (const [type, handler] of input.preferenceHandler || []) input.removeEventListener(type, handler);
      input.preferenceHandler = null; input.preferenceWriter = null;
    }
    fields.token.onblur = null; fields.token.onkeydown = null;
  }
  const actions = document.querySelector("#provider-extra-actions");
  const actionKey = `${key}:${endpoint?.canStop}:${!!administration.token}:${JSON.stringify(desired)}`;
  if (actions.dataset.key !== actionKey) {
    actions.dataset.key = actionKey; actions.replaceChildren();
    if (endpoint && !newRegistration && local) {
      if (desired?.enabled && (desired.address !== endpoint.address || desired.profile !== wireProfile(endpoint.profile))) actions.append(action("Approve requested connection", event => providerAction(event.currentTarget, "Approving connection…", async () => {
        const writer = currentProviderWriter;
        writer.change("profile", desired.profile); writer.change("address", desired.address);
        await writer.resume();
        if (writer.pending) throw new Error(writer.state.message);
      }, "Requested connection approved on this computer.")));
      if (endpoint.canStop) actions.append(action(endpoint.exclusiveHost ? "Stop and release computer" : "Stop owned process", event => providerAction(event.currentTarget, "Stopping provider…", () => api(`providers/${endpoint.id}/stop`, {}), "Process stopped.")));
      const remove = action(desired?.enabled === false ? "Confirm disable on this computer" : "Remove provider", event => {
        if (confirm("Remove this provider registration? Model files remain on disk.")) providerAction(event.currentTarget, "Removing provider…", async () => {
          await api(`providers/${endpoint.id}`, null, "DELETE"); servingProvider = null;
        }, "Provider removed.");
      }); actions.append(remove);
    }
  }
  showProviderFields();
}
function modelRecord(desired, local) {
  if (desired) return { alias: desired.alias ?? "", ratings: desired.ratings, ...Object.fromEntries(servingFields.map(key => [key, desired.serving?.[key] ?? null])), revision: desired.revision };
  return { alias: local.alias ?? "", ratings: local.options.ratings, ...Object.fromEntries(servingFields.map(key => [key, local.options[key] ?? null])), revision: local.options.revision };
}
function providerIdentifiers(item) { return item.local?.providerIdentifiers ?? item.observed?.providerIdentifiers ?? []; }
function isLmLinkModel(item) { return (item.local?.model.origin ?? item.observed?.origin) === "lmLink"; }
function servingModels() {
  if (!catalogView) return [];
  const local = localOwner(servingNode), owner = servingNode;
  const observed = networkView?.catalogs.find(item => item.node === owner)?.models || [];
  const desired = (networkView?.models || []).filter(item => item.owner === owner);
  const models = local ? catalogView.models.filter(item => item.endpointId === servingProvider).map(item => ({ id: item.id, owner,
    local: item, desired: desired.find(wanted => wanted.deployment === item.id), observed: observed.find(seen => seen.id === item.id) }))
    : desired.map(item => ({ id: item.deployment, owner, desired: item, observed: observed.find(seen => seen.id === item.deployment) }))
    .filter(item => item.observed?.endpoint === servingProvider || !item.observed?.endpoint || !servingProvider);
  return models.sort((left, right) => Number(isLmLinkModel(left)) - Number(isLmLinkModel(right)));
}
function modelWriter(item) {
  const local = localOwner(item.owner), key = `model:${local ? "local" : item.owner}:${item.id}`;
  const shared = !!item.desired;
  const read = async () => {
    if (shared) {
      const desired = (await api("network")).models.find(model => model.owner === item.owner && model.deployment === item.id);
      if (!desired) throw new Error("The requested model is no longer present."); return modelRecord(desired);
    }
    const model = (await api("providers")).models.find(model => model.id === item.id);
    if (!model) throw new Error("The model is no longer in the inventory."); return modelRecord(null, model);
  };
  return recordWriter(key, { value: modelRecord(item.desired, item.local), label: item.desired?.alias || item.local?.alias ||
    providerIdentifiers(item)[0] || item.observed?.displayName || "Model",
    page: `serving/${item.owner}/${servingProvider}?model=${item.id}`, requiresAdmin: () => !!networkView?.identity,
    read, write: async ({ baseline, changes }) => {
      const next = { ...baseline, ...changes };
      if (shared) await api("network/models", { owner: item.owner, deployment: item.id, revision: baseline.revision,
        alias: next.alias, ratings: next.ratings, serving: Object.fromEntries(servingFields.map(name => [name, next[name]])) });
      else await api("providers/models", { id: item.id, ...next });
      return read();
    } });
}
function ratingEditor(writer) {
  const fieldset = element("fieldset"), rows = element("div");
  fieldset.append(element("legend", "Capability ratings"), rows);
  const changed = () => {
    const entries = [...rows.children].map(row => [...row.querySelectorAll("input")]);
    const valid = entries.every(([label, rating]) => label.validity.valid && rating.validity.valid) && new Set(entries.map(([label]) => label.value.trim())).size === entries.length;
    for (const [label, rating] of entries) { label.setAttribute("aria-invalid", String(!valid)); rating.setAttribute("aria-invalid", String(!rating.validity.valid)); }
    writer.change("ratings", Object.fromEntries(entries.map(([label, rating]) => [label.value.trim(), rating.value === "" ? null : rating.valueAsNumber])), { valid });
  };
  function row(label, value) {
    const node = element("div", null, "rating-row");
    const name = labeledInput("Capability", "capability", "text", label), rating = labeledInput("Rating · 0–5", "rating", "number", value);
    Object.assign(name.lastElementChild, { maxLength: 100, required: true }); Object.assign(rating.lastElementChild, { min: 0, max: 5 });
    node.append(name, rating, action("Remove", () => { node.remove(); changed(); writer.flush(); }));
    node.addEventListener("input", event => { if (!event.isComposing) changed(); }); node.addEventListener("compositionend", changed);
    node.addEventListener("focusout", () => writer.flush()); return node;
  }
  fieldset.refreshRatings = () => {
    const signature = JSON.stringify(writer.value.ratings || {});
    if (fieldset.dataset.ratings === signature || rows.contains(document.activeElement) || writer.invalid.has("ratings")) return;
    rows.replaceChildren(...Object.entries(writer.value.ratings || {}).map(([label, value]) => row(label, value)));
    fieldset.dataset.ratings = signature;
  };
  fieldset.refreshRatings();
  fieldset.append(action("Add rating", () => { const entry = row("", null); rows.append(entry); entry.querySelector("input").focus(); changed(); }));
  return fieldset;
}
function readinessText(item) {
  const readiness = modelReadiness(item);
  if (readiness) return `${item.local ? "" : "At last observation: "}${readiness.reason || readiness.label}${item.local ? "" : ` · ${timeLabel(networkView?.catalogs.find(x => x.node === item.owner)?.observedAt)}`}`;
  if (!item.local) return item.observed ? `Last observed ${timeLabel(networkView?.catalogs.find(x => x.node === item.owner)?.observedAt)} · ${item.observed.locallyAvailable ? "Available then" : "Unavailable then"}` : "Awaiting host observation";
  return "Checking serving readiness…";
}
function modelReadiness(item) {
  if (!item.local) return item.observed?.readiness;
  const targets = (resourceView?.models || []).filter(model => model.id === item.id);
  return (targets.find(model => resourceKey(model) === resourceTarget.value) || targets.find(model => model.readiness?.available) || targets[0])?.readiness;
}
function renderModelCard(item) {
  const key = `${item.owner}:${item.id}`, local = !!item.local, facts = item.local?.model || item.observed;
  const writer = modelWriter(item);
  let card = modelCards.get(key);
  if (!card) {
    card = element("article", null, "model-card"); card.dataset.model = item.id;
    const heading = element("div", null, "model-heading"), title = element("h3"), badge = element("span", null, "badge");
    const meta = element("div", null, "model-heading-meta"), link = element("span", null, "model-link-mark");
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg"), use = document.createElementNS(svg.namespaceURI, "use");
    svg.setAttribute("class", "glyph"); svg.setAttribute("aria-hidden", "true"); use.setAttribute("href", "/glyphs.svg#lm-link");
    svg.append(use); link.append(svg); link.tabIndex = 0; link.title = lmLinkWarning;
    link.setAttribute("role", "img"); link.setAttribute("aria-label", `LM Link. ${lmLinkWarning}`);
    meta.append(link, badge); heading.append(title, meta);
    card.append(heading, element("p", "", "model-facts muted"), element("p", "", "model-readiness"), element("div", "", "readiness-action"), element("p", "", "model-applied muted"));
    const form = element("form"), inputs = element("div", null, "inputs");
    for (const [label, name, type] of [["Alias", "alias", "text"], ["Fallback context · tokens", "servedContext", "number"],
      ["Embedding dimensions", "embeddingDimensions", "number"], ["Embedding family", "embeddingFamily", "text"], ["Media tokens per item", "mediaTokensPerItem", "number"]]) {
      const field = labeledInput(label, name, type, writer.value[name]), input = field.lastElementChild;
      if (type === "number") { input.min = "1"; input.max = name === "embeddingDimensions" ? "65536" : "4194304"; }
      else input.maxLength = 200;
      if (name === "alias") input.placeholder = "Optional";
      if (name.startsWith("embedding")) field.hidden = !facts?.operations.includes("embeddings");
      if (name === "mediaTokensPerItem") field.hidden = !facts?.inputs.some(value => ["image", "audio"].includes(value));
      inputs.append(field);
    }
    form.append(inputs); card.append(form, ratingEditor(writer));
    const feedback = element("p", null, "field-status"); feedback.setAttribute("role", "status"); card.append(feedback);
    bindPreferences(writer, form, feedback);
    card.append(element("div", null, "actions")); modelCards.set(key, card);
  }
  card.classList.toggle("selected", servingModel === item.id);
  card.querySelector("fieldset").refreshRatings();
  for (const input of card.querySelectorAll("[name^=embedding]")) input.closest("label").hidden = !facts?.operations?.includes("embeddings") && !writer.dirty.has(input.name);
  card.querySelector("[name=mediaTokensPerItem]").closest("label").hidden = !facts?.inputs?.some(value => ["image", "audio"].includes(value)) && !writer.dirty.has("mediaTokensPerItem");
  const identifiers = providerIdentifiers(item);
  card.querySelector("h3").textContent = facts?.displayName || identifiers[0] || writer.value.alias || "Model";
  card.querySelector(".model-link-mark").hidden = !isLmLinkModel(item);
  card.querySelector(".badge").textContent = local ? item.local.instance ? item.local.busy || item.local.instance.busy ? "Busy" : "Loaded" : "Unloaded" : "Remote";
  const origin = facts?.origin === "local" ? "Local to LM Studio" : facts?.origin === "lmLink" ? `LM Link${facts.sourceDevice ? ` · ${facts.sourceDevice}` : ""}` : "";
  const timing = local
    ? resourceView?.models.find(model => model.id === item.id && model.operation === "chat")
    : item.observed?.workloads?.find(work => work.limit.operation === "chat");
  const rate = timing?.generationTokensPerSecond;
  card.querySelector(".model-facts").textContent = [identifiers.length ? `Provider ${identifiers.length === 1 ? "ID" : "IDs"} ${identifiers.join(", ")}` : "",
    origin, (facts?.operations || []).map(key => operationNames[key]).join(", "),
    rate == null ? "" : `${rate.toFixed(1)} tokens/s (${timing.providerGenerationRate === true
      ? "provider reported" : timing.providerGenerationRate === false ? "stream estimate" : "source unrecorded"})`,
    facts?.inputs?.length ? `${facts.inputs.join(", ")} → ${(facts.outputs || []).join(", ")}` : "Capabilities unobserved",
    facts?.tools ? "Function tools" : "", facts?.structuredOutput ? "Structured output" : "", `Context ${item.local?.context ?? item.observed?.context ?? "unconfirmed"}`].filter(Boolean).join(" · ");
  card.querySelector(".model-readiness").textContent = readinessText(item);
  const next = modelReadiness(item)?.action, nextAction = card.querySelector(".readiness-action");
  if (nextAction.dataset.action !== next) {
    nextAction.dataset.action = next || ""; nextAction.replaceChildren();
    if (local && next) {
      const labels = { qualification: "Set local limits", contribution: "Contribution control", provider: "Provider settings", model: "Model limits", protection: "Review protection", observations: "Refresh observations" };
      if (labels[next]) nextAction.append(action(labels[next], () => {
        if (next === "protection") navigate("protection/activity");
        else if (next === "observations") loadResources().catch(error => resourceFeedback.textContent = error.message);
        else if (next === "qualification") { servingModel = item.id; renderServingModels(); renderQualification(); resourceTarget.focus(); }
        else if (next === "contribution") document.querySelector(".serving-quick [data-setting=role]").focus();
        else if (next === "provider") providerForm.elements.address.focus();
        else card.querySelector("[name=servedContext]").focus();
      }));
    }
  }
  const appliedAlias = item.local ? item.local.alias : item.observed?.effectiveAlias;
  card.querySelector(".model-applied").textContent = item.desired ?
    [intentLabel(item.desired, item.observed?.intent), appliedAlias && `Applied alias ${appliedAlias}`].filter(Boolean).join(" · ") : "Settings on this computer";
  const actions = card.querySelector(".actions"), endpoint = selectedProvider();
  const disabled = item.local?.servingDisabled === true;
  const actionKey = `${local}:${item.local?.instance?.id}:${item.local?.eligible}:${disabled}:${item.local?.options?.revision}:${item.local?.yolo}:${catalogView.selected}:${servingModel}:${endpoint?.canLoad}:${endpoint?.canUnload}`;
  if (actions.dataset.key !== actionKey) {
    actions.dataset.key = actionKey; actions.replaceChildren();
    actions.append(action(servingModel === item.id ? "Limits selected" : "View limits", () => {
      servingModel = item.id; renderServingModels(); renderQualification();
    }));
    if (local) {
      const yolo = item.local.yolo === true;
      const capacity = action(yolo ? "Disable YOLO" : "Enable YOLO", event => providerAction(event.currentTarget,
        "Updating provider capacity policy…",
        () => api(`providers/models/${item.id}/yolo`, { enabled: !yolo, revision: item.local.options.revision }),
        yolo ? "Mango resource protection restored." : "YOLO enabled: native provider owns capacity."));
      capacity.title = "YOLO uses native provider capacity checks alongside other YOLO models. Mango swaps idle models when demand changes resource mode.";
      if (!endpoint.exclusiveHost) actions.append(capacity);
      actions.append(action(disabled ? "Enable serving" : "Disable serving", event => providerAction(event.currentTarget,
        disabled ? "Enabling serving…" : "Disabling serving…",
        () => api(`providers/models/${item.id}/serving`, { enabled: disabled }),
        disabled ? "Serving enabled." : "Serving disabled.")));
      const select = action(item.id === catalogView.selected ? "Preferred model" : "Use locally", event => providerAction(event.currentTarget, "Selecting model…", async () => {
        await selectionWriter.change("id", item.id, { immediate: true });
        if (selectionWriter.pending) throw new Error(selectionWriter.state.message);
      }, "Preferred model updated."));
      select.disabled = !item.local.eligible || item.id === catalogView.selected; actions.append(select);
      if (!item.local.eligible) select.title = "A preferred local deployment must be loaded and eligible. Automatic placement can load qualified models on demand.";
      const load = !item.local.instance;
      if (facts.installed && (load ? endpoint.canLoad && facts.supportsLoad : endpoint.canUnload && facts.supportsUnload))
        actions.append(action(load ? "Load" : "Unload", event => providerAction(event.currentTarget, load ? "Loading model…" : "Unloading model…", () => api("providers/lifecycle", { id: item.id, load }), load ? "Load observed." : "Unload observed.")));
    } else { const hint = element("span", "Lifecycle and limits are managed on the owning computer.", "muted"); actions.append(hint); }
  }
  return card;
}
function renderServingModels() {
  let items = servingModels();
  document.querySelector("#model-serving-actions").hidden = !localOwner(servingNode) || addingProvider || items.length === 0;
  const query = document.querySelector("#model-search").value.toLowerCase(), paginated = modelCategory === "all";
  items = items.filter(item => (!query || [item.local?.model.displayName || item.observed?.displayName,
    item.desired?.alias || item.local?.alias, ...providerIdentifiers(item)].filter(Boolean).join(" ").toLowerCase().includes(query)) &&
    (paginated || (item.local?.model.operations || item.observed?.operations || []).includes(modelCategory)));
  if (!items.some(item => item.id === servingModel)) servingModel = items[0]?.id || null;
  modelPage = Math.min(modelPage, Math.max(0, Math.ceil(items.length / modelPageSize) - 1));
  const start = paginated ? modelPage * modelPageSize : 0, end = paginated ? Math.min(items.length, start + modelPageSize) : items.length;
  const visible = items.slice(start, end).map(renderModelCard);
  const list = document.querySelector("#deployment-list");
  list.setAttribute("aria-labelledby", `model-category-${modelCategory}`);
  for (const tab of modelCategories.children) {
    const active = tab.dataset.category === modelCategory;
    tab.setAttribute("aria-selected", String(active)); tab.tabIndex = active ? 0 : -1;
  }
  if (!visible.length) visible.push(element("div", addingProvider ? "Connect a provider to discover its models."
    : selectedProvider()?.pendingRegistration ? "Approve this connection above to discover its models."
      : "No matching models. Refresh the provider inventory or adjust the filters.", "panel muted"));
  if (visible.length !== list.childElementCount || visible.some((card, index) => card !== list.children[index])) list.replaceChildren(...visible);
  document.querySelector("#model-count").textContent = !items.length ? "No models" : paginated
    ? `${start + 1}–${end} of ${items.length} models` : `${items.length} ${items.length === 1 ? "model" : "models"}`;
  const previous = document.querySelector("#model-previous"), next = document.querySelector("#model-next");
  previous.hidden = next.hidden = !paginated;
  previous.disabled = start === 0; next.disabled = end === items.length;
}
function renderServing() {
  if (!catalogView) return;
  const owners = networkView?.localNode ? [networkView.localNode, ...networkView.nodes] : [{ id: "local", name: "This computer" }];
  if (!owners.some(node => node.id === servingNode)) servingNode = owners[0].id;
  const local = localOwner(servingNode);
  selectOptions(document.querySelector("#serving-node"), owners.map(node => [node.id, `${node.name}${node.id === networkView?.localNode?.id ? " · This computer" : ""}`]), servingNode);
  const endpoints = local ? localProviders() : (networkView?.providerIntents || []).filter(item => item.owner === servingNode);
  if (!endpoints.some(item => item.id === servingProvider)) servingProvider = endpoints[0]?.id || null;
  selectOptions(document.querySelector("#serving-provider"), endpoints.map(item => [item.id, `${profileNames[item.profile]} · ${item.address}`]), servingProvider);
  document.querySelector("#serving-scope").textContent = local ? networkView?.identity
    ? "Model configuration is shared across your network. Provider credentials, process permissions and qualification stay on this computer."
    : "This computer · Provider configuration, models and qualification are local until you join a network."
    : "Requested configuration for the selected computer · Local permissions and qualification must be set there.";
  document.querySelector(".serving-quick").hidden = !local;
  document.querySelector("#local-placement").hidden = !local || !(catalogView.models.length || catalogView.selected || selectionWriter?.pending);
  renderProvider(); renderServingModels();
  if (typeof renderQualification === "function") renderQualification();
  renderAliasScope();
}
async function loadProviders(force = false) {
  if (catalogLoading) { if (!force) return catalogLoading; await catalogLoading; }
  catalogLoading = (async () => {
    catalogView = await api("providers");
    const warning = document.querySelector("#providers-recovery"); warning.textContent = catalogView.recoveryWarning || ""; warning.hidden = !catalogView.recoveryWarning;
    const input = document.querySelector("#local-selection");
    selectOptions(input, [["", "Automatic placement"], ...catalogView.models.filter(model => model.eligible || model.id === catalogView.selected || model.id === selectionWriter?.value.id).map(model => [model.id, model.alias || model.providerIdentifiers?.[0] || model.model.displayName])], selectionWriter?.pending ? selectionWriter.value.id : catalogView.selected || "");
    selectionWriter = recordWriter("selection", { value: { id: catalogView.selected || "" }, label: "Local model preference", page: "serving",
      write: async ({ changes }) => { await api("providers/selection", { id: changes.id || null }); return { id: (await api("providers")).selected || "" }; } });
    bindPreferences(selectionWriter, input.parentElement, document.querySelector("#selection-status"), { inputs: () => [input] });
    renderServing();
  })().finally(() => { catalogLoading = null; });
  return catalogLoading;
}
document.querySelector("#provider-add").addEventListener("click", () => { addingProvider = true; renderProvider(); providerForm.elements.profile.focus(); });
document.querySelector("#provider-refresh").addEventListener("click", event => providerAction(event.currentTarget, "Refreshing catalog…", () => api("providers/refresh", { id: servingProvider }), "Catalog refreshed."));
document.querySelector("#serving-node").addEventListener("change", event => { servingNode = event.target.value; servingProvider = null; servingModel = null; addingProvider = false; modelPage = 0; renderServing(); });
document.querySelector("#serving-provider").addEventListener("change", event => { servingProvider = event.target.value; addingProvider = false; servingModel = null; modelPage = 0; renderServing(); });
document.querySelector("#model-search").addEventListener("input", () => { modelPage = 0; renderServingModels(); renderQualification(); });
for (const [key, label] of [["all", "All"], ...Object.entries(operationNames)]) {
  const tab = element("button", label);
  tab.type = "button"; tab.id = `model-category-${key}`; tab.dataset.category = key;
  tab.setAttribute("role", "tab"); tab.setAttribute("aria-controls", "deployment-list");
  tab.addEventListener("click", () => { modelCategory = key; modelPage = 0; renderServingModels(); renderQualification(); });
  modelCategories.append(tab);
}
modelCategories.addEventListener("keydown", event => {
  const tabs = [...modelCategories.children], index = tabs.indexOf(event.target);
  if (index < 0 || !["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
  event.preventDefault();
  const next = event.key === "Home" ? 0 : event.key === "End" ? tabs.length - 1
    : (index + (event.key === "ArrowLeft" ? -1 : 1) + tabs.length) % tabs.length;
  tabs[next].focus(); tabs[next].click();
});
for (const [id, enabled] of [["model-serve-all", true], ["model-serve-none", false]]) {
  document.querySelector(`#${id}`).addEventListener("click", event => perform(event.currentTarget, document.querySelector("#catalog-status"), async () => {
    await api(`providers/${servingProvider}/serving`, { enabled });
    await loadProviders(true); await Promise.allSettled([loadNetwork(), loadResources()]);
    return enabled ? "Serving enabled for all models on this provider." : "Serving disabled for all models on this provider.";
  }));
}
document.querySelector("#model-previous").addEventListener("click", () => { modelPage--; renderServingModels(); });
document.querySelector("#model-next").addEventListener("click", () => { modelPage++; renderServingModels(); });
document.addEventListener("mango:route", event => {
  if (event.detail.page !== "serving") providerForm.elements.token.value = "";
  if (event.detail.group !== "serving") return;
  if (event.detail.node) { servingNode = event.detail.node; servingProvider = event.detail.provider; addingProvider = false; }
  if (event.detail.model) {
    servingModel = event.detail.model;
    document.querySelector("#model-search").value = ""; modelCategory = "all";
    modelPage = Math.max(0, Math.floor(servingModels().findIndex(item => item.id === servingModel) / modelPageSize));
  }
  renderServing();
});
document.addEventListener("mango:administration", () => { renderServing(); });

function aliasModels() {
  const alias = aliasRatingForm.elements.alias.value;
  return networkView?.identity ? networkView.models.filter(item => item.alias === alias).map(desired => ({ id: desired.deployment, owner: desired.owner, desired }))
    : (catalogView?.models || []).filter(item => item.alias === alias).map(local => ({ id: local.id, owner: "local", local }));
}
function renderAliasRating() {
  const fields = aliasRatingForm.elements, items = aliasModels(), label = fields.label.value.trim();
  const values = items.map(item => modelWriter(item).value.ratings?.[label] ?? null);
  const mixed = values.some(value => value !== values[0]);
  if (document.activeElement !== fields.rating) fields.rating.value = mixed ? "" : values[0] ?? "";
  fields.rating.placeholder = mixed ? "Mixed" : "Unrated";
  document.querySelector("#alias-scope").textContent = `${networkView?.identity ? "Network-wide" : "This computer"} · ${items.length} matching ${items.length === 1 ? "deployment" : "deployments"}. ${mixed ? "Mixed ratings." : ""} Editing the rating updates this capability on every matching deployment. Other ratings are preserved.`;
  const feedback = document.querySelector("#alias-feedback");
  const pending = items.map(modelWriter).filter(writer => writer.pending || writer.state.kind === "error");
  // Keep error and conflict actions attached while the user is interacting with them.
  if (feedback.contains(document.activeElement)) return;
  feedback.replaceChildren(...(pending.length ? pending.map(writer => { const row = element("div"); row.append(element("strong", writer.label)); const status = element("p"); feedbackFor(writer, status); row.append(status); return row; }) : [element("span", label && items.length ? "Up to date" : "Choose a capability to see its current rating.")]));
}
function renderAliasScope() {
  const shared = !!networkView?.identity;
  const models = shared ? networkView.models : catalogView?.models || [];
  selectOptions(aliasRatingForm.elements.alias, [...new Set(models.map(item => item.alias).filter(Boolean))].sort().map(alias => [alias, alias]), aliasRatingForm.elements.alias.value);
  renderAliasRating();
}
aliasRatingForm.addEventListener("submit", event => event.preventDefault());
aliasRatingForm.elements.alias.addEventListener("change", renderAliasRating);
aliasRatingForm.elements.label.addEventListener("input", renderAliasRating);
aliasRatingForm.elements.rating.addEventListener("input", event => {
  if (event.isComposing) return;
  const fields = aliasRatingForm.elements;
  if (!aliasRatingForm.checkValidity() || !fields.label.value.trim()) { document.querySelector("#alias-feedback").textContent = "Choose an alias and enter a capability label and a rating from 0 to 5, or leave the rating blank."; return; }
  for (const item of aliasModels()) {
    const writer = modelWriter(item);
    writer.change("ratings", { ...writer.value.ratings, [fields.label.value.trim()]: inputValue(fields.rating) });
  }
  renderAliasRating();
});
aliasRatingForm.elements.rating.addEventListener("blur", () => { for (const item of aliasModels()) modelWriter(item).flush(); });
