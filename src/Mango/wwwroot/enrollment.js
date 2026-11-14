"use strict";

// Enrollment and sign-in are one user operation, with no replay after uncertain setup.
async function connectNetwork(kind, fields, request) {
  let setup, interrupted = false;
  try {
    try { setup = await request(`network/${kind}`, fields); }
    catch (error) {
      if (error.status && error.status < 500) throw error;
      const current = await request("network").catch(() => null);
      if (!current?.localNode || current.reenroll) throw error;
      setup = { node: current.localNode.id, identity: current.identity };
      interrupted = true;
    }
    try {
      return { setup, interrupted, session: await request("network/unlock", { password: fields.password }) };
    } catch (unlockError) { return { setup, interrupted, unlockError }; }
  } finally { delete fields.password; }
}
if (typeof module !== "undefined" && module.exports) module.exports = { connectNetwork };
