// DropFlow Web end-to-end test in headless Edge over the DevTools protocol.
//   node e2e/run.mjs [baseUrl=http://localhost:3200] [--only=images,pdf,...] [--out=<dir for screenshots/downloads>]
// Needs a production server (`npm run build && npx next start --port 3200`). Uses real files written by the test,
// real file inputs / drag events (DOM.setFileInputFiles, Input.dispatchDragEvent) and real downloads; asserts the bytes.
import { mkdirSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { open, sleep } from "./cdp.mjs";
import { check, results } from "./helpers.mjs";
import { makeFiles } from "./files.mjs";
import { landing } from "./sections/landing.mjs";
import { images } from "./sections/images.mjs";
import { pdfs } from "./sections/pdf.mjs";
import { textTools } from "./sections/text.mjs";
import { wheelUx } from "./sections/wheel-ux.mjs";
import { media } from "./sections/media.mjs";
import { offline } from "./sections/offline.mjs";
import { privacy } from "./sections/privacy.mjs";
import { quality } from "./sections/quality.mjs";

const args = process.argv.slice(2);
const base = (args.find((a) => !a.startsWith("--")) ?? "http://localhost:3200").replace(/\/$/, "");
const only = (args.find((a) => a.startsWith("--only=")) ?? "").slice(7).split(",").filter(Boolean);
const out = args.find((a) => a.startsWith("--out="))?.slice(6) ?? join(tmpdir(), "dropflow-e2e");
mkdirSync(out, { recursive: true });

const files = await makeFiles(join(out, "inputs"));
const b = await open({ width: 1280, height: 900, scheme: "dark" });
const ctx = { b, base, out, files, only };
const wants = (name) => only.length === 0 || only.includes(name);

const t0 = Date.now();
try {
  if (wants("landing")) await landing(ctx);
  if (wants("images")) await images(ctx);
  if (wants("pdf")) await pdfs(ctx);
  if (wants("text")) await textTools(ctx);
  if (wants("wheel")) await wheelUx(ctx);
  if (wants("media")) await media(ctx);
  if (wants("offline")) await offline(ctx);
  if (wants("quality")) await quality(ctx);
  if (wants("privacy")) await privacy(ctx);
  check("no console errors / exceptions in the whole run", b.errors.length === 0, b.errors.slice(0, 4).join(" | "));
} finally {
  writeFileSync(join(out, "results.json"), JSON.stringify({ base, seconds: Math.round((Date.now() - t0) / 1000), results, errors: b.errors, requests: b.requests.length }, null, 2));
  await sleep(200);
  b.close();
}
const failed = results.filter((r) => !r.ok);
console.log(`\n${results.length - failed.length}/${results.length} checks passed in ${Math.round((Date.now() - t0) / 1000)}s`);
if (failed.length) console.log("FAILED:\n" + failed.map((f) => " - " + f.name + (f.detail ? ": " + f.detail : "")).join("\n"));
process.exit(failed.length ? 1 : 0);
