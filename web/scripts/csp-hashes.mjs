// Hash-based CSP for the static pages, in two build passes (see "build" in package.json).
// Next.js puts small inline <script> blocks into every static page (its bootstrap and the RSC payload), so a CSP without
// 'unsafe-inline' needs their SHA-256 hashes. They are only known once the HTML exists, and Vercel takes response headers from
// next.config.ts as evaluated DURING `next build` (it ignores later edits to .next/routes-manifest.json). So:
//   pass 1  next build                      -> HTML
//           node scripts/csp-hashes.mjs     -> writes csp-hashes.json { "/ar": ["'sha256-…'", …], … }
//   pass 2  next build                      -> next.config.ts puts each page's hashes in its CSP header INSTEAD of 'unsafe-inline'
//           node scripts/csp-hashes.mjs --verify -> recomputes from the final HTML; FAILS the build on any difference,
//                                              so a page can never ship with a policy that blocks its own scripts.
// The build id is fixed (next.config.ts generateBuildId), which keeps the inline scripts identical between the two passes.
// A page whose HTML is not found keeps 'unsafe-inline' (it still works, but loses the protection). On Vercel (VERCEL=1) and in
// CI (CSP_REQUIRE_HASHES=1) that is an error: it went unnoticed for weeks when Next moved the HTML (scripts/prerendered-html.mjs).
import { createHash } from "node:crypto";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { prerenderedHtml } from "./prerendered-html.mjs";

export const HASH_FILE = join(process.cwd(), "csp-hashes.json");
const DIST_DIR = join(process.cwd(), process.env.NEXT_DIST_DIR ?? ".next");
const ROUTES = ["ar", "en"].flatMap((l) => [`/${l}`, `/${l}/privacy`, `/${l}/terms`, `/${l}/app`]);
const REQUIRE = process.env.CSP_REQUIRE_HASHES === "1" || process.env.VERCEL === "1";

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
    const file = prerenderedHtml(DIST_DIR, route);
    if (file) out[route] = inlineScriptHashes(readFileSync(file, "utf8"));
  }
  return out;
}

function fail(msg) {
  console.error(`csp-hashes: ${msg}`);
  process.exit(1);
}

const now = compute();
const missing = ROUTES.filter((r) => !now[r]);
if (missing.length) {
  const msg = `no prerendered HTML found for ${missing.join(", ")} under ${DIST_DIR}; those pages would keep 'unsafe-inline'.`;
  if (REQUIRE) fail(msg);
  console.warn(`csp-hashes: ${msg}`);
}

if (process.argv.includes("--verify")) {
  const used = existsSync(HASH_FILE) ? JSON.parse(readFileSync(HASH_FILE, "utf8")) : {};
  const bad = ROUTES.filter((r) => now[r] && JSON.stringify(now[r]) !== JSON.stringify(used[r]));
  if (bad.length) fail(`the CSP of ${bad.join(", ")} does not match its final HTML; refusing to ship pages that would block their own scripts.`);
  // The headers the build actually emitted (what Vercel and `next start` serve) must carry exactly these hashes, and then
  // no 'unsafe-inline' for scripts (a scanner reads the header as written, whatever browsers would ignore).
  const manifest = JSON.parse(readFileSync(join(DIST_DIR, "routes-manifest.json"), "utf8"));
  const served = {};
  for (const rule of manifest.headers ?? [])
    for (const h of rule.headers)
      if (h.key.toLowerCase() === "content-security-policy" && ROUTES.includes(rule.source)) served[rule.source] = h.value;
  const hashesOf = (csp) => [...(csp ?? "").matchAll(/'sha256-[^']+'/g)].map((m) => m[0]).sort();
  const scriptSrc = (csp) => /(?:^|;)\s*script-src([^;]*)/.exec(csp ?? "")?.[1] ?? "";
  const wrong = ROUTES.filter((r) => now[r] && hashesOf(served[r]).length && JSON.stringify(hashesOf(served[r])) !== JSON.stringify(now[r]));
  if (wrong.length) fail(`the served CSP of ${wrong.join(", ")} lists hashes that do not match the page; refusing to ship.`);
  const absent = ROUTES.filter((r) => now[r] && !hashesOf(served[r]).length);
  if (absent.length) {
    const msg = `the served CSP of ${absent.join(", ")} carries no hashes (next.config.ts did not find csp-hashes.json); the pages fall back to 'unsafe-inline'.`;
    if (REQUIRE) fail(msg);
    console.warn(`csp-hashes: ${msg}`);
  }
  const inline = ROUTES.filter((r) => hashesOf(served[r]).length && /'unsafe-inline'/.test(scriptSrc(served[r])));
  if (inline.length) fail(`the served CSP of ${inline.join(", ")} still allows 'unsafe-inline' scripts next to its hashes.`);
  const hashed = ROUTES.filter((r) => hashesOf(served[r]).length).length;
  console.log(`csp-hashes: verified ${Object.keys(now).length} of ${ROUTES.length} pages; ${hashed} served with hashes and no 'unsafe-inline'`);
} else {
  writeFileSync(HASH_FILE, JSON.stringify(now, null, 1) + "\n");
  console.log(`csp-hashes: wrote hashes for ${Object.keys(now).length} of ${ROUTES.length} pages`);
}
