// SPDX-License-Identifier: GPL-3.0-or-later
// Fails the build on any npm advisory that is not listed below with a reason. A listed advisory must have NO patched version yet
// and must not reach the visitor's browser; each entry carries a date by which someone has to look again (the check fails after it).
// Run: node scripts/audit-check.mjs   (CI runs it after `npm install`)
import { execSync } from "node:child_process";

/** Advisories accepted for now. Remove an entry as soon as a fixed version exists (Dependabot then proposes it). */
export const ACCEPTED = {
  // picomatch <= 4.0.7: ReDoS when an ATTACKER supplies the glob (e.g. "+(*a)"). No fixed version exists anywhere (advisory:
  // first_patched_version = null). Only build/dev tools use it (eslint, vitest/vite, next-intl's file watcher) with our own fixed
  // patterns; nothing of it is in the browser bundle. `npm audit fix --force` would only downgrade vitest to 3.1.4 (breaking,
  // and next-intl/eslint would still pull picomatch in).
  "GHSA-fc5q-39xc-vjpf": { reason: "no patched picomatch exists; build-time only, our own patterns", reviewBy: "2027-01-31" },
};

function audit() {
  try {
    return execSync("npm audit --json", { encoding: "utf8", maxBuffer: 64 * 1024 * 1024, stdio: ["ignore", "pipe", "ignore"] });
  } catch (e) {
    if (e.stdout) return e.stdout; // npm audit exits 1 when it finds anything
    throw e;
  }
}

export function problems(report, accepted = ACCEPTED, today = new Date()) {
  const found = new Map();
  for (const v of Object.values(report.vulnerabilities ?? {}))
    for (const via of v.via) if (typeof via === "object") found.set(via.url.split("/").pop(), `${via.name}: ${via.title} (${via.severity})`);
  const out = [];
  for (const [id, text] of found) {
    const a = accepted[id];
    if (!a) out.push(`not accepted: ${id} ${text}`);
    else if (new Date(a.reviewBy) < today) out.push(`review date passed for ${id} (${a.reviewBy}): check whether a fix exists, then update scripts/audit-check.mjs`);
  }
  return out;
}

if (process.argv[1]?.endsWith("audit-check.mjs")) {
  const list = problems(JSON.parse(audit()));
  if (list.length) {
    console.error("audit-check: FAILED\n  " + list.join("\n  "));
    process.exit(1);
  }
  console.log(`audit-check: OK (accepted with a reason: ${Object.keys(ACCEPTED).join(", ") || "none"})`);
}
