import { readFileSync } from "node:fs";
import { appMessages } from "./messages-fixture";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, describe, expect, it, vi } from "vitest";
import catalog from "../../data/catalog.json";
import { DEFAULT_IMAGE_CAPS, getImageCaps, setImageCaps, type ImageCaps } from "../caps";
import { CONVERT_TARGETS, IMAGE_TOOLS, MAX_IMAGE_BYTES, imagesConverter as c, ensureImageCaps } from "../converters/images";
import { detectImageCapsWith } from "../lib/image-caps";
import type { ToolOption } from "../types";

const caps = (over: Partial<ImageCaps["encode"]> = {}, decodeAvif = false): ImageCaps => ({
  encode: { ...DEFAULT_IMAGE_CAPS.encode, ...over },
  decodeAvif,
});

afterEach(() => {
  setImageCaps(DEFAULT_IMAGE_CAPS);
  vi.unstubAllGlobals();
});

const FULL = caps({ webp: true, avif: true }, true);
const PLAIN = caps();

describe("manifest sanity", () => {
  it("every operation has options(), suffix() and a hard size limit", () => {
    expect(c.operations.length).toBe(CONVERT_TARGETS.length + IMAGE_TOOLS.length);
    for (const op of c.operations) {
      expect(Array.isArray(c.options(op))).toBe(true);
      expect(c.suffix(op)).toMatch(/^[a-z]+$/);
      expect(c.maxBytes?.(op)).toBe(MAX_IMAGE_BYTES);
    }
    expect(MAX_IMAGE_BYTES).toBe(300 * 1024 * 1024);
  });

  it("convert operations are exactly catalog image outputs we can really produce", () => {
    const image = catalog.categories.find((x) => x.id === "image")!;
    const converts = c.operations.filter((o) => o.startsWith("convert:")).map((o) => o.slice(8));
    expect(converts.sort()).toEqual(["avif", "bmp", "ico", "jpg", "pdf", "png", "webp"]);
    for (const t of converts) expect(image.outputs).toContain(t);
    expect(converts).not.toContain("heic");
    expect(converts).not.toContain("tiff");
    expect(converts).not.toContain("gif");
  });

  it("tool ids exist in the catalog with platform web", () => {
    for (const id of IMAGE_TOOLS) {
      const tool = catalog.tools.find((t) => t.id === id);
      expect(tool, id).toBeDefined();
      expect(tool!.platforms).toContain("web");
    }
  });

  it("the multi flag matches the catalog (img.to-pdf is multi, the rest are not)", () => {
    for (const id of IMAGE_TOOLS) {
      const tool = catalog.tools.find((t) => t.id === id) as { multi?: boolean };
      expect(!!tool.multi).toBe(id === "img.to-pdf");
    }
  });

  it("op-less operations return no options and unknown ops are unsupported", () => {
    expect(c.options("convert:png")).toEqual([]);
    expect(c.options("img.strip-gps")).toEqual([]);
    expect(c.supports("img.nope", "png")).toBe(false);
    expect(c.supports("convert:gif", "png")).toBe(false);
    expect(c.supports("convert:heic", "png")).toBe(false);
  });
});

