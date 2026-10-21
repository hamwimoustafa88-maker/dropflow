import type { NextConfig } from "next";
import createNextIntlPlugin from "next-intl/plugin";

const isProd = process.env.NODE_ENV === "production";

// DropFlow Web must never send a file anywhere. Besides the code never doing so, the browser enforces it: on /app the
// page, its Web Workers (module workers take their policy from their OWN script response, hence the headers on
// /_next/static and /vendor too) and the service worker may only talk to their own origin (static assets and WASM).
// 'unsafe-inline' scripts are Next's inline bootstrap; 'wasm-unsafe-eval' lets ffmpeg.wasm / pdf.js compile WebAssembly.
const APP_CSP = [
  "default-src 'self'",
  "script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' blob: data:",
  "media-src 'self' blob:",
  "font-src 'self' data: blob:",
  "connect-src 'self' blob: data:",
  "worker-src 'self' blob:",
  "manifest-src 'self'",
  "object-src 'none'",
  "base-uri 'self'",
  "form-action 'none'",
  "frame-ancestors 'none'",
].join("; ");

const nextConfig: NextConfig = {
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },
  async headers() {
    // ffmpeg.wasm runs as the SINGLE-thread core, which needs no SharedArrayBuffer, so no COOP/COEP headers are sent
    // (they would also restrict the landing page and third-party embeds for no benefit).
    const csp = { key: "Content-Security-Policy", value: APP_CSP };
    return [
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
            { source: "/vendor/:path*", headers: [csp] },
          ]
        : []),
    ];
  },
};

const withNextIntl = createNextIntlPlugin();
export default withNextIntl(nextConfig);
