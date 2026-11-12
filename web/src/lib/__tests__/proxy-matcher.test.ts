import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { PROXY_MATCHER, proxyRuns } from "../proxy-matcher";

describe("locale proxy matcher (Vercel middleware invocations)", () => {
  it("is the same string as src/proxy.ts uses", () => {
    const proxy = readFileSync(new URL("../../proxy.ts", import.meta.url), "utf8");
    expect(proxy).toContain(`matcher: ${JSON.stringify(PROXY_MATCHER)}`);
  });

  it("skips every page that already has a language, so page views cost no middleware call", () => {
    for (const p of ["/ar", "/en", "/ar/", "/ar/app", "/en/app", "/ar/privacy", "/en/terms"]) expect(proxyRuns(p), p).toBe(false);
  });

  it("still adds the language to paths without one", () => {
    for (const p of ["/app", "/privacy", "/terms", "/xyz", "/arabic", "/english/page"]) expect(proxyRuns(p), p).toBe(true);
  });

  it("never touches engines, icons, Next internals or files", () => {
    for (const p of ["/vendor/ffmpeg/ffmpeg-core.wasm", "/icons/x.png", "/_next/static/a.js", "/sw.js", "/robots.txt", "/sitemap.xml"])
      expect(proxyRuns(p), p).toBe(false);
  });
});
