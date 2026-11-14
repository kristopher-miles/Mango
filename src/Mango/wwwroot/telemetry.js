"use strict";
(() => {
  class ResourceHistory {
    constructor() { this.samples = []; this.gap = false; this.error = null; }
    pause() { this.gap = true; }
    fail(message) { this.error = message; this.gap = true; }
    status(now, sampleSeconds) {
      const at = this.samples.at(-1)?.at;
      return { at, stale: at == null || now - at > Math.max(15000, 2 * sampleSeconds * 1000), error: this.error };
    }
    add(reading) {
      if (!reading) return false;
      const at = Date.parse(reading.at), previous = this.samples.at(-1);
      if (!Number.isFinite(at)) return false;
      this.error = null;
      if (previous && at <= previous.at) return false;
      this.samples.push({ at, reading, gap: this.gap }); this.gap = false; this.error = null;
      this.samples = this.samples.filter(sample => sample.at >= at - 600000).slice(-120);
      return true;
    }
    series(metric, device, maxGap = 15000) {
      return this.samples.map((sample, index) => {
        const r = sample.reading, gpu = r.devices.find(item => item.id === device && !item.integrated);
        const value = metric === "cpu" ? r.totalCpu : metric === "gpu" ? r.totalGpu : metric === "ram" ? r.totalRam > 0 ? (r.totalRam - r.freeRam) / r.totalRam * 100 : null
          : gpu?.totalBytes > 0 && gpu.usedBytes != null ? gpu.usedBytes / gpu.totalBytes * 100 : null;
        return { at: sample.at, value: Number.isFinite(value) ? Math.max(0, Math.min(100, value)) : null,
          gap: sample.gap || index > 0 && sample.at - this.samples[index - 1].at > maxGap };
      });
    }
  }
  if (typeof module !== "undefined" && module.exports) module.exports = { ResourceHistory };
  else globalThis.ResourceHistory = ResourceHistory;
})();
