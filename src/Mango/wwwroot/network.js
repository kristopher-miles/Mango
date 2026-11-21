"use strict";

const networkFeedback = document.querySelector("#network-status");
const networkForms = Object.fromEntries(["discover", "join", "create", "unlock", "issue", "password", "recover", "reset", "queue"].map(name => [name, document.querySelector(`#network-${name}`)]));
const networkPairing = document.querySelector("#network-pairing");
const networkLoginCode = document.querySelector("#network-login-code");
const networkScopes = { ...operationNames, imageInput: "Image input", audioInput: "Audio input" };
const networkSecretPanel = document.querySelector("#network-secret");
const networkSecret = document.querySelector("#network-secret-value");
const networkDownload = document.querySelector("#network-download");
let networkView, networkLoad = 0, queueWriter;
let memberNames = new Map();

for (const choice of document.querySelectorAll("[data-benchmark-choice], [data-role-choice]")) choice.addEventListener("change", () => choice.dataset.changed = "true");
for (const [value, text] of Object.entries(networkScopes)) {
  const label = element("label", null, "checkbox"), input = element("input");
  Object.assign(input, { type: "checkbox", name: "scopes", value });
  label.append(input, text); document.querySelector("#network-scopes").append(label);
}
function showNetworkSecret(value, title, label, download = false) {
  networkSecret.value = value;
  document.querySelector("#network-secret-title").textContent = title;
  document.querySelector("#network-secret-label").textContent = label;
  document.querySelector("#secret-feedback").textContent = "";
  networkDownload.hidden = !download;
  networkSecretPanel.showModal(); networkSecret.focus(); networkSecret.select();
}
function showRecoveryRecord(value) {
  showNetworkSecret(value, "Keep your recovery record", "Store this recovery record outside the network. Download it to keep or print it.", true);
}
function hideNetworkSecret() { networkSecretPanel.close(); }
networkSecretPanel.addEventListener("close", () => { networkSecret.value = ""; });
document.querySelector("#network-hide-secret").addEventListener("click", hideNetworkSecret);
document.querySelector("#network-copy-secret").addEventListener("click", event => perform(event.currentTarget, document.querySelector("#secret-feedback"), async () => {
  await navigator.clipboard.writeText(networkSecret.value); return "Copied.";
}));
networkDownload.addEventListener("click", () => {
  const url = URL.createObjectURL(new Blob([networkSecret.value, "\n"], { type: "text/plain" }));
  const link = element("a"); link.href = url; link.download = "mango-recovery.txt"; link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
});
function showDelivery(result) {
  return !result || result.confirmed + result.pending === 0 ? "Updated on this computer." : `Updated here · ${result.confirmed} peer confirmations · ${result.pending} pending`;
}
function networkAction(button, work, feedback = networkFeedback) { return perform(button, feedback, work); }
function networkList(id, rows) {
  const list = document.querySelector(`#network-${id}`), focused = list.contains(document.activeElement) ? document.activeElement.dataset.action : null;
  if (list.innerHTML === rows.map(row => row.outerHTML).join("")) return;
  list.replaceChildren(...rows);
  if (focused) [...list.querySelectorAll("[data-action]")].find(node => node.dataset.action === focused)?.focus({ preventScroll: true });
}

