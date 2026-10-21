// Generates public/sw.js from scripts/sw.template.js AFTER `next build`: the precache list is the real output of that
// build (every /_next/static asset, both /app pages, icons, manifests, vendor files), and the version is a hash of it,
// so a new deploy automatically becomes a new service-worker version and goes through the "Update" flow.
import { createHash } from "node:crypto";
import { existsSync, readdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const dist = join(root, process.env.NEXT_DIST_DIR ?? ".next");
const publicDir = join(root, "public");
const locales = ["ar", "en"];

function walk(dir) {
  if (!existsSync(dir)) return [];
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    const p = join(dir, e.name);
    return e.isDirectory() ? walk(p) : [p];
  });
}
const toUrl = (base, file) => "/" + relative(base, file).split(sep).map(encodeURIComponent).join("/");
const sha = (buf) => createHash("sha256").update(buf).digest("hex").slice(0, 16);

// 1. The app shell: the two prerendered /app pages ...
// The hash only versions the service worker. Where the prerendered HTML is not on disk (a hosting build can lay the output
// out differently), fall back to the build id with a warning instead of failing the whole deploy.
const buildIdFile = join(dist, "BUILD_ID");
const buildId = existsSync(buildIdFile) ? readFileSync(buildIdFile, "utf8").trim() : "no-build-id";
if (!existsSync(dist)) throw new Error(`build-sw: ${dist} is missing - run \`next build\` first`);
const pages = [];
for (const l of locales) {
  const html = join(dist, "server", "app", l, "app.html");
  if (existsSync(html)) {
    pages.push({ url: `/${l}/app`, hash: sha(readFileSync(html)) });
  } else {
    console.warn(`build-sw: ${html} not found; versioning /${l}/app by the build id instead`);
    pages.push({ url: `/${l}/app`, hash: sha(`${buildId}:${l}`) });
  }
}

// 2. ... every static asset of the build (hashed chunks, css, fonts, worker chunks), source maps excluded.
const staticFiles = walk(join(dist, "static"))
  .filter((f) => !f.endsWith(".map"))
  .map((f) => ({ url: "/_next/static" + toUrl(join(dist, "static"), f), size: statSync(f).size }))
  .sort((a, b) => a.url.localeCompare(b.url));

// 3. Icons + manifests + favicon.
const small = ["/manifest.webmanifest", "/manifest.en.webmanifest"].filter((u) => existsSync(join(publicDir, u)));
const icons = walk(join(publicDir, "icons")).map((f) => toUrl(publicDir, f));
const faviconRoute = existsSync(join(dist, "server", "app", "icon.svg")) ? ["/icon.svg"] : [];

// 4. Vendor files (copied from node_modules by scripts/vendor.mjs).
const vendorRoot = join(publicDir, "vendor");
const vendorAll = walk(vendorRoot).map((f) => ({ url: toUrl(publicDir, f), size: statSync(f).size }));
// REQUIRED (installed with the shell, so offline works right after the first visit): ffmpeg glue + core loader, the
// pdf.js worker, its standard fonts, image-decoder wasm and ICC profiles. OPTIONAL (best-effort, fetched after install
// when the page asks): the ~170 CJK CMap files and no-wasm fallbacks. The 31 MB ffmpeg-core.wasm is never precached.
const isRequiredVendor = (u) =>
  /^\/vendor\/ffmpeg\/(worker|const|errors|ffmpeg-core)\.js$/.test(u) ||
  u === "/vendor/pdfjs/pdf.worker.min.mjs" ||
  /^\/vendor\/pdfjs\/standard_fonts\/[^/]+\.(pfb|ttf)$/.test(u) ||
  /^\/vendor\/pdfjs\/wasm\/[^/]+\.wasm$/.test(u) ||
  /^\/vendor\/pdfjs\/iccs\/[^/]+\.icc$/.test(u) ||
  u === "/vendor/versions.json";
// ON FIRST USE (neither list): the heavy on-device engines - Tesseract OCR (/vendor/tesseract, ~8 MB), libheif
// (/vendor/heif, 2 MB), ONNX Runtime + U2-Net-P (/vendor/ort, /vendor/models, ~18 MB). The OPTIONAL list is fetched in the
// background for EVERY visitor, which would cost ~28 MB on a phone that never uses these tools; instead the service
// worker's cache-first /vendor route stores each file the first time a tool really loads it, so it then works offline.
const isOptionalVendor = (u) => /^\/vendor\/pdfjs\/(cmaps\/[^/]+\.bcmap|wasm\/[^/]+\.js)$/.test(u);
const vendorRequired = vendorAll.filter((v) => isRequiredVendor(v.url)).map((v) => v.url);
const vendorOptional = vendorAll.filter((v) => !isRequiredVendor(v.url) && isOptionalVendor(v.url)).map((v) => v.url);

const shellUrls = [...new Set([...pages.map((p) => p.url), ...staticFiles.map((s) => s.url), ...small, ...icons, ...faviconRoute])];
const version = sha(JSON.stringify([pages, staticFiles]));
const vendorVersion = sha(JSON.stringify(vendorAll));

const template = readFileSync(join(root, "scripts", "sw.template.js"), "utf8");
const out = template
  .replace("__VERSION__", version)
  .replace("__VENDOR_VERSION__", vendorVersion)
  .replace("__SHELL_URLS__", JSON.stringify(shellUrls, null, 1))
  .replace("__VENDOR_URLS__", JSON.stringify(vendorRequired, null, 1))
  .replace("__OPTIONAL_URLS__", JSON.stringify(vendorOptional, null, 1));
writeFileSync(join(publicDir, "sw.js"), out);

const shellBytes = staticFiles.reduce((n, s) => n + s.size, 0);
console.log(
  `build-sw: version ${version} - ${shellUrls.length} shell files (${(shellBytes / 1024).toFixed(0)} KB static), ` +
    `${vendorRequired.length} vendor + ${vendorOptional.length} optional files -> public/sw.js`,
);
