"use strict";

const resourceForm = document.querySelector("#resource-form"), resourceTarget = resourceForm.elements.target;
const resourceFeedback = document.querySelector("#resource-feedback"), resourceGpus = document.querySelector("#resource-gpus");
const mib = 1024 ** 2, resourceKey = model => `${model.id}/${model.operation}`;
const resourceHistory = new ResourceHistory();
const hiddenMetrics = new Set();
let resourceView, resourceLoading = false, budgetWriter, qualificationKey;
function bytes(value) { return value == null ? "unavailable" : `${(value / mib).toFixed(1)} MiB`; }
function percent(value) { return value == null ? "unavailable" : `${value.toFixed(0)}%`; }
function renderResourceProfile(model) {
  const profile = model.profile;
  document.querySelector("#resource-profile").textContent = [model.unsafe ? "An allocation failed. Correct the limits or explicitly retry before using this placement." : "",
    profile?.complete ? "Benchmark complete." : profile?.speed?.elapsed ? "Warm benchmark recorded." :
      profile?.measurements.length ? "Benchmark measurements are incomplete." : "No benchmark measurements.",
    profile?.error,
    model.canColdLoad ? "" : "Cold-load measurement is unavailable for this provider."].filter(Boolean).join(" ");
  document.querySelector("#resource-retry").disabled = !model.budget || resourceView.forced;
  document.querySelector("#measurement-title").textContent = `${model.identifier} · ${operationNames[model.operation]}`;
  const speed = profile?.speed;
  const speedRow = speed ? [element("li", ["Warm sample", speed.error,
    speed.elapsed == null ? "" : `Request ${speed.elapsed}`,
    speed.firstToken == null ? "" : `First token ${speed.firstToken}`,
    speed.promptTokens == null ? "" : `${speed.promptTokens} prompt tokens`,
    speed.outputTokens == null ? "" : `${speed.outputTokens} generated tokens`,
    speed.tokensPerSecond == null ? "" : `${speed.tokensPerSecond.toFixed(1)} tokens/s (${speed.providerGenerationRate === true
      ? "provider reported" : speed.providerGenerationRate === false ? "stream estimate" : "source unrecorded"})`,
    !speed.error && model.operation === "chat" && speed.tokensPerSecond == null ? "Generation speed unavailable" : ""
  ].filter(Boolean).join(" · "))] : [];
  document.querySelector("#resource-passes").replaceChildren(...speedRow, ...(profile?.measurements || []).map(pass => element("li", [pass.pass, pass.elapsed,
    pass.workload.width ? `${pass.workload.width} × ${pass.workload.height}` : "",
    pass.promptTokens == null ? "" : `${pass.promptTokens} prompt tokens`,
    pass.promptMilliseconds == null ? "" : `Prompt processing ${pass.promptMilliseconds.toFixed(1)} ms`,
    pass.firstToken == null ? "" : `First token ${pass.firstToken}`,
    pass.tokensPerSecond == null ? "" : `${pass.tokensPerSecond.toFixed(1)} tokens/s`,
    pass.observedIncrement ? `Additional RAM ${bytes(pass.observedIncrement.ramBytes)}` : "RAM peak unavailable",
    pass.reusedCache == null ? "" : pass.reusedCache ? "Cache reused" : "Cache unused"].filter(Boolean).join(" · "))));
}
function budgetRecord(model, devices) {
  const budget = model.budget;
  const record = { units: budget?.workload.units ?? model.context ?? null, items: budget?.workload.items ?? 1, mediaItems: budget?.workload.mediaItems ?? 0,
    loadRamMiB: budget ? Math.ceil(budget.load.ramBytes / mib) : null, executeRamMiB: budget ? Math.ceil(budget.execute.ramBytes / mib) : null,
    allowAttached: budget?.allowAttached ?? false, allowOverlap: budget?.allowOverlap ?? false, parallelSlots: budget?.parallelSlots ?? 1, expectedRevision: model.budgetRevision, configuration: model.qualificationConfiguration };
  for (const device of devices.filter(device => !device.integrated && device.totalBytes != null))
    for (const stage of ["load", "execute"]) record[`${stage}:${device.id}`] = budget ? Math.ceil((budget[stage].deviceBytes[device.id] || 0) / mib) : null;
  return record;
}
function renderQualification() {
  const local = localOwner(servingNode), models = (resourceView?.models || []).filter(item => item.id === servingModel);
  resourceForm.hidden = !local || models.length === 0;
  document.querySelector("#qualification-empty").hidden = local && models.length > 0;
  document.querySelector("#qualification-empty").textContent = !local ? "Local limits and lifecycle permission belong to the owning computer. Open Mango there to qualify this model." : "Models appear after the provider reports its inventory.";
  if (!local || !models.length) {
    budgetWriter?.bindings.splice(0); qualificationKey = null;
    document.querySelector("#qualification-title").textContent = "Model qualification";
    document.querySelector("#qualification-scope").textContent = "Select a model to inspect its local limits.";
    resourceFeedback.replaceChildren(); delete resourceFeedback.dataset.feedback;
    return;
  }
  let selected = models.find(model => resourceKey(model) === resourceTarget.value);
  if (!selected) selected = models.find(model => model.operation === route.operation) || models[0];
  selectOptions(resourceTarget, models.map(model => [resourceKey(model), operationNames[model.operation]]), resourceKey(selected));
  const key = resourceKey(selected), devices = resourceView.observation?.devices || [];
  document.querySelector("#qualification-title").textContent = selected.identifier;
  document.querySelector("#qualification-scope").textContent = selected.budget?.estimated ? "Estimated limits · This computer only" : "Local qualification · This computer only";
  document.querySelector("#resource-units").textContent = ["chat", "embeddings", "systemOne"].includes(selected.operation) ? "Units are tokens per sequence; items are sequences per request."
    : selected.operation === "images" ? "Units are pixels per image; items are images per request." : selected.operation === "speech" ? "Units are input characters." : "Units are input audio bytes.";
  document.querySelector("#resource-attached").hidden = !selected.attached;
  const value = budgetRecord(selected, devices);
  const shape = `${key}:${devices.filter(d => !d.integrated).map(d => d.id).join(",")}`;
  if (qualificationKey !== shape) {
    budgetWriter?.bindings.splice(0); qualificationKey = shape;
    resourceGpus.replaceChildren(...devices.filter(d => !d.integrated && d.totalBytes != null).flatMap(device => ["load", "execute"].map(stage => {
      const label = labeledInput(`${device.name} · ${stage} VRAM (MiB)`, `${stage}:${device.id}`, "number", value[`${stage}:${device.id}`]);
      Object.assign(label.lastElementChild, { min: 0, max: 1048576, required: true }); return label;
    })));
    delete resourceFeedback.dataset.feedback;
  }
  budgetWriter = recordWriter(`budget:${key}`, { value, reviewKeys: ["configuration"], label: `${selected.identifier} · ${operationNames[selected.operation]} limits`, page: `serving/${servingNode}/${servingProvider}?model=${selected.id}&operation=${selected.operation}`,
    validate: values => Object.entries(values).some(([name, value]) => (name.includes(":") || ["units", "items", "mediaItems", "loadRamMiB", "executeRamMiB", "parallelSlots"].includes(name)) && value == null)
      ? "Complete all workload and memory limits before qualification takes effect." : null,
    read: async () => {
      const current = await api("resources"), model = current.models.find(item => resourceKey(item) === key);
      if (!model) throw new Error("This deployment is no longer available."); return budgetRecord(model, current.observation?.devices || []);
    },
    write: async ({ changes, baseline }) => {
      const next = { ...baseline, ...changes };
      const payload = { deployment: selected.id, operation: selected.operation, expectedRevision: baseline.expectedRevision,
        allowAttached: next.allowAttached, allowOverlap: next.allowOverlap, parallelSlots: next.allowOverlap ? next.parallelSlots : 1 };
      for (const name of ["units", "items", "mediaItems", "loadRamMiB", "executeRamMiB"]) payload[name] = next[name];
      for (const stage of ["load", "execute"]) payload[`${stage}VramMiB`] = Object.fromEntries(devices.filter(device => !device.integrated && device.totalBytes != null).map(device => [device.id, next[`${stage}:${device.id}`]]));
      await api("resources/budget", payload);
      const current = await api("resources"); resourceView = current;
      const model = current.models.find(item => resourceKey(item) === key); return budgetRecord(model, current.observation?.devices || []);
    }, changed: writer => { resourceForm.elements.parallelSlots.disabled = !writer.value.allowOverlap; }
  });
  bindPreferences(budgetWriter, resourceForm, resourceFeedback, { exclude: ["target"] });
  if (!selected.budget && !budgetWriter.pending && budgetWriter.state.kind === "current") {
    resourceFeedback.textContent = "Waiting for Windows device observations before estimating limits.";
    delete resourceFeedback.dataset.feedback;
  }
  resourceForm.elements.parallelSlots.disabled = !budgetWriter.value.allowOverlap;
  renderResourceProfile(selected);
}
function drawResources() {
  const reading = resourceView?.observation, last = resourceHistory.samples.at(-1);
  if (!reading || !last) { document.querySelector("#chart-freshness").textContent = resourceHistory.error || "Waiting for Windows observations"; return; }
  const devices = reading.devices.filter(device => !device.integrated && device.totalBytes != null), deviceSelect = document.querySelector("#chart-device");
  const device = devices.some(item => item.id === deviceSelect.value) ? deviceSelect.value : devices[0]?.id;
  selectOptions(deviceSelect, devices.map(item => [item.id, item.name]), device);
  document.querySelector("#chart-device-label").hidden = devices.length < 2;
  const sampleSeconds = view?.settings.sampleSeconds || 2;
  const { stale } = resourceHistory.status(Date.now(), sampleSeconds);
  document.querySelector("#chart-freshness").textContent = resourceHistory.error ? `Unavailable · ${resourceHistory.error}` : stale ? "Observations stale" : "Live observations";
  const colors = { cpu: "#23624c", gpu: "#9c5c0b", ram: "#386da0", vram: "#7853a4" }, names = { cpu: "Total CPU", gpu: "GPU · busiest engine", ram: "RAM used", vram: "Dedicated VRAM" };
  const patterns = { cpu: "", gpu: "7 4", ram: "2 3", vram: "10 3 2 3" };
  const series = Object.fromEntries(Object.keys(colors).map(key => [key, resourceHistory.series(key, device, Math.max(15000, (view?.settings.sampleSeconds || 2) * 2000))]));
  const metrics = document.querySelector("#resource-metrics");
  if (!metrics.childElementCount) for (const key of Object.keys(colors)) {
    const metric = element("button", null, "metric"); metric.type = "button"; metric.dataset.metric = key;
    metric.style.setProperty("--metric-color", colors[key]);
    metric.append(element("span", names[key]), element("strong"), element("span"));
    metric.addEventListener("click", () => { if (hiddenMetrics.has(key)) hiddenMetrics.delete(key); else hiddenMetrics.add(key); drawResources(); });
    metrics.append(metric);
  }
  for (const metric of metrics.children) {
    const key = metric.dataset.metric;
    const value = series[key].at(-1)?.value;
    metric.setAttribute("aria-pressed", String(!hiddenMetrics.has(key)));
    metric.setAttribute("aria-label", `${names[key]}: ${value == null ? "unavailable" : `${value.toFixed(0)} percent`}. ${hiddenMetrics.has(key) ? "Show" : "Hide"} in plot`);
    metric.children[1].textContent = value == null ? "—" : `${value.toFixed(0)}%`;
    const gpu = devices.find(item => item.id === device), gib = bytes => (bytes / 1024 ** 3).toFixed(1);
    metric.children[2].textContent = value == null ? key === "vram" && !devices.length ? "No dedicated GPU" : "Unavailable"
      : key === "ram" ? `${gib(reading.totalRam - reading.freeRam)} / ${gib(reading.totalRam)} GiB`
        : key === "vram" ? `${gib(gpu.usedBytes)} / ${gib(gpu.totalBytes)} GiB`
          : stale || resourceHistory.error ? "Last observation" : "Current observation";
  }
  const svg = document.querySelector("#resource-chart"), ns = "http://www.w3.org/2000/svg";
  svg.querySelectorAll("g").forEach(group => group.remove());
  const group = document.createElementNS(ns, "g");
  for (const value of [0, 25, 50, 75, 100]) {
    const y = 195 - value * 1.7, line = document.createElementNS(ns, "line"), label = document.createElementNS(ns, "text");
    for (const [key, val] of Object.entries({ x1: 38, x2: 790, y1: y, y2: y, stroke: "#dde3dc" })) line.setAttribute(key, val);
    label.setAttribute("x", "0"); label.setAttribute("y", y + 4); label.setAttribute("fill", "#5a6862"); label.setAttribute("font-size", "11"); label.textContent = `${value}%`; group.append(line, label);
  }
  const from = last.at - 600000;
  for (const key of Object.keys(series)) {
    if (hiddenMetrics.has(key)) continue;
    let d = "", open = false;
    for (const point of series[key]) {
      if (point.value == null) { open = false; continue; }
      const x = 38 + (point.at - from) / 600000 * 752, y = 195 - point.value * 1.7;
      d += `${!open || point.gap ? "M" : "L"}${x.toFixed(1)},${y.toFixed(1)} `; open = true;
    }
    const path = document.createElementNS(ns, "path"); path.setAttribute("d", d); path.setAttribute("fill", "none"); path.setAttribute("stroke", colors[key]); path.setAttribute("stroke-width", "2.5"); path.setAttribute("stroke-dasharray", patterns[key]); group.append(path);
    const latest = series[key].at(-1);
    if (latest?.value != null) {
      const dot = document.createElementNS(ns, "circle"); dot.setAttribute("cx", "790"); dot.setAttribute("cy", String(195 - latest.value * 1.7)); dot.setAttribute("r", "3"); dot.setAttribute("fill", colors[key]); group.append(dot);
    }
  }
  for (const [x, at, anchor] of [[38, from, "start"], [414, from + 300000, "middle"], [790, last.at, "end"]]) {
    const label = document.createElementNS(ns, "text"); label.setAttribute("x", x); label.setAttribute("y", "217");
    label.setAttribute("text-anchor", anchor); label.setAttribute("fill", "#5a6862"); label.setAttribute("font-size", "11");
    label.textContent = new Date(at).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" }); group.append(label);
  }
  svg.append(group);
  const extrema = Object.keys(series).map(key => {
    const values = series[key].map(point => point.value).filter(value => value != null);
    return values.length ? `${names[key]} ${Math.min(...values).toFixed(0)}–${Math.max(...values).toFixed(0)}%` : null;
  }).filter(Boolean).join(" · ");
  const summary = `${resourceHistory.samples.length} observations · Protection samples every ${sampleSeconds}s · Gaps indicate missing observations.`;
  document.querySelector("#chart-summary").textContent = summary;
  document.querySelector("#chart-description").textContent = `${Object.keys(series).map(key => `${names[key]} ${percent(series[key].at(-1)?.value)}`).join(". ")}. Observed range: ${extrema}. ${summary}`;
  document.querySelector("#chart-limitation").textContent = [reading.limitation, reading.devices.some(device => device.integrated) ? "Integrated GPUs use shared RAM; they are not dedicated VRAM." : ""].filter(Boolean).join(" ");
}
async function loadResources() {
  if (resourceLoading || !token) return;
  resourceLoading = true;
  try {
    resourceView = await api("resources");
    const state = resourceView.status, reading = resourceView.observation;
    resourceHistory.add(reading);
    document.querySelector("#resource-state").textContent = `${stateNames[state.state]}${state.reason ? ` · ${state.reason}` : ""}${state.quietUntil ? ` · Until ${new Date(state.quietUntil).toLocaleTimeString()}` : ""}`;
    document.querySelector("#resource-release").hidden = state.state !== "fault";
    document.querySelector("#resource-observation").textContent = reading ? [`Free RAM ${bytes(reading.freeRam)}`, `External CPU ${percent(reading.externalCpu)}`, `External GPU ${percent(reading.externalGpu)}`,
      ...reading.devices.map(device => device.integrated ? `${device.name} uses shared RAM` : `${device.name}: ${bytes(device.usedBytes)} of ${bytes(device.totalBytes)}`), reading.limitation, resourceView.recoveryWarning].filter(Boolean).join(" · ") : "Waiting for Windows observations.";
    document.querySelector("#resource-benchmark").textContent = resourceView.benchmark ? `${resourceView.forced ? "Forced benchmark" : "Benchmark"} · ${resourceView.benchmark.pass}` :
      resourceView.forcedBenchmarkError || (view?.settings.benchmarkPolicy === "now" && !resourceView.benchmarkReady ? "Complete local benchmarks are required before serving." : "No benchmark is running.");
    document.querySelector("#resource-benchmark-missing").disabled = !resourceView.missingBenchmarks || resourceView.forced;
    drawResources(); renderQualification();
    if (catalogView) renderServingModels();
    const attention = document.querySelector("#overview-attention"), items = [];
    if (!networkView?.localNode) { const li = element("li"); const a = element("a", "Connect this computer to a network"); a.href = "#/network/setup"; li.append(a); items.push(li); }
    if (!catalogView?.endpoints.length) { const li = element("li"); const a = element("a", "Connect a provider in Serving"); a.href = "#/serving"; li.append(a); items.push(li); }
    if (!["contributor", "manualLeech"].includes(state.state) && state.reason) items.push(element("li", state.reason));
    const notices = items.length ? items : [element("li", "No current notices.", "muted")];
    if (attention.innerHTML !== notices.map(item => item.outerHTML).join("")) attention.replaceChildren(...notices);
  } catch (error) { resourceHistory.fail(error.message); drawResources(); throw error; }
  finally { resourceLoading = false; }
}
resourceTarget.addEventListener("change", renderQualification);
document.querySelector("#chart-device").addEventListener("change", drawResources);
document.addEventListener("visibilitychange", () => { if (document.hidden) resourceHistory.pause(); });
document.addEventListener("mango:route", () => { if (!["overview", "serving", "protection"].includes(route.group)) resourceHistory.pause(); });
document.querySelector("#resource-benchmark-missing").addEventListener("click", event =>
  perform(event.currentTarget, document.querySelector("#resource-missing-feedback"), async () => {
    const result = await api("resources/benchmark-missing", {});
    await loadResources();
    return result.queued ? `Queued ${result.queued} model benchmarks.` : "All eligible models have a valid sample.";
  }));
for (const [id, feedback, work] of [["resource-refresh", "protection-feedback", loadResources], ["resource-release", "protection-feedback", () => api("resources/release", {})],
  ["resource-retry", "resource-feedback", () => { const [deployment, operation] = resourceTarget.value.split("/"); return api("resources/retry", { deployment, operation, sampleOnly: true }); }]])
  document.querySelector(`#${id}`).addEventListener("click", event => perform(event.currentTarget, document.querySelector(`#${feedback}`), async () => { await work(); await loadResources(); return "Updated local observations."; }));
