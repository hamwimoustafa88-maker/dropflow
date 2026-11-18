import { readFileSync } from "node:fs";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { DEFAULT_IMAGE_CAPS, setImageCaps, setMediaCaps } from "@/engine/caps";
import { CORE_VERSION, buildMediaCaps } from "@/engine/lib/ffmpeg-probe";
import { registry } from "@/engine/registry";
import { PAIRS, formatName, inCatalog, pairBySlug, related, slugOf } from "../pairs";
import { PAGES } from "../seo";

const fx = (n: string) => readFileSync(new URL(`../../engine/__tests__/fixtures/ffmpeg-${n}.txt`, import.meta.url), "utf8");

describe("popular conversion pages", () => {
  beforeAll(() => {
    // A Chromium browser: canvas encodes WebP; the media core is the one the app ships.
    setImageCaps({ ...DEFAULT_IMAGE_CAPS, encode: { ...DEFAULT_IMAGE_CAPS.encode, webp: true } });
    setMediaCaps(buildMediaCaps(CORE_VERSION, { encoders: fx("encoders"), decoders: fx("decoders"), muxers: fx("muxers"), demuxers: fx("demuxers"), filters: fx("filters") }));
  });
  afterAll(() => {
    setImageCaps(DEFAULT_IMAGE_CAPS);
    setMediaCaps(null);
  });

  it("only lists conversions the desktop catalog really has", () => {
    for (const p of PAIRS) expect(inCatalog(p.from, p.to), p.slug).toBe(true);
  });

  it("says 'in the browser' exactly when the web app converts the pair", () => {
    for (const p of PAIRS) expect(!!registry.find(`convert:${p.to}`, p.from), p.slug).toBe(p.web);
  });

  it("has unique, URL-safe slugs that resolve back to their pair", () => {
    const slugs = PAIRS.map((p) => p.slug);
    expect(new Set(slugs).size).toBe(slugs.length);
    for (const p of PAIRS) {
      expect(p.slug).toMatch(/^[a-z0-9]+-to-[a-z0-9]+$/);
      expect(pairBySlug(p.slug)).toBe(p);
      expect(p.slug).toBe(slugOf(p.from, p.to));
    }
  });

  it("is in the sitemap page list, and every page links somewhere related", () => {
    for (const p of PAIRS) {
      expect(PAGES).toContain(`/convert/${p.slug}`);
      expect(related(p).length, p.slug).toBeGreaterThan(0);
    }
  });

  it("uses friendly names in both languages", () => {
    expect(formatName("docx", "ar")).toBe("وورد");
    expect(formatName("docx", "en")).toBe("Word");
    expect(formatName("heic", "ar")).toBe("HEIC");
  });
});
