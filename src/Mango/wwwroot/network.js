"use strict";

const networkFeedback = document.querySelector("#network-status");
const networkForms = Object.fromEntries(
  ["discover", "join", "create", "unlock", "issue", "recover", "reset", "queue"].map(name => [name, document.querySelector(`#network-${name}`)])
);
for (const choice of document.querySelectorAll("[data-benchmark-choice], [data-role-choice]"))
  choice.addEventListener("change", () => choice.dataset.changed = "true");
const networkScopes = { ...operationNames, imageInput: "Image input", audioInput: "Audio input" };
const networkSecretPanel = document.querySelector("#network-secret");
const networkSecret = document.querySelector("#network-secret-value");
const networkDownload = document.querySelector("#network-download");

for (const [value, text] of Object.entries(networkScopes)) {
  const label = element("label", null, "checkbox");
  const input = element("input");
  input.type = "checkbox";
  input.name = "scopes";
  input.value = value;
  label.append(input, text);
  document.querySelector("#network-scopes").append(label);
}

function showNetworkSecret(value, recovery) {
  networkSecret.value = value;
  document.querySelector("#network-secret-label").textContent = recovery
    ? "Store this recovery record outside the network. Download it to save or print it."
    : "Copy this inference key now. Mango cannot display the secret again.";
  networkDownload.hidden = !recovery;
  networkSecretPanel.hidden = false;
  networkSecret.focus();
  networkSecret.select();
}

function hideNetworkSecret() {
  networkSecret.value = "";
  networkSecretPanel.hidden = true;
}

document.querySelector("#network-hide-secret").addEventListener("click", hideNetworkSecret);
networkDownload.addEventListener("click", () => {
  const url = URL.createObjectURL(new Blob([networkSecret.value, "\n"], { type: "text/plain" }));
  const link = document.createElement("a");
  link.href = url;
  link.download = "mango-recovery.txt";
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
});

function showDelivery(result) {
  if (!result || result.confirmed + result.pending === 0) return "Saved on this node.";
  return `Saved locally. ${result.confirmed} peer confirmations, ${result.pending} pending.`;
}

async function networkAction(button, work) {
  button.disabled = true;
  networkFeedback.classList.remove("error");
  networkFeedback.textContent = "Working…";
  try {
    networkFeedback.textContent = (await work()) || "Done.";
  } catch (error) {
    networkFeedback.classList.add("error");
    networkFeedback.textContent = error.message;
  } finally {
    button.disabled = false;
  }
}

function networkList(id, rows) {
  document.querySelector(`#network-${id}`).replaceChildren(...rows);
}

let networkLoad = 0;
let unlockExpiry;

function renderNetworkLists(network) {
  const focused = document.activeElement?.dataset.focusKey;
  networkList("keys", network.keys.map(key => {
    const expiry = key.expiresAt ? timeLabel(key.expiresAt) : "Never expires";
    const row = element("li", `${key.name} · ${key.id} · ${key.scopes.map(scope => networkScopes[scope]).join(", ")} · ${key.priority} · ${key.revoked ? "Revoked" : expiry}`);
    if (network.unlocked && !key.revoked)
      row.append(catalogAction(`key:${key.id}`, "Revoke", event => networkAction(event.currentTarget, async () => {
        const result = await api(`network/keys/${key.id}`, null, "DELETE");
        await loadNetwork();
        return showDelivery(result);
      })));
    return row;
  }));
  renderNetworkCatalog(network);
  if (focused) document.querySelector(`[data-focus-key="${CSS.escape(focused)}"]`)?.focus({ preventScroll: true });
}

