// Hash-based CSP for the static pages, in two build passes (see "build" in package.json).
// Next.js puts small inline <script> blocks into every static page (its bootstrap and the RSC payload), so a CSP without relying on
// 'unsafe-inline' needs their SHA-256 hashes. They are only known once the HTML exists, and Vercel takes response headers from
// next.config.ts as evaluated DURING `next build` (it ignores later edits to .next/routes-manifest.json). So:
//   pass 1  next build                      -> HTML
//           node scripts/csp-hashes.mjs     -> writes csp-hashes.json { "/ar": ["'sha256-…'", …], … }
//   pass 2  next build                      -> next.config.ts reads csp-hashes.json into each page's CSP header
//           node scripts/csp-hashes.mjs --verify -> recomputes from the final HTML; FAILS the build on any difference,
//                                              so a page can never ship with a policy that blocks its own scripts.
// The build id is fixed (next.config.ts generateBuildId), which keeps the inline scripts identical between the two passes.
// With hashes present browsers ignore 'unsafe-inline' for scripts; it stays only as a fallback for very old browsers.
import { createHash } from "node:crypto";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

export const HASH_FILE = join(process.cwd(), "csp-hashes.json");
const APP_DIR = join(process.cwd(), ".next", "server", "app");
const ROUTES = ["ar", "en"].flatMap((l) => [`/${l}`, `/${l}/privacy`, `/${l}/terms`, `/${l}/app`]);

/** Hashes of the executable inline scripts of one prerendered page (JSON-LD data blocks are not scripts to CSP). */
export function inlineScriptHashes(html) {
  const hashes = new Set();
  for (const m of html.matchAll(/<script(?![^>]*\bsrc=)([^>]*)>([\s\S]*?)<\/script>/g)) {
    if (/type\s*=\s*["']?application\/(ld\+)?json/i.test(m[1])) continue;
    hashes.add(`'sha256-${createHash("sha256").update(m[2], "utf8").digest("base64")}'`);
  }
  return [...hashes].sort();
}

function compute() {
  const out = {};
  for (const route of ROUTES) {
    const file = join(APP_DIR, `${route.slice(1)}.html`);
    if (existsSync(file)) out[route] = inlineScriptHashes(readFileSync(file, "utf8"));
  }
  return out;
}

const now = compute();
if (process.argv.includes("--verify")) {
  const used = existsSync(HASH_FILE) ? JSON.parse(readFileSync(HASH_FILE, "utf8")) : {};
  const bad = ROUTES.filter((r) => now[r] && JSON.stringify(now[r]) !== JSON.stringify(used[r]));
  if (bad.length) {
    console.error(`csp-hashes: the CSP of ${bad.join(", ")} does not match its final HTML; refusing to ship pages that would block their own scripts.`);
    process.exit(1);
  }
  console.log(`csp-hashes: verified ${Object.keys(now).length} page policies against the final HTML`);
} else {
  writeFileSync(HASH_FILE, JSON.stringify(now, null, 1) + "\n");
  console.log(`csp-hashes: wrote hashes for ${Object.keys(now).length} pages`);
}
