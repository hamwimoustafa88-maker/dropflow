// Runs after `next build`. Next.js puts small inline <script> blocks into every static page (its bootstrap and the RSC payload),
// so a CSP without 'unsafe-inline' needs their SHA-256 hashes. Those are only known once the HTML exists, and they change with
// every build, so next.config.ts writes a placeholder ('csp-hashes:/ar') into each page's CSP header and this script replaces it,
// in .next/routes-manifest.json (read by `next start` and by Vercel), with the hashes of that page's own inline scripts.
// When hashes are present browsers ignore 'unsafe-inline' (CSP level 2+); it stays only as a fallback for very old browsers.
// If a page's HTML is missing the placeholder is removed and the page keeps the plain 'unsafe-inline' policy (never a broken page).
import { createHash } from "node:crypto";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const next = join(process.cwd(), ".next");
const manifestPath = join(next, "routes-manifest.json");
const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
const TOKEN = /'csp-hashes:([^']+)'/g;

/** Hashes of the executable inline scripts of one prerendered page (JSON-LD data blocks are not scripts to CSP). */
export function inlineScriptHashes(html) {
  const hashes = new Set();
  for (const m of html.matchAll(/<script(?![^>]*\bsrc=)([^>]*)>([\s\S]*?)<\/script>/g)) {
    if (/type\s*=\s*["']?application\/(ld\+)?json/i.test(m[1])) continue;
    hashes.add(`'sha256-${createHash("sha256").update(m[2], "utf8").digest("base64")}'`);
  }
  return [...hashes];
}

let pages = 0;
let missing = 0;
for (const rule of manifest.headers ?? []) {
  for (const header of rule.headers) {
    if (header.key.toLowerCase() !== "content-security-policy" || !header.value.includes("'csp-hashes:")) continue;
    header.value = header.value.replace(TOKEN, (_, route) => {
      const file = join(next, "server", "app", `${route.replace(/^\//, "")}.html`);
      if (!existsSync(file)) {
        missing++;
        console.warn(`csp-hashes: no prerendered HTML for ${route}; it keeps 'unsafe-inline' only`);
        return "";
      }
      pages++;
      return inlineScriptHashes(readFileSync(file, "utf8")).join(" ");
    }).replace(/\s{2,}/g, " ").replace(/ ;/g, ";");
  }
}

writeFileSync(manifestPath, JSON.stringify(manifest));
console.log(`csp-hashes: ${pages} page policies hashed${missing ? `, ${missing} without HTML` : ""}`);
