"use strict";

const modelIntentForm = document.querySelector("#network-model-intent");
const providerIntentForm = document.querySelector("#network-provider-intent");
const providerIntentAdd = document.querySelector("#network-provider-add");
const ratingRows = document.querySelector("#network-rating-rows");
let modelIntent;
let providerIntent;
let memberNames = new Map();
let providerOwners = [];
const servingFields = ["servedContext", "embeddingDimensions", "embeddingFamily", "mediaTokensPerItem"];
const benchmarkNames = { never: "Never", now: "Now", whenConvenient: "When convenient" };
const stateNames = { contributor: "Contributing", withdrawing: "Withdrawing", cooldown: "Cooling down",
  manualLeech: "Leech", fault: "Resource protection fault" };

for (const [key, name] of Object.entries(profileNames)) {
  const option = element("option", name);
  option.value = key;
  providerIntentForm.elements.profile.append(option);
}

function timeLabel(time) {
  return time ? new Date(time).toLocaleString() : "Unknown";
}

function sameRevision(left, right) {
  return left && right && left.time === right.time && left.writer === right.writer;
}

function intentLabel(desired, report) {
  if (!sameRevision(desired.revision, report?.revision)) return "Pending host application";
  const label = { applied: "Applied at last observation", pending: "Pending host resources",
    localAction: "Local action required", rejected: "Rejected by owning host" }[report.state];
  return report.reason ? `${label} · ${report.reason}` : label;
}

function ratingText(ratings) {
  return Object.entries(ratings ?? {}).map(([label, value]) => `${label} ${value ?? "unrated"}`).join(" · ") || "No capability ratings";
}

function catalogAction(key, label, handler) {
  const button = action(label, handler);
  button.dataset.focusKey = key;
  return button;
}

function modelRequest(desired, observed) {
  modelIntent = desired;
  modelIntentForm.reset();
  modelIntentForm.elements.alias.value = desired.alias;
  for (const key of servingFields) {
    const field = modelIntentForm.elements[key];
    field.value = (desired.serving ?? observed?.effectiveServing)?.[key] ?? "";
    field.closest("label").hidden = !!observed && !field.value && (
      (key === "embeddingDimensions" || key === "embeddingFamily") && !observed.operations.includes("embeddings") ||
      key === "mediaTokensPerItem" && !observed.inputs.some(modality => modality === "image" || modality === "audio"));
  }
  document.querySelector("#network-model-edit-title").textContent = `Requested settings for ${desired.alias}`;
  ratingRows.replaceChildren(...Object.entries(desired.ratings).map(([label, rating]) => ratingRow(label, rating)));
  providerIntentForm.hidden = true;
  modelIntentForm.hidden = false;
  modelIntentForm.elements.alias.focus();
}

function providerRequest(desired) {
  providerIntent = desired;
  providerIntentForm.reset();
  const fields = providerIntentForm.elements;
  fields.owner.replaceChildren(...providerOwners.map(owner => {
    const option = element("option", owner.name);
    option.value = owner.id;
    return option;
  }));
  fields.owner.disabled = !!desired;
  if (desired) {
    fields.owner.value = desired.owner;
    fields.profile.value = desired.profile;
    fields.address.value = desired.address;
    fields.enabled.checked = desired.enabled;
  }
  modelIntentForm.hidden = true;
  providerIntentForm.hidden = false;
  (desired ? fields.profile : fields.owner).focus();
}

providerIntentAdd.addEventListener("click", () => providerRequest(null));
document.querySelector("#network-provider-cancel").addEventListener("click", () => {
  providerIntentForm.hidden = true;
  providerIntentAdd.focus();
});
document.querySelector("#network-model-cancel").addEventListener("click", () => {
  modelIntentForm.hidden = true;
  document.querySelector("#network-models summary")?.focus();
});
document.querySelector("#network-rating-add").addEventListener("click", () => {
  const row = ratingRow("", null);
  ratingRows.append(row);
  row.querySelector("input").focus();
});