describe("supports() truth table", () => {
  it("never converts a format to itself, including the jpeg alias", () => {
    setImageCaps(FULL);
    for (const ext of ["png", "jpg", "jpeg", "webp", "bmp", "ico", "avif"]) {
      const self = ext === "jpeg" ? "jpg" : ext;
      expect(c.supports(`convert:${self}`, ext), `${ext}->${self}`).toBe(false);
    }
    expect(c.supports("convert:png", "jpg")).toBe(true);
    expect(c.supports("convert:jpg", "png")).toBe(true);
    expect(c.supports("convert:jpg", "JPEG")).toBe(false);
    expect(c.supports("convert:png", ".jpeg")).toBe(true);
  });

  it("png/jpg/bmp/ico/pdf are always offered; webp and avif only when encodable", () => {
    setImageCaps(PLAIN);
    expect(["png", "jpg", "bmp", "ico", "pdf"].every((t) => c.supports(`convert:${t}`, "gif"))).toBe(true);
    expect(c.supports("convert:webp", "png")).toBe(false);
    expect(c.supports("convert:avif", "png")).toBe(false);
    setImageCaps(caps({ webp: true }));
    expect(c.supports("convert:webp", "png")).toBe(true);
    expect(c.supports("convert:avif", "png")).toBe(false);
    setImageCaps(caps({ avif: true }));
    expect(c.supports("convert:avif", "png")).toBe(true);
  });

  it("AVIF input needs decode support; HEIC and TIFF inputs are never accepted", () => {
    setImageCaps(PLAIN);
    expect(c.supports("convert:png", "avif")).toBe(false);
    expect(c.supports("img.resize", "avif")).toBe(false);
    setImageCaps(caps({}, true));
    expect(c.supports("convert:png", "avif")).toBe(true);
    expect(c.supports("img.resize", "avif")).toBe(true);
    setImageCaps(FULL);
    for (const ext of ["heic", "heif", "tiff", "tif", "pdf", "txt", "docx", ""]) {
      expect(c.supports("convert:png", ext), ext).toBe(false);
      expect(c.supports("img.compress", ext), ext).toBe(false);
      expect(c.supports("img.to-pdf", ext), ext).toBe(false);
    }
  });

  it("tools claim exactly their inputs", () => {
    setImageCaps(FULL);
    const exts = ["png", "jpg", "jpeg", "webp", "gif", "bmp", "ico", "svg", "avif"];
    const claim = (op: string) => exts.filter((e) => c.supports(op, e));
    expect(claim("img.compress")).toEqual(["png", "jpg", "jpeg", "webp", "gif", "bmp", "svg", "avif"]); // not ico
    expect(claim("img.resize")).toEqual(exts);
    expect(claim("img.crop")).toEqual(exts);
    expect(claim("img.to-pdf")).toEqual(exts);
    expect(claim("img.strip-gps")).toEqual(["png", "jpg", "jpeg", "webp"]);
    expect(claim("img.metadata")).toEqual(["png", "jpg", "jpeg", "webp"]);
  });

  it("convert:pdf is offered for every decodable image but not for itself", () => {
    setImageCaps(PLAIN);
    expect(c.supports("convert:pdf", "png")).toBe(true);
    expect(c.supports("convert:pdf", "svg")).toBe(true);
    expect(c.supports("convert:pdf", "pdf")).toBe(false);
  });
});

describe("runtime()", () => {
  it("SVG input always runs on the main thread", () => {
    vi.stubGlobal("OffscreenCanvas", class {});
    expect(c.runtime("convert:png", "svg")).toBe("main");
    expect(c.runtime("img.resize", "svg")).toBe("main");
    expect(c.runtime("convert:png", "png")).toBe("worker");
    expect(c.runtime("img.to-pdf", "jpg")).toBe("worker");
  });

  it("engines without OffscreenCanvas run everything on the main thread", () => {
    vi.stubGlobal("OffscreenCanvas", undefined);
    expect(c.runtime("convert:png", "png")).toBe("main");
  });
});

describe("options()", () => {
  it("img.compress offers webp as an output format only when it can be encoded", () => {
    setImageCaps(PLAIN);
    const fmt = (o: readonly ToolOption[]) => o.find((x) => x.key === "format") as Extract<ToolOption, { kind: "choice" }>;
    expect(fmt(c.options("img.compress")).choices).toEqual(["same", "jpg"]);
    setImageCaps(caps({ webp: true }));
    expect(fmt(c.options("img.compress")).choices).toEqual(["same", "jpg", "webp"]);
    expect(fmt(c.options("img.compress")).default).toBe("same");
  });

  it("defaults are inside their own ranges", () => {
    for (const op of c.operations) {
      for (const o of c.options(op)) {
        if (o.kind === "range" || o.kind === "number") {
          if (o.min !== undefined) expect(o.default).toBeGreaterThanOrEqual(o.min);
          if (o.max !== undefined) expect(o.default).toBeLessThanOrEqual(o.max);
        }
        if (o.kind === "choice") expect(o.choices).toContain(o.default);
      }
    }
    const resize = c.options("img.resize");
    expect(resize.map((o) => o.key)).toEqual(["width", "height", "keepRatio"]);
    expect(c.options("img.crop")[0]).toMatchObject({ key: "ratio", default: "1:1" });
    expect(c.options("img.metadata")[0]).toMatchObject({ key: "action", default: "view", choices: ["view", "remove"] });
    expect(c.options("img.to-pdf")[0]).toMatchObject({ key: "page", default: "fit", choices: ["fit", "a4"] });
    expect(c.options("img.compress").map((o) => o.key)).toEqual(["quality", "targetKb", "format"]);
  });
});

