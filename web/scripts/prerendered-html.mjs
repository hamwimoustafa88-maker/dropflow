// Where `next build` left the prerendered HTML of a route. Two layouts exist in Next 16.4:
//   plain build                      .next/server/app/ar.html, .next/server/app/ar/app.html
//   build with a deployment adapter  .next/server/route-cache/APP_PAGE/<sha256 of the source route>/$/ar.html
// Vercel always builds with its adapter, so the second layout is the one production uses; looking only for the first one made
// scripts/csp-hashes.mjs find no page on Vercel and ship every page without its script hashes (Kashef scan, 2026-11-11).
import { existsSync, readdirSync, statSync } from "node:fs";
import { join, relative, sep } from "node:path";

function walk(dir) {
  if (!existsSync(dir)) return [];
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    const p = join(dir, e.name);
    return e.isDirectory() ? walk(p) : [p];
  });
}

/** Absolute path of the prerendered HTML of `route` ("/ar", "/en/app", ...) under `distDir`, or undefined when there is none. */
export function prerenderedHtml(distDir, route) {
  const plain = join(distDir, "server", "app", `${route.slice(1)}.html`);
  if (existsSync(plain)) return plain;
  const cacheDir = join(distDir, "server", "route-cache");
  const suffix = `/$${route}.html`;
  const found = walk(cacheDir).filter((f) => ("/" + relative(cacheDir, f).split(sep).join("/")).endsWith(suffix));
  // One file per route; should an earlier build have left another, the newest is the one this build wrote.
  return found.sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs)[0];
}