modelIntentForm.addEventListener("submit", event => {
  event.preventDefault();
  const fields = modelIntentForm.elements;
  const values = Array.from(ratingRows.children, row => {
    const [label, rating] = row.querySelectorAll("input");
    return [label.value.trim(), rating.value === "" ? null : rating.valueAsNumber];
  });
  networkAction(event.submitter, async () => {
    if (new Set(values.map(([label]) => label)).size !== values.length) throw new Error("Use each capability label once.");
    const serving = Object.fromEntries(servingFields.map(key => [key, !fields[key].value ? null :
      key === "embeddingFamily" ? fields[key].value.trim() : fields[key].valueAsNumber]));
    const delivery = await api("network/models", { owner: modelIntent.owner, deployment: modelIntent.deployment,
      revision: modelIntent.revision, alias: fields.alias.value, ratings: Object.fromEntries(values), serving });
    modelIntentForm.hidden = true;
    await loadNetwork();
    document.querySelector("#network-models summary")?.focus();
    return `Model request committed. ${showDelivery(delivery)}`;
  });
});

providerIntentForm.addEventListener("submit", event => {
  event.preventDefault();
  const fields = providerIntentForm.elements;
  const update = { owner: fields.owner.value, id: providerIntent?.id, revision: providerIntent?.revision,
    profile: fields.profile.value, address: fields.address.value, enabled: fields.enabled.checked };
  networkAction(event.submitter, async () => {
    const delivery = await api("network/providers", update);
    providerIntentForm.hidden = true;
    await loadNetwork();
    providerIntentAdd.focus();
    return `Provider request committed. ${showDelivery(delivery)}`;
  });
});

function providerObservation(desired, observed, local, unlocked) {
  const item = element("div", undefined, "provider-row");
  item.append(element("strong", profileNames[desired.profile] || desired.profile));
  item.append(element("p", `Requested ${desired.enabled ? desired.address : "disabled"} · ${intentLabel(desired, observed?.intent)}`));
  const reachability = observed?.reachable == null ? "Inventory unconfirmed" : observed.reachable ? "Reached when observed" : "Unreachable when observed";
  item.append(element("p", observed?.address
    ? `Applied ${observed.address} · ${reachability}${observed.version ? " · " + observed.version : ""} · ${timeLabel(observed.observedAt)}`
    : "No applied provider registration observed.", "muted"));
  const buttons = element("div", undefined, "actions");
  if (unlocked) buttons.append(catalogAction(`provider:${desired.id}`, "Edit requested provider", () => providerRequest(desired)));
  if (local && sameRevision(desired.revision, observed?.intent?.revision) && observed.intent.state === "localAction") {
    if (desired.enabled) {
      buttons.append(catalogAction(`review:${desired.id}`, "Review on this host", event => networkAction(event.currentTarget, async () => {
        const catalog = await api("providers");
        const endpoint = catalog.endpoints.find(current => current.id === desired.id);
        const matching = endpoint?.profile === desired.profile && endpoint?.address === desired.address;
        editProvider({ ...(matching ? endpoint : { mode: "attached" }), id: desired.id,
          profile: desired.profile, address: desired.address });
        return "Review the local provider form and supply any required credential.";
      })));
    } else {
      buttons.append(catalogAction(`disable:${desired.id}`, "Confirm disable on this host", event => networkAction(event.currentTarget, async () => {
        await api(`providers/${desired.id}`, undefined, "DELETE");
        await loadNetwork();
        await loadProviders();
        return "Provider disabled on this host.";
      })));
    }
  }
  if (buttons.childElementCount) item.append(buttons);
  return item;
}

