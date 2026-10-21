// Renders the PWA icon PNGs from the logo geometry (src/app/icon.svg) with headless Edge/Chrome over the DevTools
// protocol - no image library needed. Dev tool, run by hand: `node scripts/gen-icons.mjs` (set BROWSER to override).
// Outputs: public/icons/{icon-192,icon-512,icon-maskable-512,apple-touch-icon}.png
import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const browser = process.env.BROWSER ?? "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe";
const svg = readFileSync(join(root, "src", "app", "icon.svg"), "utf8");
// Inner artwork = everything after the background <rect>, so it can be reused on a full-bleed (maskable) canvas.
const art = svg.replace(/^[\s\S]*?<rect[^>]*\/>/, "").replace(/<\/svg>\s*$/, "");

const page = (size, kind) => {
  if (kind === "any") return `<body style="margin:0;background:transparent">${svg.replace("<svg ", `<svg width="${size}" height="${size}" `)}</body>`;
  // maskable / apple: full-bleed background, artwork scaled into the 80% safe zone
  return `<body style="margin:0;background:#070b1c"><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48" width="${size}" height="${size}"><rect width="48" height="48" fill="#070b1c"/><g transform="translate(4.8 4.8) scale(0.8)">${art}</g></svg></body>`;
};

const port = 9800 + Math.floor(Math.random() * 150);
const proc = spawn(browser, ["--headless=new", "--disable-gpu", "--hide-scrollbars", `--remote-debugging-port=${port}`, `--user-data-dir=${mkdtempSync(join(tmpdir(), "df-icons-"))}`, "about:blank"], { stdio: "ignore" });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let target;
for (let i = 0; i < 60 && !target; i++) {
  try {
    target = (await (await fetch(`http://127.0.0.1:${port}/json`)).json()).find((t) => t.type === "page");
  } catch {
    /* starting */
  }
  if (!target) await sleep(200);
}
if (!target) throw new Error("browser did not start");
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((r) => (ws.onopen = r));
let id = 0;
const pending = new Map();
ws.onmessage = (m) => {
  const msg = JSON.parse(m.data);
  if (msg.id && pending.has(msg.id)) {
    pending.get(msg.id)(msg);
    pending.delete(msg.id);
  }
};
const send = (method, params = {}) => new Promise((res) => { const n = ++id; pending.set(n, res); ws.send(JSON.stringify({ id: n, method, params })); });

await send("Page.enable");
const jobs = [
  ["icon-192.png", 192, "any"],
  ["icon-512.png", 512, "any"],
  ["icon-maskable-512.png", 512, "maskable"],
  ["apple-touch-icon.png", 180, "maskable"],
];
for (const [name, size, kind] of jobs) {
  await send("Emulation.setDeviceMetricsOverride", { width: size, height: size, deviceScaleFactor: 1, mobile: false });
  await send("Emulation.setDefaultBackgroundColorOverride", { color: { r: 0, g: 0, b: 0, a: 0 } });
  await send("Page.navigate", { url: "data:text/html;charset=utf-8," + encodeURIComponent(page(size, kind)) });
  await sleep(400);
  const shot = await send("Page.captureScreenshot", { format: "png", clip: { x: 0, y: 0, width: size, height: size, scale: 1 }, fromSurface: true });
  writeFileSync(join(root, "public", "icons", name), Buffer.from(shot.result.data, "base64"));
  console.log(`wrote public/icons/${name} (${size}x${size}, ${kind})`);
}
ws.close();
proc.kill();
