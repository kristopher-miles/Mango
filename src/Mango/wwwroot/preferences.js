"use strict";

// A record keeps its draft while one acknowledged write runs; pages never own persistence.
(() => {
  function same(left, right) {
    if (Object.is(left, right)) return true;
    if (!left || !right || typeof left !== "object" || typeof right !== "object") return false;
    if (Array.isArray(left) !== Array.isArray(right)) return false;
    const keys = Object.keys(left);
    return keys.length === Object.keys(right).length && keys.every(key =>
      Object.hasOwn(right, key) && same(left[key], right[key]));
  }

  class PreferenceWriter {
    constructor({ value, write, read, validate = () => null, changed = () => {}, delay = 600, reviewKeys = [] }) {
      this.baseline = structuredClone(value);
      this.write = write;
      this.read = read;
      this.validate = validate;
      this.changed = changed;
      this.delay = delay;
      this.reviewKeys = reviewKeys;
      this.dirty = new Map();
      this.invalid = new Set();
      this.conflicts = new Set();
      this.versions = new Map();
      this.running = null;
      this.paused = null;
      this.state = { kind: "current", message: "Up to date" };
    }

    get value() { return { ...structuredClone(this.baseline), ...Object.fromEntries(this.dirty) }; }
    get pending() { return this.dirty.size > 0 || !!this.running; }

    report(kind, message, error) {
      this.state = { kind, message, error };
      this.changed(this);
    }

    change(name, value, { immediate = false, valid = true } = {}) {
      this.versions.set(name, (this.versions.get(name) || 0) + 1);
      this.dirty.set(name, structuredClone(value));
      if (valid) this.invalid.delete(name); else this.invalid.add(name);
      if (!this.running && valid && same(value, this.baseline[name])) this.dirty.delete(name);
      clearTimeout(this.timer);
      if (this.paused) return this.report("paused", this.paused);
      if (this.conflicts.size) return this.report("conflict", "Changed elsewhere · review current and entered values");
      this.report(this.dirty.size ? "pending" : "current", this.dirty.size ? "Waiting to update…" : "Up to date");
      if (immediate) return this.flush();
      this.timer = setTimeout(() => this.flush(), this.delay);
    }

    refresh(value) {
      if (this.pending) return;
      this.baseline = structuredClone(value);
      if (this.state.kind === "error") { this.changed(this); return; }
      this.report("current", "Up to date");
    }

    pause(reason = "Unlock administration to update") {
      this.paused = reason;
      clearTimeout(this.timer);
      if (this.pending) this.report("paused", reason);
    }

    resume() {
      this.paused = null;
      return this.flush();
    }

    resolve(useCurrent) {
      if (useCurrent) for (const key of this.conflicts) {
        this.dirty.delete(key);
        this.invalid.delete(key);
      }
      this.conflicts.clear();
      return this.flush();
    }

    flush() {
      clearTimeout(this.timer);
      if (this.running) return this.running;
      if (this.paused || this.conflicts.size) return Promise.resolve(false);
      const changes = Object.fromEntries([...this.dirty].filter(([key]) => !this.invalid.has(key)));
      if (!Object.keys(changes).length) {
        this.report(this.invalid.size ? "incomplete" : "current",
          this.invalid.size ? "Complete the highlighted fields" : "Up to date");
        return Promise.resolve(!this.invalid.size);
      }
      const baseline = structuredClone(this.baseline);
      const problem = this.validate({ ...baseline, ...changes }, changes);
      if (problem) {
        this.report("incomplete", problem);
        return Promise.resolve(false);
      }
      const versions = new Map(this.versions);
      const expected = Object.fromEntries(Object.keys(changes).map(key => [key, baseline[key]]));
      this.report("updating", "Updating…");
      this.running = (async () => {
        let again = false;
        try {
          const accepted = await Promise.resolve().then(() => this.write({ changes, expected, baseline }));
          this.baseline = structuredClone(accepted);
          for (const key of Object.keys(changes)) {
            if (this.versions.get(key) === versions.get(key)) this.dirty.delete(key);
          }
          for (const [key, value] of this.dirty) {
            if (!this.invalid.has(key) && same(value, this.baseline[key])) this.dirty.delete(key);
          }
          again = [...this.dirty.keys()].some(key => !this.invalid.has(key));
          this.report(this.invalid.size ? "incomplete" : again ? "pending" : "current",
            this.invalid.size ? "Complete the highlighted fields" : again ? "Waiting to update…" : "Up to date");
          return true;
        } catch (error) {
          if (error.status === 409 && ["conflict", "configuration_changed"].includes(error.code) && this.read) {
            try {
              const current = await this.read();
              const needsReview = error.code === "configuration_changed" || this.reviewKeys.some(key => !same(current[key], baseline[key]));
              for (const key of Object.keys(changes)) {
                if (needsReview || !same(current[key], baseline[key]) && !same(current[key], changes[key])) this.conflicts.add(key);
              }
              this.baseline = structuredClone(current);
              if (this.conflicts.size) this.report("conflict", needsReview ? "Deployment or hardware changed · review these limits before requalifying" : "Changed elsewhere · review current and entered values", error);
              else if (!same(current, baseline)) { again = true; this.report("pending", "Refreshing changed settings…"); }
              else this.report("error", `Not updated · ${error.message}`, error);
            } catch (readError) { this.report("error", `Not updated · ${readError.message}`, readError); }
          } else if (error.code === "admin_required") this.pause();
          else this.report("error", `Not updated · ${error.message}`, error);
          return false;
        } finally {
          this.running = null;
          this.changed(this);
          if (again && !this.paused && !this.conflicts.size) this.timer = setTimeout(() => this.flush(), 0);
        }
      })();
      return this.running;
    }
  }

  const api = { PreferenceWriter, same };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else globalThis.MangoPreferences = api;
})();
