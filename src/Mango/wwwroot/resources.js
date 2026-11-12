"use strict";

const resourceForm = document.querySelector("#resource-form");
const resourceTarget = resourceForm.elements.target;
const resourceFeedback = document.querySelector("#resource-feedback");
const resourceGpus = document.querySelector("#resource-gpus");
const mib = 1024 ** 2;
const resourceStates = {
  contributor: "Contributor", withdrawing: "Withdrawing", cooldown: "Automatic cooldown",
  manualLeech: "Manual leech", fault: "Release or telemetry fault"
};
const resourceOperations = {
  chat: "Chat", embeddings: "Embeddings", systemOne: "System One", images: "Images",
  speech: "Speech", transcription: "Transcription"
};
const resourceKey = model => `${model.id}/${model.operation}`;
let resourceView;
let resourceDirty = false;
let resourceLoading = false;

function bytes(value) {
  return value == null ? "unavailable" : `${(value / mib).toFixed(1)} MiB`;
}

function percent(value) {
  return value == null ? "unavailable" : `${value.toFixed(0)}%`;
}

function renderResourceProfile(model) {
  const profile = model.profile;
  const detail = document.querySelector("#resource-profile");
  const active = resourceView.benchmark?.deployment === model.id;
  const cold = model.canColdLoad ? "" : " Cold-load measurement is unavailable for this provider.";
  const progress = profile?.error || (profile?.complete ? "Benchmark complete."
    : profile?.measurements.length ? "Benchmark measurements are incomplete." : "No benchmark measurements.");
  detail.textContent = `${model.unsafe ? "Allocation failed. Update limits or retry before using this placement. " : ""}${progress}${cold}`;
  document.querySelector("#resource-retry").disabled = !model.budget || active;
  document.querySelector("#resource-passes").replaceChildren(...(profile?.measurements ?? []).map(pass => {
    const rate = pass.tokensPerSecond == null ? "" : ` · ${pass.tokensPerSecond.toFixed(1)} tokens/s`;
    const counts = pass.promptTokens == null ? "" : ` · ${pass.promptTokens} prompt tokens`;
    const first = pass.firstToken == null ? "" : ` · first token ${pass.firstToken}`;
    const prompt = pass.promptMilliseconds == null ? "" : ` · prompt processing ${pass.promptMilliseconds.toFixed(1)} ms`;
    const cache = pass.reusedCache == null ? "" : pass.reusedCache ? " · cache reused" : " · cache unused";
    const ram = pass.observedIncrement == null ? " · RAM peak unavailable" : ` · observed additional RAM ${bytes(pass.observedIncrement.ramBytes)}`;
    const dimensions = pass.workload.width ? ` · ${pass.workload.width} × ${pass.workload.height}` : "";
    return element("li", `${pass.pass} · ${pass.elapsed}${dimensions}${counts}${prompt}${first}${rate}${ram}${cache}`);
  }));
}

function fillResourceForm() {
  const model = resourceView.models.find(item => resourceKey(item) === resourceTarget.value);
  if (!model) return;
  const fields = resourceForm.elements;
  const budget = model.budget;
  const text = ["chat", "embeddings", "systemOne"].includes(model.operation);
  document.querySelector("#resource-units").textContent = text
    ? "Use the served context in tokens per sequence. Items are sequences in one request."
    : model.operation === "images" ? "Use pixels per image and images per request."
      : model.operation === "speech" ? "Use input characters." : "Use input audio bytes.";
  document.querySelector("#resource-attached").hidden = !model.attached;
  fields.units.value = budget?.workload.units ?? model.context ?? "";
  fields.items.value = budget?.workload.items ?? 1;
  fields.mediaItems.value = budget?.workload.mediaItems ?? 0;
  fields.loadRamMiB.value = budget ? Math.ceil(budget.load.ramBytes / mib) : "";
  fields.executeRamMiB.value = budget ? Math.ceil(budget.execute.ramBytes / mib) : "";
  fields.allowAttached.checked = budget?.allowAttached ?? false;
  fields.allowOverlap.checked = budget?.allowOverlap ?? false;
  fields.parallelSlots.value = budget?.parallelSlots ?? 1;
  fields.parallelSlots.disabled = !fields.allowOverlap.checked;
  resourceGpus.replaceChildren();
  for (const device of resourceView.observation?.devices ?? []) {
    if (device.integrated || device.totalBytes == null) continue;
    for (const stage of ["load", "execute"]) {
      const label = element("label", `${device.name} · ${stage} VRAM peak (MiB)`);
      const input = element("input");
      Object.assign(input, { type: "number", min: 0, max: 1048576, step: 1, required: true });
      input.value = budget ? Math.ceil((budget[stage].deviceBytes[device.id] ?? 0) / mib) : "";
      input.dataset.device = device.id;
      input.dataset.stage = stage;
      label.append(input);
      resourceGpus.append(label);
    }
  }
  renderResourceProfile(model);
}

