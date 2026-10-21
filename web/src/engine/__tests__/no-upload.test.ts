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
      const hits = files.filter((f) => re.test(code(f.text))).map((f) => f.path);
      expect(hits).toEqual([]);
    });
  }

  it("the only absolute URLs in app source are the site's own placeholders", () => {
    const urls = new Set<string>();
    for (const f of files) for (const m of code(f.text).matchAll(/["'`](https?:\/\/[^"'`\s]+)/g)) urls.add(`${f.path}: ${m[1]}`);
    const allowed = [
      "src/app/[locale]/layout.tsx: https://${process.env.VERCEL_PROJECT_PRODUCTION_URL}",
      "src/app/[locale]/layout.tsx: http://localhost:3000",
      "src/components/LegalPage.tsx: https://github.com/hamwimoustafa88-maker/dropflow/issues", // a link the visitor may click, never fetched
      "src/components/DownloadButtons.tsx: https://github.com/hamwimoustafa88-maker/dropflow/releases/latest", // the Windows download link, clicked by the visitor, never fetched
      "src/engine/lib/strip-meta.ts: http://ns.adobe.com/xap", // the XMP packet signature we look FOR, not a request
    ];
    expect([...urls].filter((u) => !allowed.includes(u))).toEqual([]);
  });

  it("engine assets are same-origin paths (/vendor/...), never a CDN", () => {
    const pdfjs = readFileSync(join(srcDir, "engine/lib/pdfjs-setup.ts"), "utf8");
    expect(pdfjs).toContain('PDFJS_BASE = "/vendor/pdfjs/"');
    expect(code(pdfjs)).not.toMatch(/https?:\/\//);
    expect(code(pdfjs)).not.toMatch(/unpkg|jsdelivr|cdnjs|cdn\./);
    for (const f of files) expect(code(f.text)).not.toMatch(/unpkg\.com|jsdelivr|cdnjs|googleapis\.com\/(?!css)/);
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

  it("the page CSP forbids talking to any other origin (browser-enforced)", () => {
    const cfg = readFileSync(join(root, "next.config.ts"), "utf8");
    expect(cfg).toContain("connect-src 'self' blob: data:");
    expect(cfg).toContain("default-src 'self'");
    expect(cfg).toContain("form-action 'none'");
    expect(cfg).toContain("worker-src 'self' blob:");
    expect(cfg).not.toMatch(/(connect-src|default-src|img-src|script-src)[^"]*https?:/);
    // Web Workers take their policy from their own script response: the engine/vendor/chunk paths carry it too
    expect(cfg).toContain('"/_next/static/:path*"');
    expect(cfg).toContain('"/vendor/:path*"');
  });

  it("no COOP/COEP headers are needed or sent (no SharedArrayBuffer is used)", () => {
    const cfg = readFileSync(join(root, "next.config.ts"), "utf8");
    expect(cfg).not.toMatch(/Cross-Origin-(Opener|Embedder)-Policy/);
    const vendor = readFileSync(join(root, "scripts/vendor.mjs"), "utf8");
    expect(vendor).toContain("pdfjs-dist/build/pdf.worker.min.mjs");
    expect(vendor).not.toContain("core-mt");
  });

  it("no GPL ffmpeg engine ships in the web app (proprietary licence, ADR-0010)", () => {
    const pkg = JSON.parse(readFileSync(join(root, "package.json"), "utf8")) as { dependencies: Record<string, string> };
    expect(Object.keys(pkg.dependencies).filter((d) => d.startsWith("@ffmpeg/"))).toEqual([]);
    expect(readFileSync(join(root, "scripts/vendor.mjs"), "utf8")).not.toMatch(/ffmpeg/i);
    expect(readFileSync(join(root, "scripts/sw.template.js"), "utf8")).not.toMatch(/ffmpeg/i);
  });
});