function modelObservation(desired, observed, unlocked) {
  const item = element("div", undefined, "provider-row");
  item.append(element("strong", desired.alias));
  item.append(element("p", `Requested · ${ratingText(desired.ratings)} · ${intentLabel(desired, observed?.intent)}`, "muted"));
  if (desired.serving?.servedContext != null)
    item.append(element("p", `Requested context ${desired.serving.servedContext} tokens`, "muted"));
  if (observed) {
    const modelStatus = observed.busy ? "Busy when observed" : observed.loaded ? "Loaded when observed" : "Unloaded when observed";
    item.append(element("p", `Applied alias ${observed.effectiveAlias || desired.alias} · ${modelStatus}${observed.locallyAvailable ? "" : " · Unavailable for routing when observed"}`));
    item.append(element("p", [observed.displayName, observed.providerVersion,
      `Context ${observed.context ?? "unknown"}${observed.contextSource ? " (" + observed.contextSource + ")" : ""}`,
      ...observed.operations.map(operation => operationNames[operation])].filter(Boolean).join(" · "), "muted"));
    item.append(element("p", `Input ${observed.inputs.join(", ") || "unreported"} · Output ${observed.outputs.join(", ") || "unreported"}`, "muted"));
    item.append(element("p", `Benchmarks ${observed.benchmarksComplete ?? 0} complete, ${observed.benchmarksPartial ?? 0} partial · ${ratingText(observed.effectiveRatings)}`, "muted"));
    if (observed.workloads?.length) item.append(element("p", observed.workloads.map(work =>
      `${operationNames[work.limit.operation]}: ${work.slots} qualified slot${work.slots === 1 ? "" : "s"}`).join(" · "), "muted"));
  } else {
    item.append(element("p", "No applied deployment observed.", "muted"));
  }
  if (unlocked) item.append(catalogAction(`model:${desired.id}`, "Edit requested model", () => modelRequest(desired, observed)));
  return item;
}

function renderNetworkCatalog(network) {
  providerOwners = [network.localNode, ...network.nodes];
  memberNames = new Map(providerOwners.map(node => [node.id, node.name]));
  providerIntentAdd.hidden = !network.unlocked;
  if (!network.unlocked) {
    providerIntentForm.hidden = true;
    modelIntentForm.hidden = true;
  }
  const list = document.querySelector("#network-models");
  const firstRender = list.childElementCount === 0;
  const expanded = new Set(Array.from(list.querySelectorAll("details[open]"), details => details.dataset.node));
  const catalogs = new Map(network.catalogs.map(catalog => [catalog.node, catalog]));
  list.replaceChildren(...[...providerOwners].sort((left, right) => left.joinOrder - right.joinOrder).map(node => {
    const catalog = catalogs.get(node.id);
    const local = node.id === network.localNode.id;
    const details = element("details", undefined, "deployment");
    details.dataset.node = node.id;
    details.open = expanded.has(node.id) || firstRender && local;
    const role = !catalog ? "Role unobserved" : catalog.role === "contributor" ? "Contributing" :
      catalog.resourceState === "contributor" ? "Leech" : stateNames[catalog.resourceState] ?? "Leech";
    const summary = element("summary", `#${node.joinOrder} ${node.name}${local ? " (this node)" : ""} · ${role}`);
    summary.dataset.focusKey = `node:${node.id}`;
    details.append(summary);
    const reachability = local ? "Local host" : node.reachableAtLastCheck == null ? "Reachability unobserved"
      : node.reachableAtLastCheck ? `Reached ${timeLabel(node.lastChecked)}` : `Unreachable when checked ${timeLabel(node.lastChecked)}`;
    details.append(element("p", `${reachability} · ${node.address}${node.current ? " · Catalog matched at last contact" : ""}`, "path"));
    if (catalog) details.append(element("p", `Observed ${timeLabel(catalog.observedAt)} · Mango ${catalog.appVersion ?? "version unobserved"} · Role preference ${catalog.preferredRole || catalog.role} · Benchmark policy ${benchmarkNames[catalog.localBenchmarkPolicy] ?? "unobserved"}`, "muted"));
    if (catalog?.contributionReason) details.append(element("p", catalog.contributionReason, "muted"));
    for (const desired of network.providerIntents.filter(item => item.owner === node.id))
      details.append(providerObservation(desired, catalog?.providers?.find(item => item.id === desired.id), local, network.unlocked));
    for (const desired of network.models.filter(item => item.owner === node.id))
      details.append(modelObservation(desired, catalog?.models.find(item => item.id === desired.deployment), network.unlocked));
    if (!local && network.unlocked) {
      const actions = element("div", undefined, "actions");
      actions.append(catalogAction(`remove:${node.id}`, "Remove from network", event => {
        if (!confirm(`Remove ${node.name} from the network? This node will need to enroll again.`)) return;
        networkAction(event.currentTarget, async () => {
          const result = await api(`network/nodes/${node.id}`, undefined, "DELETE");
          await loadNetwork();
          return showDelivery(result);
        });
      }));
      details.append(actions);
    }
    return details;
  }));
}
