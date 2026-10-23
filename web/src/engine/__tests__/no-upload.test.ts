import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

// "Privacy is the product": no code path may send user data anywhere. This is the static half of the proof (the
// browser half - CSP + a network audit of the whole e2e run - lives in e2e/). Source is scanned, not trusted.
const root = fileURLToPath(new URL("../../../", import.meta.url));
const srcDir = join(root, "src");

function walk(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const p = join(dir, name);
    return statSync(p).isDirectory() ? walk(p) : [p];
  });
}

/** App source: everything under src except tests and the generated catalog. */
const files = walk(srcDir)
  .filter((f) => /\.(ts|tsx)$/.test(f) && !f.includes(`${sep}__tests__${sep}`) && !f.includes(`${sep}data${sep}`))
  .map((f) => ({ path: relative(root, f).split(sep).join("/"), text: readFileSync(f, "utf8") }));

/** Strips comments so documentation that MENTIONS fetch/POST does not count as code. */
function code(text: string): string {
  return text.replace(/\/\*[\s\S]*?\*\//g, "").replace(/(^|[^:])\/\/.*$/gm, "$1");
}

/** The ONLY module allowed to make a network request: it downloads the file at an address the user gave. */
const LINK_MODULE = "src/engine/lib/fetch-link.ts";

const FORBIDDEN: [string, RegExp][] = [
  ["fetch()", /\bfetch\s*\(/],
  ["XMLHttpRequest", /\bXMLHttpRequest\b/],
  ["navigator.sendBeacon", /\bsendBeacon\b/],
  ["WebSocket", /\bWebSocket\b/],
  ["EventSource", /\bEventSource\b/],
  ["WebRTC", /\bRTCPeerConnection\b/],
  ["importScripts", /\bimportScripts\b/],
  ["HTTP write verbs", /method\s*:\s*["'`](POST|PUT|PATCH|DELETE)/i],
  ["FormData upload", /\bnew FormData\b/],
  ["analytics SDKs", /(gtag\(|googletagmanager|google-analytics|plausible\.io|mixpanel|posthog|sentry\.io|@sentry|amplitude\.com|segment\.(io|com)|vercel\/analytics|_vercel\/insights)/i],
];

describe("no code path uploads user data", () => {
  it("scans a meaningful amount of source", () => {
    expect(files.length).toBeGreaterThan(60);
  });

  for (const [name, re] of FORBIDDEN) {
    it(`app source contains no ${name}`, () => {
      // The one exception: fetch() in the "drop a link" module, checked line by line in the next test.
      const hits = files.filter((f) => re.test(code(f.text)) && !(name === "fetch()" && f.path === LINK_MODULE)).map((f) => f.path);
      expect(hits).toEqual([]);
    });
  }

  it("the only absolute URLs in app source are the site's own placeholders", () => {
    const urls = new Set<string>();
    for (const f of files) for (const m of code(f.text).matchAll(/["'`](https?:\/\/[^"'`\s]+)/g)) urls.add(`${f.path}: ${m[1]}`);
    const allowed = [
      "src/lib/site.ts: https://dropflow.scouthub.dev", // the site's own canonical address (metadata, robots.txt, sitemap), never fetched
      "src/lib/seo.ts: https://schema.org", // the JSON-LD vocabulary identifier, a name inside structured data, never fetched
      "src/lib/site.ts: https://github.com/hamwimoustafa88-maker/dropflow/issues", // a link the visitor may click, never fetched
      "src/lib/site.ts: https://github.com/hamwimoustafa88-maker/dropflow/blob/main/SECURITY.md", // a link the visitor may click, never fetched
      "src/lib/site.ts: https://github.com/hamwimoustafa88-maker/dropflow/blob/main/docs/code-signing-policy.md", // a link the visitor may click, never fetched
      "src/lib/site.ts: https://github.com/hamwimoustafa88-maker/dropflow/releases/latest", // the Windows download link, clicked by the visitor, never fetched
      "src/lib/site.ts: https://github.com/hamwimoustafa88-maker/dropflow/releases/latest/download", // base of the direct download links (src/lib/downloads.ts), clicked by the visitor, never fetched
      "src/lib/site.ts: https://github.com/hamwimoustafa88-maker/dropflow", // a link the visitor may click, never fetched
      "src/lib/site.ts: https://github.com/hamwimoustafa88-maker/dropflow/blob/main/CONTRIBUTING.md", // a link the visitor may click, never fetched
      "src/lib/site.ts: https://github.com/hamwimoustafa88-maker/dropflow/blob/main/LICENSE", // a link the visitor may click, never fetched
      "src/lib/site.ts: https://github.com/hamwimoustafa88-maker/dropflow/blob/main/TRADEMARK.md", // a link the visitor may click, never fetched
      "src/lib/site.ts: https://github.com/hamwimoustafa88-maker/dropflow/blob/main/THIRD-PARTY-NOTICES.md", // a link the visitor may click, never fetched
      "src/lib/site.ts: https://scouthub.dev/", // the ScoutHub platform link in the credit line, clicked by the visitor, never fetched
      "src/lib/site.ts: https://www.gnu.org/licenses/gpl-3.0.html", // a link the visitor may click, never fetched
      "src/lib/site.ts: https://wa.me/96170863903", // a link the visitor may click (WhatsApp contact), never fetched
      "src/engine/converters/media-engine.ts: http://localhost", // a base for parsing relative URLs, never fetched
      "src/engine/lib/strip-meta.ts: http://ns.adobe.com/xap", // the XMP packet signature we look FOR, not a request
    ];
    expect([...urls].filter((u) => !allowed.includes(u))).toEqual([]);
  });

  it("engine assets are same-origin paths (/vendor/...), never a CDN", () => {
    const pdfjs = readFileSync(join(srcDir, "engine/lib/pdfjs-setup.ts"), "utf8");
    expect(pdfjs).toContain('PDFJS_BASE = "/vendor/pdfjs/"');
    expect(code(pdfjs)).not.toMatch(/https?:\/\//);
    const engine = readFileSync(join(srcDir, "engine/converters/media-engine.ts"), "utf8");
    expect(engine).toContain('const VENDOR = "/vendor/ffmpeg"');
    expect(engine).not.toMatch(/unpkg|jsdelivr|cdnjs|cdn\./);
    expect(code(pdfjs)).not.toMatch(/unpkg|jsdelivr|cdnjs|cdn\./);
    for (const f of files) expect(code(f.text)).not.toMatch(/unpkg\.com|jsdelivr|cdnjs|googleapis\.com\/(?!css)/);
  });

  it("on-device OCR, HEIC and background-removal engines load only from /vendor on this origin, through ONE narrow importer", () => {
    const read = (p: string) => readFileSync(join(srcDir, p), "utf8");
    const ocr = read("engine/lib/ocr-engine.ts");
    expect(ocr).toContain('TESS_BASE = "/vendor/tesseract/"');
    expect(code(ocr)).toContain('cacheMethod: "none"'); // nothing written to IndexedDB
    expect(code(ocr)).toContain("workerBlobURL: false"); // a plain same-origin worker script
    expect(read("engine/lib/heic.ts")).toContain('HEIF_BASE = "/vendor/heif/"');
    const rembg = read("engine/lib/rembg-engine.ts");
    expect(rembg).toContain('ORT_BASE = "/vendor/ort/"');
    expect(rembg).toContain('U2NETP_URL = "/vendor/models/u2netp.onnx"');
    // Runtime (unbundled) imports exist only in the vendor importer, which accepts nothing but /vendor/*.js|mjs paths.
    const dynamic = files.filter((f) => /import\s*\(\s*\/\*\s*webpackIgnore/.test(f.text)).map((f) => f.path);
    expect(dynamic).toEqual(["src/engine/lib/vendor-import.ts"]);
  });

  it("the vendor importer refuses anything that is not a same-origin /vendor module", async () => {
    const { importVendor } = await import("../lib/vendor-import");
    for (const bad of ["https://cdn.example.com/x.js", "//evil.example/x.js", "/vendor/../x.js", "/other/x.js", "data:text/javascript,1", "/vendor/x.wasm", "blob:x"]) {
      await expect(importVendor(bad), bad).rejects.toThrow(/not a vendor module/);
    }
  });

  it("the service worker only GETs same-origin static assets (no body, no method override)", () => {
    const sw = readFileSync(join(root, "scripts/sw.template.js"), "utf8");
    const swCode = code(sw);
    const fetches = [...swCode.matchAll(/\bfetch\s*\(([^)]*)\)/g)].map((m) => m[1].trim());
    expect(fetches.length).toBeGreaterThan(0);
    for (const f of fetches) expect(f).not.toMatch(/method|body|POST|PUT/i);
    expect(swCode).toContain("if (request.method !== \"GET\") return;");
    expect(swCode).toContain("if (url.origin !== self.location.origin) return;");
    expect(swCode).not.toMatch(/\bsendBeacon|XMLHttpRequest|WebSocket|FormData/);
  });

  it("the link module is a plain GET of the user's address: no credentials, no referrer, no body, one call only", () => {
    const link = files.find((f) => f.path === LINK_MODULE);
    expect(link).toBeDefined();
    const src = code(link!.text);
    const calls = [...src.matchAll(/\bfetch\s*\(([^)]*)\)/g)].map((m) => m[1]);
    expect(calls).toHaveLength(1);
    expect(calls[0]).toContain('method: "GET"');
    expect(calls[0]).toContain('credentials: "omit"');
    expect(calls[0]).toContain('referrerPolicy: "no-referrer"');
    expect(calls[0]).not.toMatch(/\bbody\b|headers\s*:|keepalive|method:\s*"(?!GET)/);
    expect(src).not.toMatch(/https?:\/\//); // no built-in address: it only ever downloads what the user gave
    // only the app shell (the drop zone) uses it
    const users = files.filter((f) => f.path !== LINK_MODULE && /from "@\/engine\/lib\/fetch-link"/.test(f.text)).map((f) => f.path);
    expect(users).toEqual(["src/components/app/AppShell.tsx"]);
  });

  it("the page CSP forbids talking to any other origin (browser-enforced), except https downloads of a given link", () => {
    const cfg = readFileSync(join(root, "next.config.ts"), "utf8");
    expect(cfg).toContain("connect-src 'self' blob: data: https:\"");
    expect(cfg).toContain("default-src 'self'");
    expect(cfg).toContain("form-action 'none'");
    expect(cfg).toContain("worker-src 'self' blob:");
    // the only scheme-wide source is `https:` in the /app connect-src; no host is ever named, no http:
    expect(cfg.replace("connect-src 'self' blob: data: https:\"", "")).not.toMatch(/(connect-src|default-src|img-src|script-src|media-src)[^"]*https?:/);
    // Web Workers take their policy from their own script response: the engine/vendor/chunk paths carry it too
    expect(cfg).toContain('"/_next/static/:path*"');
    expect(cfg).toContain('"/vendor/:path*"');
  });

  it("every other page also carries a same-origin CSP and the security headers", () => {
    const cfg = readFileSync(join(root, "next.config.ts"), "utf8");
    for (const d of ["connect-src 'self'\"", "frame-ancestors 'none'", "object-src 'none'", "form-action 'self'"]) expect(cfg).toContain(d);
    for (const h of ["X-Frame-Options", "Strict-Transport-Security", "Permissions-Policy", "Cross-Origin-Resource-Policy", "Referrer-Policy"]) expect(cfg).toContain(`key: "${h}"`);
    expect(cfg).toContain('source: "/:path*"');
    expect(cfg).not.toMatch(/Access-Control-Allow-Origin", value: "\*"/);
  });

  it("no COEP header is needed or sent (single-thread ffmpeg core; COOP alone is a plain security header)", () => {
    const cfg = readFileSync(join(root, "next.config.ts"), "utf8");
    expect(cfg).not.toMatch(/Cross-Origin-Embedder-Policy/);
    const vendor = readFileSync(join(root, "scripts/vendor.mjs"), "utf8");
    expect(vendor).toContain("pdfjs-dist/build/pdf.worker.min.mjs");
    expect(vendor).toContain("@ffmpeg/core/dist/esm/ffmpeg-core.wasm");
    expect(vendor).not.toContain("core-mt");
  });

});