async function loadResources() {
  if (resourceLoading || !token) return;
  resourceLoading = true;
  try {
    const current = await api("resources");
    resourceView = current;
    const state = current.status;
    const quiet = state.quietUntil ? ` · quiet until ${new Date(state.quietUntil).toLocaleTimeString()}` : "";
    document.querySelector("#resource-state").textContent =
      `${resourceStates[state.state]}${state.reason ? ` · ${state.reason}` : ""}${quiet}`;
    document.querySelector("#resource-release").hidden = state.state !== "fault";
    const reading = current.observation;
    document.querySelector("#resource-observation").textContent = reading
      ? [`Free RAM ${bytes(reading.freeRam)}`, `external CPU ${percent(reading.externalCpu)}`,
        `external GPU ${percent(reading.externalGpu)}`,
        ...reading.devices.map(device => device.integrated ? `${device.name} uses shared RAM`
          : `${device.name} ${bytes(device.usedBytes)} in use of ${bytes(device.totalBytes)}`),
        reading.limitation, current.recoveryWarning].filter(Boolean).join(" · ")
      : "Waiting for Windows observations.";
    document.querySelector("#resource-benchmark").textContent = current.benchmark
      ? `Benchmark running · ${current.benchmark.pass}`
      : view?.settings.benchmarkPolicy === "now" && !current.benchmarkReady
        ? "Waiting for complete local benchmarks before serving inference." : "";
    const selected = resourceTarget.value;
    const valid = current.models.some(model => resourceKey(model) === selected);
    if (!resourceDirty || !valid) {
      resourceDirty = false;
      resourceTarget.replaceChildren(...current.models.map(model => {
        const option = element("option", `${model.alias} · ${resourceOperations[model.operation]}`);
        option.value = resourceKey(model);
        return option;
      }));
      if (valid) resourceTarget.value = selected;
      fillResourceForm();
    } else {
      renderResourceProfile(current.models.find(model => resourceKey(model) === selected));
    }
    resourceForm.hidden = current.models.length === 0;
  } finally {
    resourceLoading = false;
  }
}

async function resourceAction(button, work) {
  button.disabled = true;
  resourceFeedback.textContent = "";
  resourceFeedback.classList.remove("error");
  try {
    await work();
    await loadResources();
  } catch (error) {
    resourceFeedback.textContent = error.message;
    resourceFeedback.classList.add("error");
  } finally {
    button.disabled = false;
  }
}

resourceTarget.addEventListener("change", () => {
  resourceDirty = false;
  fillResourceForm();
});
resourceForm.addEventListener("input", event => {
  if (event.target !== resourceTarget) resourceDirty = true;
  resourceForm.elements.parallelSlots.disabled = !resourceForm.elements.allowOverlap.checked;
});
resourceForm.addEventListener("submit", event => {
  event.preventDefault();
  const [deployment, operation] = resourceTarget.value.split("/");
  const fields = resourceForm.elements;
  const payload = { deployment, operation, allowOverlap: fields.allowOverlap.checked, allowAttached: fields.allowAttached.checked,
    parallelSlots: fields.allowOverlap.checked ? fields.parallelSlots.valueAsNumber : 1 };
  for (const name of ["units", "items", "mediaItems", "loadRamMiB", "executeRamMiB"]) payload[name] = fields[name].valueAsNumber;
  for (const [stage, key] of [["load", "loadVramMiB"], ["execute", "executeVramMiB"]])
    payload[key] = Object.fromEntries(Array.from(resourceGpus.querySelectorAll(`[data-stage="${stage}"]`))
      .filter(input => input.value !== "").map(input => [input.dataset.device, input.valueAsNumber]));
  resourceAction(event.submitter, async () => {
    await api("resources/budget", payload);
    resourceDirty = false;
    resourceFeedback.textContent = "Local limits saved.";
  });
});
document.querySelector("#resource-refresh").addEventListener("click", event => resourceAction(event.currentTarget, loadResources));
document.querySelector("#resource-release").addEventListener("click", event =>
  resourceAction(event.currentTarget, () => api("resources/release", {})));
document.querySelector("#resource-retry").addEventListener("click", event => {
  const [deployment, operation] = resourceTarget.value.split("/");
  resourceAction(event.currentTarget, () => api("resources/retry", { deployment, operation }));
});

loadResources().catch(() => {});
setInterval(() => { if (!document.hidden) loadResources().catch(() => {}); }, 5000);