let lockNoticeTimer;
function showLockedEdit() {
  const control = document.querySelector("#admin-control"), toast = document.querySelector("#admin-toast");
  if (control.dataset.locked !== "true") return;
  control.classList.add("attention");
  toast.hidden = false;
  toast.querySelector("[role=status]").textContent = "Network administration is locked.";
  clearTimeout(lockNoticeTimer);
  lockNoticeTimer = setTimeout(() => {
    control.classList.remove("attention");
    if (!toast.contains(document.activeElement)) toast.hidden = true;
  }, 4500);
}
document.querySelector("#toast-unlock").addEventListener("click", () => {
  document.querySelector("#admin-control").focus();
  document.querySelector("#admin-toast").hidden = true;
  openAdministration();
});
document.querySelector("#admin-toast").addEventListener("focusout", event => {
  if (!event.currentTarget.contains(event.relatedTarget)) event.currentTarget.hidden = true;
});
document.addEventListener("pointerdown", event => {
  if (event.target.closest("#network-issue, #network-password, #network-reset, #network-queue, #logs-settings, #logs-clear, #network-keys, #network-models") &&
      event.target.closest("fieldset:disabled, button:disabled")) showLockedEdit();
}, true);
function renderAdministration() {
  if (administration.pendingRevocation && Date.parse(administration.pendingRevocation.expiresAt) <= Date.now()) administration.pendingRevocation = null;
  const active = !!networkView?.localNode && !networkView.reenroll;
  const unlocked = !!administration.token;
  const label = document.querySelector("#admin-label");
  const minutes = Math.max(1, Math.ceil((Date.parse(administration.expiresAt) - Date.now()) / 60000));
  label.textContent = administration.locking ? "Locking…" : !active ? "Connect network" : unlocked ? `Unlocked · ${minutes} min` : administration.pendingRevocation ? "Locked · Sign-out unconfirmed" : "Locked · Unlock";
  document.querySelector("#admin-control").title = active ? "Network administration · " + label.textContent : label.textContent;
  if (unlocked || !active) document.querySelector("#admin-toast").hidden = true;
  document.querySelector("#admin-control").dataset.locked = String(active && !unlocked);
  document.querySelector("#admin-control use").setAttribute("href", `/glyphs.svg#${!active ? "network" : unlocked ? "unlock" : "lock"}`);
  document.querySelector("#admin-lock").hidden = !unlocked && !administration.pendingRevocation;
  document.querySelector("#admin-lock").textContent = administration.pendingRevocation ? "Retry sign-out" : "Lock administration now";
  document.querySelector("#admin-lock").disabled = administration.locking;
  document.querySelector("#admin-control").disabled = administration.locking;
  for (const form of [networkForms.issue, networkForms.password, networkForms.reset, networkForms.queue]) form.querySelector("fieldset") ? form.querySelector("fieldset").disabled = !unlocked : [...form.elements].filter(input => !input.hasAttribute("data-admin-unlock")).forEach(input => input.disabled = !unlocked);
  for (const button of [networkPairing, networkLoginCode]) {
    button.hidden = !active;
    button.disabled = administration.locking || !networkView?.ready;
  }
  for (const button of document.querySelectorAll("[data-admin-unlock]")) button.hidden = !active || unlocked;
  const feedback = document.querySelector("#queue-feedback");
  if (!unlocked) { feedback.textContent = "Unlock administration in the top bar to change the queue."; delete feedback.dataset.feedback; }
  else if (queueWriter) feedbackFor(queueWriter, feedback);
}
function setAdministration(session) {
  clearTimeout(administration.timer);
  administration.token = session?.token || null;
  administration.expiresAt = session?.expiresAt || null;
  for (const writer of writers.values()) {
    if (!writer.requiresAdmin?.()) continue;
    if (administration.token) writer.resume(); else writer.pause();
  }
  if (session) administration.timer = setTimeout(() => {
    setAdministration(null);
    loadNetwork().catch(error => networkFeedback.textContent = error.message);
    loadLogs().catch(logError);
  }, Math.max(0, Date.parse(session.expiresAt) - Date.now()));
  renderAdministration();
  document.dispatchEvent(new CustomEvent("mango:administration"));
}
function openAdministration() {
  if (!networkView?.localNode || networkView.reenroll) { navigate("network/setup"); return; }
  const dialog = document.querySelector("#admin-dialog");
  document.querySelector("#admin-feedback").textContent = administration.token ? `Administration is unlocked until ${timeLabel(administration.expiresAt)}. Unlock again to extend this session.` : "";
  dialog.showModal(); networkForms.unlock.elements.password.focus();
}
for (const button of document.querySelectorAll("[data-admin-unlock]")) button.addEventListener("click", openAdministration);
document.querySelector("#admin-control").addEventListener("click", openAdministration);
document.querySelector("#admin-lock").addEventListener("click", async () => {
  const revocation = administration.pendingRevocation || { token: administration.token, expiresAt: administration.expiresAt };
  administration.locking = true; renderAdministration();
  const protectedWriters = [...writers.values()].filter(w => w.requiresAdmin?.());
  protectedWriters.forEach(w => w.pause("Administration is locking"));
  await Promise.allSettled(protectedWriters.map(w => w.running).filter(Boolean));
  try {
    await api("network/lock", {}, "PUT", revocation.token);
    administration.pendingRevocation = null;
    document.querySelector("#admin-dialog").close();
    setAdministration(null);
    await Promise.allSettled([loadNetwork(), loadLogs()]);
  } catch (error) {
    administration.pendingRevocation = revocation;
    setAdministration(null);
    showSessionError(new Error(`Local controls are locked; server sign-out is unconfirmed until this session expires. Retry sign-out in the top bar. ${error.message}`));
  } finally { administration.locking = false; renderAdministration(); }
});
setInterval(renderAdministration, 60000);

