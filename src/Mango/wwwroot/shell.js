"use strict";

(() => {
  const pages = {
    overview: ["Overview", "Your computer, models, and network at a glance."],
    serving: ["Serving", "Configure a provider. Make its models ready."],
    "serving/aliases": ["Alias ratings", "Set clearly scoped ratings across matching deployments."],
    "serving/measurements": ["Measurements", "Observed benchmark results for the selected model."],
    activity: ["Activity", "Live requests and retained history."],
    "network/nodes": ["Network", "Your computers and their last observed state."],
    "network/queue": ["Queue", "How long a request may wait for capacity."],
    "network/keys": ["Access", "Client inference keys and this computer’s public endpoint."],
    "network/public": ["Public access", "Configure the inference-only upstream on this computer."],
    "network/recovery": ["Recovery", "Recover access or explicitly reset network authority."],
    "network/setup": ["Connect this computer", "Join a network or create your own."],
    "protection/activity": ["Protection", "Activity thresholds, memory reserves and benchmark policy on this computer."],
    "protection/memory": ["Memory reserves", "Leave room for your operating system and other work."],
    "protection/benchmarks": ["Benchmarks", "Choose when Mango may qualify local models."],
    "system/startup": ["Host settings", "Windows startup, connectivity and updates on this computer."],
    "system/ports": ["Node port", "Choose the port clients use to reach this computer."],
    "system/updates": ["Updates", "Release availability and local maintenance preferences."],
    "system/logging": ["Logging and storage", "Retention policy, disk usage and clearing records."],
    "system/data": ["Data", "Local storage and retained-record clearing."]
  };
  const tabs = {
    serving: [["serving", "Models and readiness"], ["serving/aliases", "Aliases"], ["serving/measurements", "Measurements"]],
    network: [["network/nodes", "Computers and queue"], ["network/keys", "Access"], ["network/recovery", "Recovery"], ["network/setup", "Setup"]],
    system: [["system/startup", "Host settings"], ["system/logging", "Logging and storage"]]
  };
  const aliases = { "private-network": "network/nodes", providers: "serving", models: "serving",
    "live-requests": "activity", "public-inference": "network/public", logging: "activity", settings: "protection/activity",
    "activity/live": "activity", "activity/history": "activity", network: "network/nodes", protection: "protection/activity", system: "system/startup" };

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
    const currentSection = document.querySelector('section[data-page="' + current.page + '"]');
    const groupPanel = currentSection.closest("[data-group-page]");
    const displayPage = groupPanel?.dataset.groupPage || current.page;
    document.title = "Mango · " + pages[displayPage][0];
    document.querySelector("#page-title").textContent = pages[displayPage][0];
    document.querySelector("#page-description").textContent = pages[displayPage][1];
    for (const group of document.querySelectorAll("[data-group-page]")) group.hidden = group !== groupPanel;
    document.querySelectorAll("section.page[data-page]").forEach(section => {
      const group = section.closest("[data-group-page]");
      section.id = "page-" + section.dataset.page.replaceAll("/", "-");
      if (group) group.id = "group-" + group.dataset.groupPage.replaceAll("/", "-");
      section.hidden = group ? group !== groupPanel : section !== currentSection;
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
      const grouped = document.querySelector('[data-group-page="' + button.dataset.page + '"]');
      if (grouped) button.setAttribute("aria-controls", grouped.id);
      if (button.dataset.page === "network/setup") button.hidden = !!globalThis.mangoEnrolled && current.page !== "network/setup";
      const selected = button.dataset.page === displayPage;
      button.setAttribute("aria-selected", String(selected));
      button.tabIndex = selected ? 0 : -1;
      const panel = document.getElementById(button.getAttribute("aria-controls"));
      panel.setAttribute("role", "tabpanel");
      panel.setAttribute("aria-labelledby", button.id);
    }
    if (!initial && previous.group !== current.group) document.querySelector("#page-title").focus({ preventScroll: true });
    if (previous.page !== current.page) main.scrollTop = scroll.get(current.page) || 0;
    if (groupPanel && current.page !== displayPage) currentSection.scrollIntoView({ block: "nearest" });
    document.dispatchEvent(new CustomEvent("mango:route", { detail: current }));
  }

  document.addEventListener("DOMContentLoaded", () => {
    renderRoute(true);
    for (const button of document.querySelectorAll("[data-reveal]")) button.addEventListener("click", () => {
      const panel = document.getElementById(button.dataset.reveal);
      panel.hidden = !panel.hidden; button.setAttribute("aria-expanded", String(!panel.hidden));
    });
    document.querySelector(".skip-link").addEventListener("click", event => {
      event.preventDefault(); document.querySelector("#main-content").focus();
    });
    document.querySelector("#page-tabs").addEventListener("keydown", event => {
      const buttons = [...event.currentTarget.children].filter(button => !button.hidden);
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