async function loadNetwork() {
  const revision = ++networkLoad;
  const network = await api("network");
  if (revision !== networkLoad) return;
  const active = network.localNode && !network.reenroll;
  const state = network.reenroll ? "Reenrollment required" : active
    ? (network.isolated ? "Isolated" : network.ready ? "Enrolled" : "Reconciling") : "Not configured";
  document.querySelector("#network-summary").textContent = state;
  document.querySelector("#network-tailnet").textContent = network.tailnet.error || network.tailnet.message;
  const warning = document.querySelector("#network-recovery");
  warning.textContent = network.recoveryWarning || "";
  warning.hidden = !network.recoveryWarning;
  document.querySelector("#network-addresses").textContent = network.tailnet.addresses.join(" · ");
  document.querySelector("#network-setup").hidden = !!active;
  document.querySelector("#network-enrolled").hidden = !active;
  document.querySelector("#network-new-panel").hidden = !!network.identity;
  document.querySelector("#network-unlocked").hidden = !network.unlocked;
  document.querySelector("#network-reset-panel").hidden = !network.unlocked;
  networkForms.issue.hidden = !network.unlocked;
  networkForms.queue.hidden = !network.unlocked;
  if (document.activeElement !== networkForms.queue.elements.waitSeconds)
    networkForms.queue.elements.waitSeconds.value = network.queueWaitSeconds;
  if (!active) {
    memberNames = new Map();
    if (network.identity) networkForms.join.elements.network.value = network.identity.id;
    return;
  }
  document.querySelector("#network-local").textContent =
    `${network.localNode.name} · Node ${network.localNode.id} · Network ${network.identity.id}`;
  document.querySelector("#network-key-policy").textContent = network.nodes.length === 0
    ? "This is a single-node network. Key changes take effect here."
    : "Key changes require a reachable peer. Unreachable peers may continue using their last known key state until they reconcile.";
  renderNetworkLists(network);
}

function networkForm(name, work) {
  const form = networkForms[name];
  form.addEventListener("submit", event => {
    event.preventDefault();
    const fields = new FormData(form);
    for (const password of form.querySelectorAll('input[type="password"]')) password.value = "";
    networkAction(form.querySelector('button[type="submit"]'), () => work(Object.fromEntries(fields), fields));
  });
}

networkForm("discover", async fields => {
  const results = await api("network/discover", fields);
  networkList("discovered", results.map(result => {
    const row = element("li", `${result.address} · ${result.peer.identity.id}`);
    row.append(action("Select", () => {
      networkForms.join.elements.address.value = result.address;
      networkForms.join.elements.network.value = result.peer.identity.id;
      networkForms.join.elements.name.focus();
    }));
    return row;
  }));
  await loadNetwork();
  return results.length ? "Select a peer to join." : "No Mango peer reached. Try a known address and network ID, or retry discovery.";
});
networkForm("create", async fields => {
  const result = await api("network/create", fields);
  showNetworkSecret(result.recoveryRecord, true);
  if (!dirty) await loadSettings();
  await loadNetwork();
  return "Network created.";
});
networkForm("join", async fields => {
  await api("network/join", fields);
  if (!dirty) await loadSettings();
  await loadNetwork();
  return "Joined the selected network.";
});
networkForm("unlock", async fields => {
  const unlocked = await api("network/unlock", fields);
  networkAdmin = unlocked.token;
  clearTimeout(unlockExpiry);
  unlockExpiry = setTimeout(() => {
    if (networkAdmin === unlocked.token) {
      networkAdmin = null;
      loadNetwork().catch(error => networkFeedback.textContent = error.message);
    }
  }, Math.max(0, Date.parse(unlocked.expiresAt) - Date.now()));
  await loadNetwork();
  return "Network administration unlocked.";
});
networkForm("queue", async fields => {
  await api("network/queue", { waitSeconds: Number(fields.waitSeconds) });
  await loadNetwork();
  return "Queue limit saved.";
});
networkForms.issue.elements.expiry.addEventListener("change", event => {
  const dated = event.target.value === "date";
  document.querySelector("#network-expiry").hidden = !dated;
  networkForms.issue.elements.expiresAt.required = dated;
});
networkForm("issue", async (values, fields) => {
  const result = await api("network/keys", {
    name: values.name, priority: values.priority, scopes: fields.getAll("scopes"),
    neverExpires: values.expiry === "never",
    expiresAt: values.expiry === "date" ? new Date(values.expiresAt).toISOString() : null
  }, "POST");
  showNetworkSecret(result.secret, false);
  await loadNetwork();
  return showDelivery(result.delivery);
});
networkForm("recover", async fields => {
  const delivery = await api("network/recover", fields);
  networkAdmin = null;
  await loadNetwork();
  return showDelivery(delivery);
});
networkForm("reset", async fields => {
  const result = await api("network/reset", { newPassword: fields.newPassword, confirm: fields.confirm === "on" });
  networkAdmin = null;
  showNetworkSecret(result.recoveryRecord, true);
  await loadNetwork();
  return showDelivery(result.delivery);
});

document.querySelector("#network-refresh").addEventListener("click", event => networkAction(event.currentTarget, async () => {
  await api("network/refresh", {});
  await loadNetwork();
  return "Network refreshed.";
}));

if (token) loadNetwork().catch(error => {
  networkFeedback.classList.add("error");
  networkFeedback.textContent = error.message;
});