function renderNetworkLists(network) {
  networkList("keys", (network.keys || []).map(key => {
    const row = element("li");
    row.append(element("strong", key.name), element("p", `${key.scopes.map(scope => networkScopes[scope]).join(", ")} · ${key.priority} priority · ${key.revoked ? "Revoked" : key.expiresAt ? `Expires ${timeLabel(key.expiresAt)}` : "Never expires"}`, "muted"));
    if (!key.revoked) {
      const revoke = action("Revoke", event => networkAction(event.currentTarget, async () => {
        const result = await api(`network/keys/${key.id}`, null, "DELETE"); await loadNetwork(); return showDelivery(result);
      }, document.querySelector("#key-feedback")));
      revoke.disabled = !administration.token; revoke.dataset.action = `revoke:${key.id}`; row.append(revoke);
    }
    return row;
  }));
  const nodes = network.localNode ? [network.localNode, ...network.nodes] : [];
  memberNames = new Map(nodes.map(node => [node.id, node.name]));
  networkList("models", nodes.map(node => {
    const catalog = network.catalogs.find(item => item.node === node.id);
    const local = node.id === network.localNode.id;
    const card = element("article", null, "node-card");
    card.append(element("h3", `${node.name}${local ? " · This computer" : ""}`),
      element("p", `${catalog ? stateNames[catalog.resourceState] || catalog.role : "State unobserved"} · ${node.address}`, "path"),
      element("p", local ? "Local host" : `${node.reachableAtLastCheck == null ? "Reachability unobserved" : node.reachableAtLastCheck ? "Reached" : "Unreachable"} · Last checked ${timeLabel(node.lastChecked)}`, "muted"));
    if (catalog) card.append(element("p", `Observed ${timeLabel(catalog.observedAt)} · ${catalog.models.length} models · ${catalog.contributionReason || ""}`, "muted"));
    if (node.compatible === false) card.append(element("p", `Incompatible protocol window · This host uses ${network.release.window}`, "warning"));
    const controls = element("div", null, "actions");
    const link = element("a", "Open Serving"); link.href = `#/serving/${encodeURIComponent(node.id)}`; link.dataset.action = `serving:${node.id}`; controls.append(link);
    link.className = "icon-control"; link.title = "Open Serving on " + node.name;
    link.setAttribute("aria-label", link.title); link.replaceChildren(glyph("provider"));
    if (!local) {
      const remove = action("Remove computer", event => {
        if (confirm(`Remove ${node.name}? It will need to enroll again.`)) networkAction(event.currentTarget, async () => {
          const result = await api(`network/nodes/${node.id}`, null, "DELETE"); await loadNetwork(); return showDelivery(result);
        });
      }); remove.disabled = !administration.token; remove.dataset.action = `remove:${node.id}`; controls.append(remove);
    }
    card.append(controls); return card;
  }));
}
async function loadNetwork() {
  const revision = ++networkLoad, sentAdministration = administration.token, network = await api("network");
  if (revision !== networkLoad) return;
  networkView = network;
  if (!network.unlocked && administration.token && sentAdministration === administration.token && !administration.locking) setAdministration(null);
  const active = network.localNode && !network.reenroll;
  globalThis.mangoEnrolled = !!active;
  const setupTab = document.querySelector("#tab-network-setup");
  if (setupTab) setupTab.hidden = !!active && route.page !== "network/setup";
  document.querySelector("#network-setup-link").hidden = !!active;
  renderInferenceAddress(network);
  document.querySelector("#setup-membership").hidden = !active;
  document.querySelector("#network-setup").hidden = !!active;
  document.querySelector("[aria-label='Setup path']").hidden = !!active;
  document.querySelector("#setup-description").textContent = active ? "This computer is already connected. Configure its providers and models in Serving." : "Create a new network or join an existing one. Successful setup opens Serving with administration unlocked.";
  document.querySelector("#setup-membership").href = `#/serving/${network.localNode?.id || "local"}`;
  document.querySelector("#sidebar-node").textContent = network.localNode?.name || "This computer";
  document.querySelector("#sidebar-network").textContent = network.reenroll ? "Reenrollment required" : active ? network.isolated ? "Network isolated" : network.ready ? "Network connected" : "Network reconciling" : "Local workspace";
  document.querySelector("#network-tailnet").textContent = network.tailnet.error || network.tailnet.message;
  document.querySelector("#network-addresses").textContent = network.tailnet.addresses.join(" · ");
  const warning = document.querySelector("#network-recovery"); warning.textContent = network.recoveryWarning || ""; warning.hidden = !network.recoveryWarning;
  document.querySelector("#network-local").textContent = active ? `${network.localNode.name} · Network ${network.identity.id}` : "This computer has not joined a network.";
  document.querySelector("#network-key-policy").textContent = network.nodes.length === 0 ? "Keys take effect on this single-node network." : "Key changes require a reachable peer. Unreachable peers may use their last known key state until they reconcile.";
  if (network.identity) networkForms.join.elements.network.value = network.identity.id;
  for (const form of [networkForms.join, networkForms.create, networkForms.discover]) form.querySelector("fieldset").disabled = !!active;
  document.querySelector("#setup-create").disabled = !!network.identity;
  queueWriter = recordWriter("queue", { value: { waitSeconds: network.queueWaitSeconds }, label: "Queue wait", page: "network/queue", requiresAdmin: () => true,
    read: async () => ({ waitSeconds: (await api("network")).queueWaitSeconds }),
    write: async ({ changes, baseline }) => {
      await api("network/queue", { ...changes, expectedWaitSeconds: baseline.waitSeconds });
      return { waitSeconds: (await api("network")).queueWaitSeconds };
    } });
  bindPreferences(queueWriter, networkForms.queue, document.querySelector("#queue-feedback"));
  renderNetworkLists(network); renderAdministration();
  renderServing();
}
function networkForm(name, feedbackId, work) {
  const form = networkForms[name];
  form.addEventListener("submit", event => {
    event.preventDefault();
    const fields = new FormData(form);
    const values = Object.fromEntries(fields);
    for (const password of form.querySelectorAll('input[type="password"]')) password.value = "";
    networkAction(event.submitter || form.querySelector('button[type="submit"]'), async () => {
      try { return await work(values, fields); }
      finally { for (const key of ["password", "newPassword", "recoveryRecord"]) { delete values[key]; fields.delete(key); } }
    }, document.querySelector(`#${feedbackId}`));
  });
}
networkForm("discover", "setup-feedback", async fields => {
  const results = await api("network/discover", fields);
  networkList("discovered", results.map(result => {
    const row = element("li", `${result.address} · ${result.peer.identity.id} · Mango ${result.release.version}${result.compatible ? "" : " · Incompatible protocol window"}`);
    if (result.compatible) row.append(action("Join this network", () => {
      networkForms.join.elements.address.value = result.address; networkForms.join.elements.network.value = result.peer.identity.id; networkForms.join.elements.name.focus();
    })); return row;
  }));
  return results.length ? "Choose a peer to join." : "No peer reached. Enter a known address and network ID, or retry discovery.";
});
async function enroll(kind, fields) {
  const { setup: result, session, unlockError, interrupted } = await connectNetwork(kind, fields, api);
  if (session) setAdministration(session);
  await Promise.allSettled([loadSettings(), loadNetwork(), loadProviders(), loadLogs()]);
  navigate(`serving/${result.node}`);
  if (result.recoveryRecord) showRecoveryRecord(result.recoveryRecord);
  if (interrupted && kind === "create") showSessionError(new Error("The network was created, but its one-time recovery record was lost with the interrupted response. Mango cannot show it again. Keep the master password securely."));
  if (unlockError) {
    showSessionError(new Error(`Network ${kind === "create" ? "created" : "joined"}. Automatic unlock failed: ${unlockError.message} Use Unlock in the top bar; setup is already complete.`));
    return "Connected. Use Unlock in the top bar to finish administration sign-in.";
  }
  return "Connected and unlocked. Configure this computer in Serving.";
}
networkForm("create", "setup-feedback", fields => enroll("create", fields));
networkForm("join", "setup-feedback", fields => enroll("join", fields));
networkForm("unlock", "admin-feedback", async fields => {
  if (administration.pendingRevocation && Date.parse(administration.pendingRevocation.expiresAt) > Date.now())
    await api("network/lock", {}, "PUT", administration.pendingRevocation.token);
  administration.pendingRevocation = null;
  setAdministration(await api("network/unlock", fields));
  document.querySelector("#admin-dialog").close();
  await Promise.allSettled([loadNetwork(), loadLogs()]); return "Administration unlocked.";
});
networkForms.issue.elements.expiry.addEventListener("change", event => {
  const dated = event.target.value === "date"; document.querySelector("#network-expiry").hidden = !dated; networkForms.issue.elements.expiresAt.required = dated;
});
networkForm("issue", "key-feedback", async (values, fields) => {
  const result = await api("network/keys", { name: values.name, priority: values.priority, scopes: fields.getAll("scopes"),
    neverExpires: values.expiry === "never", expiresAt: values.expiry === "date" ? new Date(values.expiresAt).toISOString() : null }, "POST");
  showNetworkSecret(result.secret, "Your inference key", "Copy this key now. Mango cannot display the secret again.");
  await loadNetwork(); return showDelivery(result.delivery);
});
networkPairing.addEventListener("click", () => {
  if (!administration.token) { openAdministration(); return; }
  networkAction(networkPairing, async () => {
    const result = await api("network/pairing-code", null, "POST");
    showNetworkSecret(result.code, "One-time pairing code",
      `Run Mango --pair <code> on the computer joining the network. The code expires after one use or at ${timeLabel(result.expiresAt)}.`);
    return "Pairing code created.";
  });
});
networkLoginCode.addEventListener("click", () => {
  if (!administration.token) { openAdministration(); return; }
  networkAction(networkLoginCode, async () => {
    const result = await api("network/login-code", null, "POST");
    showNetworkSecret(result.code, "One-time login code",
      `Use this code to log in to any updated computer on your Mango network. It expires after one use or at ${timeLabel(result.expiresAt)}.`);
    return "Login code created.";
  });
});
networkForm("password", "recovery-feedback", async fields => {
  const change = { newPassword: fields.newPassword };
  let result = await api("network/password", change);
  if (!result.delivery) {
    if (!confirm(`Mango could not reach ${result.unreachableNodes.join(", ")}. These computers will keep using the previous password until they reconnect. Change the password now?`))
      return "Password change cancelled.";
    result = await api("network/password", { ...change, allowPending: true });
  }
  setAdministration(null);
  await Promise.allSettled([loadNetwork(), loadLogs()]);
  return `Master password changed. ${showDelivery(result.delivery)}`;
});
networkForm("recover", "recovery-feedback", async fields => {
  const result = await api("network/recover", fields); setAdministration(null); await Promise.allSettled([loadNetwork(), loadLogs()]); return showDelivery(result);
});
networkForm("reset", "recovery-feedback", async fields => {
  const result = await api("network/reset", { newPassword: fields.newPassword, confirm: fields.confirm === "on" });
  setAdministration(null); showRecoveryRecord(result.recoveryRecord); await Promise.allSettled([loadNetwork(), loadLogs()]); return showDelivery(result.delivery);
});
document.querySelector("#network-refresh").addEventListener("click", event => networkAction(event.currentTarget, async () => {
  await api("network/refresh", {}); await loadNetwork(); return "Network refreshed.";
}));
function setupPath(create) {
  networkForms.discover.hidden = create; networkForms.join.hidden = create;
  document.querySelector("#network-discovered").hidden = create;
  document.querySelector("#network-new-panel").hidden = !create;
  document.querySelector("#setup-create").setAttribute("aria-pressed", String(create));
  document.querySelector("#setup-join").setAttribute("aria-pressed", String(!create));
}
document.querySelector("#setup-create").addEventListener("click", () => setupPath(true));
document.querySelector("#setup-join").addEventListener("click", () => setupPath(false));
setupPath(false);
