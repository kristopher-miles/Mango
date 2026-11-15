"use strict";

(() => {
  const pages = {
    overview: ["Overview", "Your computer, models, and network at a glance."],
    serving: ["Serving", "Configure a provider. Make its models ready."],
    "serving/aliases": ["Alias ratings", "Set clearly scoped ratings across matching deployments."],
    "serving/measurements": ["Measurements", "Observed benchmark results for the selected model."],
    "activity/live": ["Activity", "See where work is running and what is waiting."],
    "activity/history": ["History", "Explore retained work and network coverage."],
    "network/nodes": ["Network", "Your computers and their last observed state."],
    "network/queue": ["Queue", "How long a request may wait for capacity."],
    "network/keys": ["Inference keys", "Give clients scoped access to inference."],
    "network/public": ["Public access", "Configure the inference-only upstream on this computer."],
    "network/recovery": ["Recovery", "Recover access or explicitly reset network authority."],
    "network/setup": ["Connect this computer", "Join a network or create your own."],
    "protection/activity": ["Activity protection", "Keep this computer responsive to you."],
    "protection/memory": ["Memory reserves", "Leave room for your operating system and other work."],
    "protection/benchmarks": ["Benchmarks", "Choose when Mango may qualify local models."],
    "system/startup": ["Windows startup", "Choose how Mango starts on this computer."],
    "system/ports": ["Node port", "Choose the port clients use to reach this computer."],
    "system/updates": ["Updates", "Release availability and local maintenance preferences."],
    "system/logging": ["Logging policy", "Choose what is retained, for how long, and where day boundaries fall."],
    "system/data": ["Data", "Local storage and retained-record clearing."]
  };
  const tabs = {
    serving: [["serving", "Models and readiness"], ["serving/aliases", "Aliases"], ["serving/measurements", "Measurements"]],
    activity: [["activity/live", "Live"], ["activity/history", "History"]],
    network: ["nodes", "queue", "keys", "public", "recovery", "setup"].map(x => [`network/${x}`, { nodes: "Computers", queue: "Queue", keys: "Keys", public: "Public access", recovery: "Recovery", setup: "Setup" }[x]]),
    protection: [["protection/activity", "Activity"], ["protection/memory", "Memory"], ["protection/benchmarks", "Benchmarks"]],
    system: [["system/startup", "Startup"], ["system/ports", "Node port"], ["system/updates", "Updates"], ["system/logging", "Logging policy"], ["system/data", "Data"]]
  };
  const aliases = { "private-network": "network/nodes", providers: "serving", models: "serving",
    "live-requests": "activity/live", "public-inference": "network/public", logging: "activity/history", settings: "protection/activity",
    activity: "activity/live", network: "network/nodes", protection: "protection/activity", system: "system/startup" };

  function parseRoute(hash) {
    const [raw, query = ""] = hash.replace(/^#\/?/, "").split("?");
    const path = Object.hasOwn(aliases, raw) ? aliases[raw] : raw;
    const parts = path.split("/");
    const page = Object.hasOwn(pages, path) ? path : parts[0] === "serving" ? "serving" : "overview";
    const values = new URLSearchParams(query);
    let node = null, provider = null;
    if (page === "serving" && parts.length > 1) {
      try { node = decodeURIComponent(parts[1]); provider = parts[2] ? decodeURIComponent(parts[2]) : null; } catch { }
    }
    return { page, group: page.split("/")[0], node, provider, model: values.get("model"), operation: values.get("operation") };
  }

  if (typeof module !== "undefined" && module.exports) { module.exports = { parseRoute, pages }; return; }
  globalThis.route = parseRoute(location.hash);
  globalThis.navigate = path => { location.hash = `#/${path}`; };
  const scroll = new Map();

  function renderRoute(initial = false) {
    const previous = globalThis.route;
    const current = parseRoute(location.hash);
    const main = document.querySelector("#main-content");
    scroll.set(previous.page, main.scrollTop);
    globalThis.route = current;
    document.title = `Mango · ${pages[current.page][0]}`;
    document.querySelector("#page-title").textContent = pages[current.page][0];
    document.querySelector("#page-description").textContent = pages[current.page][1];
    document.querySelectorAll("section.page[data-page]").forEach(section => {
      section.id = `page-${section.dataset.page.replaceAll("/", "-")}`;
      section.hidden = section.dataset.page !== current.page;
    });
    document.querySelectorAll("#primary-nav a").forEach(link => {
      if (parseRoute(link.hash).group === current.group) link.setAttribute("aria-current", "page");
      else link.removeAttribute("aria-current");
    });
    const tabList = document.querySelector("#page-tabs");
    tabList.hidden = !tabs[current.group];
    tabList.setAttribute("role", "tablist");
    if (initial || previous.group !== current.group) {
      tabList.replaceChildren(...(tabs[current.group] || []).map(([path, label]) => {
        const button = document.createElement("button");
        button.type = "button";
        button.textContent = label;
        button.dataset.page = path;
        button.id = `tab-${path.replaceAll("/", "-")}`;
        button.setAttribute("role", "tab");
        button.setAttribute("aria-controls", `page-${path.replaceAll("/", "-")}`);
        button.addEventListener("click", () => navigate(path));
        return button;
      }));
    }
    for (const button of tabList.children) {
      const selected = button.dataset.page === current.page;
      button.setAttribute("aria-selected", String(selected));
      button.tabIndex = selected ? 0 : -1;
      const panel = document.getElementById(button.getAttribute("aria-controls"));
      panel.setAttribute("role", "tabpanel");
      panel.setAttribute("aria-labelledby", button.id);
    }
    if (!initial && previous.group !== current.group) document.querySelector("#page-title").focus({ preventScroll: true });
    if (previous.page !== current.page) main.scrollTop = scroll.get(current.page) || 0;
    document.dispatchEvent(new CustomEvent("mango:route", { detail: current }));
  }

  document.addEventListener("DOMContentLoaded", () => {
    renderRoute(true);
    document.querySelector(".skip-link").addEventListener("click", event => {
      event.preventDefault(); document.querySelector("#main-content").focus();
    });
    document.querySelector("#page-tabs").addEventListener("keydown", event => {
      const buttons = [...event.currentTarget.children];
      const current = buttons.indexOf(document.activeElement);
      if (current < 0) return;
      let next;
      if (event.key === "ArrowRight") next = (current + 1) % buttons.length;
      if (event.key === "ArrowLeft") next = (current + buttons.length - 1) % buttons.length;
      if (event.key === "Home") next = 0;
      if (event.key === "End") next = buttons.length - 1;
      if (next === undefined) return;
      event.preventDefault();
      buttons.forEach((button, index) => button.tabIndex = index === next ? 0 : -1);
      buttons[next].focus();
    });
    document.querySelectorAll("[data-close-dialog]").forEach(button => button.addEventListener("click", () => button.closest("dialog").close()));
    document.querySelectorAll("dialog").forEach(dialog => dialog.addEventListener("close", () => {
      if (document.activeElement === document.body || document.activeElement?.closest("[hidden]")) document.querySelector("#page-title").focus({ preventScroll: true });
    }));
  });
  addEventListener("hashchange", () => renderRoute());
})();
