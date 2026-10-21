import { existsSync, readFileSync } from "node:fs";
import type { NextConfig } from "next";
import createNextIntlPlugin from "next-intl/plugin";

const isProd = process.env.NODE_ENV === "production";

// DropFlow Web must never send a file anywhere. Besides the code never doing so, the browser enforces it: on /app the
// page, its Web Workers (module workers take their policy from their OWN script response, hence the headers on
// /_next/static and /vendor too) and the service worker may only talk to their own origin (static assets and WASM),
// with ONE exception: `https:` in connect-src lets the "drop a link" feature download the file at an address the
// user gave (src/engine/lib/fetch-link.ts, a GET without credentials or body; the static no-upload test allows no
// other network call). Forms, beacons over http, WebSockets and image pings stay blocked.
// 'unsafe-inline' scripts are Next's inline bootstrap; 'wasm-unsafe-eval' lets ffmpeg.wasm / pdf.js compile WebAssembly;
// media-src blob: plays the converted audio/video previews.
const APP_CSP = [
  "default-src 'self'",
  "script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' blob: data:",
  "media-src 'self' blob:",
  "font-src 'self' data: blob:",
  "connect-src 'self' blob: data: https:",
  "worker-src 'self' blob:",
  "manifest-src 'self'",
  "object-src 'none'",
  "base-uri 'self'",
  "form-action 'none'",
  "frame-ancestors 'none'",
].join("; ");

// Every other page (landing, legal pages) is static HTML that loads only its own scripts, styles, fonts and images.
// JSON-LD is a data block (type="application/ld+json"), never executed, so it needs nothing here.
const SITE_CSP = [
  "default-src 'self'",
  "script-src 'self' 'unsafe-inline'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data: blob:",
  "font-src 'self' data:",
  "connect-src 'self'",
  "worker-src 'self'",
  "manifest-src 'self'",
  "object-src 'none'",
  "base-uri 'self'",
  "form-action 'self'",
  "frame-ancestors 'none'",
].join("; ");

// The site's own origin: replaces the CDN's default `Access-Control-Allow-Origin: *` (nothing here is meant for other sites).
const ORIGIN = (process.env.NEXT_PUBLIC_SITE_URL || "https://dropflow.scouthub.dev").replace(/\/+$/, "");

// Sent on every response. COOP alone isolates the browsing context from pop-ups/openers; no COEP is sent: ffmpeg.wasm
// runs as the SINGLE-thread core, which needs no SharedArrayBuffer, so cross-origin isolation is not needed.
const SECURITY_HEADERS = [
  { key: "X-Content-Type-Options", value: "nosniff" },
  { key: "X-Frame-Options", value: "DENY" },
  { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
  { key: "Permissions-Policy", value: "camera=(), microphone=(), geolocation=(), browsing-topics=()" },
  { key: "Cross-Origin-Opener-Policy", value: "same-origin" },
  { key: "Cross-Origin-Resource-Policy", value: "same-origin" },
  { key: "Strict-Transport-Security", value: "max-age=63072000; includeSubDomains; preload" },
  { key: "Access-Control-Allow-Origin", value: ORIGIN },
];

// Static pages whose CSP carries the SHA-256 hashes of their own inline scripts. csp-hashes.json is written by
// scripts/csp-hashes.mjs after the first build pass and read here by the second (see package.json "build"); without it a page
// keeps the plain policy. With hashes present, browsers ignore 'unsafe-inline' for scripts.
const LOCALES = ["ar", "en"];
const HASHED_PAGES = ["", "/privacy", "/terms"];
const CSP_HASHES: Record<string, string[]> = existsSync("csp-hashes.json") ? JSON.parse(readFileSync("csp-hashes.json", "utf8")) : {};
const withHashes = (policy: string, route: string) =>
  CSP_HASHES[route]?.length
    ? policy.replace("script-src 'self' 'unsafe-inline'", `script-src 'self' 'unsafe-inline' ${CSP_HASHES[route].join(" ")}`)
    : policy;

const nextConfig: NextConfig = {
  poweredByHeader: false,
  // A stable build id (the commit on Vercel) keeps the inline bootstrap scripts identical between the two build passes, so the
  // CSP hashes computed after the first pass match the HTML of the second (see scripts/csp-hashes.mjs).
  generateBuildId: async () => process.env.VERCEL_GIT_COMMIT_SHA || process.env.DROPFLOW_BUILD_ID || "dropflow-local",
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },
  // Arabic first: "/" always opens the Arabic site (308), never guessed from Accept-Language; the switcher is visible.
  async redirects() {
    return [{ source: "/", destination: "/ar", permanent: true }];
  },
  async headers() {
    const csp = { key: "Content-Security-Policy", value: APP_CSP };
    // Later entries override earlier ones for the same header, so the strict /app policy below wins over the site policy.
    return [
      { source: "/:path*", headers: [...SECURITY_HEADERS, ...(isProd ? [{ key: "Content-Security-Policy", value: SITE_CSP }] : [])] },
      {
        source: "/sw.js",
        headers: [
          { key: "Cache-Control", value: "no-cache, no-store, must-revalidate" },
          { key: "Service-Worker-Allowed", value: "/" },
        ],
      },
      ...(isProd
        ? [
            {
              source: "/:locale(ar|en)/app",
              headers: [csp, { key: "X-Content-Type-Options", value: "nosniff" }, { key: "Referrer-Policy", value: "no-referrer" }],
            },
            { source: "/_next/static/:path*", headers: [csp] },
            ...LOCALES.flatMap((locale) => [
              ...HASHED_PAGES.map((page) => ({
                source: `/${locale}${page}`,
                headers: [{ key: "Content-Security-Policy", value: withHashes(SITE_CSP, `/${locale}${page}`) }],
              })),
              {
                source: `/${locale}/app`,
                headers: [{ key: "Content-Security-Policy", value: withHashes(APP_CSP, `/${locale}/app`) }],
              },
            ]),
            { source: "/vendor/:path*", headers: [csp] },
          ]
        : []),
    ];
  },
};

const withNextIntl = createNextIntlPlugin();
export default withNextIntl(nextConfig);
