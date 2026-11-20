"use strict";

function providerIdentifiers(item) { return item.local?.providerIdentifiers ?? item.observed?.providerIdentifiers ?? []; }
const capabilities = {
  chat: ["chat", "Chat"], embeddings: ["embeddings", "Embeddings"], images: ["image", "Image generation"],
  transcription: ["mic", "Transcription"], speech: ["speaker", "Speech"], systemOne: ["bolt", "System One"],
  imageInput: ["eye", "Image input"], audioInput: ["audio", "Audio input"], tools: ["tool", "Function tools"],
  structuredOutput: ["braces", "Structured output"]
};
const residencyNames = { loaded: "Loaded", unloaded: "Unloaded", busy: "Busy", unknown: "Unobserved" };
function modelCapabilities(facts) {
  const keys = new Set(facts?.operations || []);
  if (facts?.inputs?.includes("image")) keys.add("imageInput");
  if (facts?.inputs?.includes("audio")) keys.add("audioInput");
  if (facts?.tools || facts?.clientTools) keys.add("tools");
  if (facts?.structuredOutput) keys.add("structuredOutput");
  return [...keys].filter(key => Object.hasOwn(capabilities, key));
}
function residency(item) {
  if (item.local) return item.local.busy || item.local.instance?.busy ? "busy" : item.local.instance ? "loaded" : "unloaded";
  return item.observed?.busy ? "busy" : item.observed?.loaded == null ? "unknown" : item.observed.loaded ? "loaded" : "unloaded";
}
function inventoryName(item) {
  return item.local?.model.displayName || item.observed?.displayName || providerIdentifiers(item)[0] || item.desired?.alias || item.local?.alias || item.id;
}
function matchingModels(items, query, mode, filters) {
  const matching = [], selected = [...filters];
  for (const item of items) {
    const keys = modelCapabilities(item.local?.model || item.observed), state = residency(item);
    if (query && ![inventoryName(item), item.desired?.alias, item.local?.alias, ...providerIdentifiers(item), item.id].filter(Boolean).join(" ").toLowerCase().includes(query)) continue;
    if (mode && mode !== state && !(mode === "loaded" && state === "busy")) continue;
    if (selected.every(key => keys.includes(key))) matching.push({ item, keys, state });
  }
  return matching;
}

if (typeof module !== "undefined" && module.exports) module.exports = { matchingModels, inventoryName };