describe("capability detection", () => {
  it("combines probes; png/jpg are always true", async () => {
    const r = await detectImageCapsWith({
      encodes: async (m) => m === "image/webp",
      decodesAvif: async () => true,
    });
    expect(r).toEqual({ encode: { png: true, jpg: true, webp: true, avif: false }, decodeAvif: true });
  });

  it("a probe that throws counts as 'not supported'", async () => {
    const r = await detectImageCapsWith({
      encodes: async () => {
        throw new Error("boom");
      },
      decodesAvif: () => Promise.reject(new Error("boom")),
    });
    expect(r).toEqual({ encode: { png: true, jpg: true, webp: false, avif: false }, decodeAvif: false });
  });

  it("ensureImageCaps is idempotent, publishes to the store and never throws without a DOM", async () => {
    const a = ensureImageCaps();
    const b = ensureImageCaps();
    expect(a).toBe(b);
    const caps = await a;
    expect(caps.encode.png && caps.encode.jpg).toBe(true);
    expect(getImageCaps()).toBe(caps);
    expect(caps.encode.webp).toBe(false); // Node has no canvas: honest "no"
  });
});

describe("i18n messages", () => {
  const dir = fileURLToPath(new URL("../../..", import.meta.url));
  const fragment = appMessages as unknown as Record<"en" | "ar", Record<string, Record<string, unknown>>>;
  const keysOf = (o: unknown, p = ""): string[] =>
    Object.entries(o as Record<string, unknown>).flatMap(([k, v]) => (typeof v === "object" && v ? keysOf(v, `${p}${k}.`) : [`${p}${k}`]));

  it("en and ar have identical key trees and no empty strings", () => {
    expect(keysOf(fragment.ar).sort()).toEqual(keysOf(fragment.en).sort());
    for (const lang of ["en", "ar"] as const) for (const k of keysOf(fragment[lang])) expect(k.length).toBeGreaterThan(0);
    const flat = (l: "en" | "ar") => JSON.stringify(fragment[l]);
    expect(flat("en")).not.toContain('""');
    expect(flat("ar")).not.toContain('""');
  });

  it("labels every tool, option key, word-like choice and error code used by the converter", () => {
    const ops = fragment.en.ops as Record<string, string>;
    for (const id of IMAGE_TOOLS) expect(ops[id.replace(/[.-]/g, "_")], id).toBeTruthy();
    setImageCaps(FULL);
    for (const op of c.operations) {
      for (const o of c.options(op)) {
        expect((fragment.en.opt as Record<string, string>)[o.key], `opt.${o.key}`).toBeTruthy();
        if (o.kind === "choice") {
          for (const v of o.choices) {
            if (/^[a-z]+$/.test(v) && !/^(jpg|webp|a4)$/.test(v)) {
              expect(((fragment.en.choice as Record<string, Record<string, string>>)[o.key] ?? {})[v], `choice.${o.key}.${v}`).toBeTruthy();
            }
          }
        }
      }
    }
    const sources = ["converters/images.impl.ts", "converters/images.ts", "lib/canvas.ts", "lib/strip-meta.ts", "lib/image-pdf.ts"];
    const codes = new Set<string>();
    for (const f of sources) {
      const text = readFileSync(join(dir, "src/engine", f), "utf8");
      for (const m of text.matchAll(/new ConvertError\("([A-Za-z]+)"/g)) codes.add(m[1]);
    }
    expect(codes.size).toBeGreaterThan(5);
    for (const code of codes) expect((fragment.en.err as Record<string, string>)[code], `err.${code}`).toBeTruthy();
  });
});
