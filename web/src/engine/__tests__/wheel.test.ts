import { readFileSync } from "node:fs";
import { afterEach, describe, expect, it } from "vitest";
import { DEFAULT_IMAGE_CAPS, setImageCaps, setMediaCaps } from "../caps";
import { CORE_VERSION, buildMediaCaps } from "../lib/ffmpeg-probe";
import { registry } from "../registry";
import {
  MAX_PER_PAGE,
  buildWheels,
  firstNonEmpty,
  pageWedges,
  wheelForKey,
  wheelForModifiers,
  type ActionState,
  type Wedge,
  type WheelSet,
} from "../wheel";

const fx = (name: string) => readFileSync(new URL(`./fixtures/ffmpeg-${name}.txt`, import.meta.url), "utf8");
const NO_ACTIONS: ActionState = { hasResults: false, canShare: false, hasText: false };
const f = (name: string, size = 1000) => ({ name, size });
const ops = (w: readonly Wedge[]) => w.map((x) => x.operation);

afterEach(() => {
  setImageCaps(DEFAULT_IMAGE_CAPS);
  setMediaCaps(null);
});

describe("wheel builder with the real registry", () => {
  it("png: converts to formats the browser can write, never to itself; tools only real ones", () => {
    setImageCaps({ encode: { png: true, jpg: true, webp: true, avif: false }, decodeAvif: false });
    const w = buildWheels([f("photo.png")], registry, NO_ACTIONS);
    expect(ops(w.convert)).toEqual(["convert:jpg", "convert:webp", "convert:tiff", "convert:bmp", "convert:gif", "convert:ico", "convert:pdf", "convert:txt"]);
    expect(w.convert.map((x) => x.label)).toEqual(["JPG", "WEBP", "TIFF", "BMP", "GIF", "ICO", "PDF", "TXT"]);
    // HEIC is never offered (no HEVC encoder in the browser); avif is absent because the (fake) browser cannot encode it;
    // TXT is the web-only OCR target (WEB_EXTRA_OUTPUTS), last
    expect(ops(w.convert)).not.toContain("convert:avif");
    expect(ops(w.convert)).not.toContain("convert:heic");
    expect(ops(w.tools)).toEqual(expect.arrayContaining(["img.compress", "img.resize", "img.crop", "img.strip-gps", "img.metadata", "arc.zip", "gen.hash", "gen.base64"]));
    // multi tool with a single file is hidden (except zip)
    expect(ops(w.tools)).not.toContain("img.to-pdf");
    expect(ops(w.tools)).toContain("arc.zip");
    // the photo and on-device AI tools are real now
    expect(ops(w.tools)).toEqual(expect.arrayContaining(["img.edit", "img.background", "img.redact", "img.icons", "img.qr", "img.remove-bg", "img.ocr"]));
    // never an unimplemented catalog tool
    expect(ops(w.tools)).not.toContain("img.annotate");
    expect(ops(w.tools)).not.toContain("img.redact-auto");
    expect(ops(w.tools)).not.toContain("img.collage"); // multi: needs two pictures
  });

  it("avif appears only when the browser can encode it", () => {
    setImageCaps({ encode: { png: true, jpg: true, webp: true, avif: true }, decodeAvif: true });
    expect(ops(buildWheels([f("a.png")], registry, NO_ACTIONS).convert)).toContain("convert:avif");
    setImageCaps({ encode: { png: true, jpg: true, webp: true, avif: false }, decodeAvif: false });
    expect(ops(buildWheels([f("a.png")], registry, NO_ACTIONS).convert)).not.toContain("convert:avif");
  });

  it("two images: img.to-pdf (multi) appears; mixed categories get no convert wheel", () => {
    const two = buildWheels([f("a.png"), f("b.jpg")], registry, NO_ACTIONS);
    expect(ops(two.tools)).toContain("img.to-pdf");
    const mixed = buildWheels([f("a.png"), f("b.pdf")], registry, NO_ACTIONS);
    expect(mixed.convert).toEqual([]);
    expect(ops(mixed.tools)).toEqual(["arc.zip", "gen.rename", "gen.hash", "gen.base64"]); // rename: 2+ files
  });

  it("pdf: png/jpg/txt/md converts and the real pdf tools, in catalog order", () => {
    const w = buildWheels([f("a.pdf")], registry, NO_ACTIONS);
    expect(ops(w.convert)).toEqual(["convert:jpg", "convert:png", "convert:txt", "convert:md"]);
    const pdfTools = ops(w.tools).filter((o) => o.startsWith("pdf."));
    expect(pdfTools).toEqual(["pdf.compress", "pdf.split", "pdf.organize", "pdf.metadata", "pdf.to-images", "pdf.extract-text", "pdf.rotate", "pdf.watermark", "pdf.page-numbers"]);
    expect(ops(w.tools)).not.toContain("pdf.merge"); // needs 2+
    expect(ops(w.tools)).not.toContain("pdf.protect"); // not possible in the browser
    expect(ops(buildWheels([f("a.pdf"), f("b.pdf")], registry, NO_ACTIONS).tools)).toContain("pdf.merge");
  });

  it("media wheels stay empty until ffmpeg is probed, then show exactly the probed targets", () => {
    const before = buildWheels([f("song.wav")], registry, NO_ACTIONS);
    expect(before.convert).toEqual([]);
    expect(ops(before.tools)).not.toContain("aud.trim");
    setMediaCaps(
      buildMediaCaps(CORE_VERSION, {
        encoders: fx("encoders"),
        decoders: fx("decoders"),
        muxers: fx("muxers"),
        demuxers: fx("demuxers"),
        filters: fx("filters"),
      }),
    );
    const after = buildWheels([f("song.wav")], registry, NO_ACTIONS);
    expect(ops(after.convert)).toEqual(expect.arrayContaining(["convert:mp3", "convert:flac", "convert:ogg", "convert:opus"]));
    expect(ops(after.convert)).not.toContain("convert:wav");
    expect(ops(after.tools)).toEqual(expect.arrayContaining(["aud.normalize", "aud.trim"]));
    const video = buildWheels([f("clip.mp4")], registry, NO_ACTIONS);
    expect(ops(video.convert)).toEqual(expect.arrayContaining(["convert:webm", "convert:gif", "convert:mp3"]));
    expect(ops(video.tools)).toEqual(expect.arrayContaining(["vid.trim", "vid.mute", "vid.snapshot", "vid.speed", "vid.compress"]));
  });

  it("subtitles: srt converts to vtt/txt only", () => {
    const w = buildWheels([f("sub.srt")], registry, NO_ACTIONS);
    expect(ops(w.convert)).toEqual(["convert:vtt", "convert:txt"]);
  });

  it("archives: zip offers extract and no converts", () => {
    const w = buildWheels([f("a.zip")], registry, NO_ACTIONS);
    expect(w.convert).toEqual([]);
    expect(ops(w.tools)).toContain("arc.extract");
  });

  it("unknown extension (docx): no convert wheel, only generic tools", () => {
    const w = buildWheels([f("letter.docx")], registry, NO_ACTIONS);
    expect(w.convert).toEqual([]);
    expect(ops(w.tools)).toEqual(["arc.zip", "gen.hash", "gen.base64"]);
  });

  it("jpeg alias is treated as jpg", () => {
    const w = buildWheels([f("a.JPEG")], registry, NO_ACTIONS);
    expect(ops(w.convert)).toContain("convert:png");
    expect(ops(w.convert)).not.toContain("convert:jpg");
  });

  it("no files -> everything empty", () => {
    const w = buildWheels([], registry, { hasResults: true, canShare: true, hasText: true });
    expect(w).toEqual({ convert: [], tools: [], actions: [] });
  });

  it("marks tools that need options", () => {
    const w = buildWheels([f("a.png")], registry, NO_ACTIONS);
    const by = (id: string) => w.tools.find((x) => x.operation === id)!;
    expect(by("img.compress").needsOptions).toBe(true);
    expect(by("img.strip-gps").needsOptions).toBe(false);
    expect(w.convert.every((x) => !x.needsOptions)).toBe(true);
  });

  it("every wedge id is unique and every tool wedge has a label key", () => {
    setImageCaps({ encode: { png: true, jpg: true, webp: true, avif: true }, decodeAvif: true });
    const w = buildWheels([f("a.png"), f("b.png")], registry, NO_ACTIONS);
    const all = [...w.convert, ...w.tools];
    expect(new Set(all.map((x) => x.id)).size).toBe(all.length);
    expect(w.tools.every((x) => x.labelKey?.startsWith("ops."))).toBe(true);
  });
});

describe("web actions", () => {
  it("offers only what is possible", () => {
    const none = buildWheels([f("a.png")], registry, NO_ACTIONS).actions;
    expect(none).toEqual([]);
    const all = buildWheels([f("a.png")], registry, { hasResults: true, canShare: true, hasText: true }).actions;
    expect(ops(all)).toEqual(["web.download", "web.share", "web.copy"]);
    expect(ops(buildWheels([f("a.png")], registry, { hasResults: true, canShare: false, hasText: false }).actions)).toEqual(["web.download"]);
  });
});

describe("paging", () => {
  const mk = (n: number): Wedge[] =>
    Array.from({ length: n }, (_, i) => ({ id: `t${i}`, wheel: "tools", operation: `t${i}`, labelKey: `ops.t${i}`, needsOptions: false, multi: false }));

  it("keeps up to 8 wedges on one page", () => {
    expect(pageWedges(mk(0))).toEqual([[]]);
    expect(pageWedges(mk(MAX_PER_PAGE))).toHaveLength(1);
    expect(pageWedges(mk(MAX_PER_PAGE))[0]).toHaveLength(MAX_PER_PAGE);
  });

  it("pages longer wheels with a 'more' wedge on every page and loses nothing", () => {
    const pages = pageWedges(mk(13));
    expect(pages.length).toBe(2);
    for (const p of pages) {
      expect(p.length).toBeLessThanOrEqual(MAX_PER_PAGE);
      expect(p[p.length - 1].more).toBe(true);
    }
    const real = pages.flat().filter((w) => !w.more);
    expect(real.map((w) => w.id)).toEqual(mk(13).map((w) => w.id));
  });
});

describe("modifier mapping", () => {
  it("Shift = convert, Ctrl = tools, Alt = actions", () => {
    expect(wheelForModifiers({ shiftKey: true })).toBe("convert");
    expect(wheelForModifiers({ ctrlKey: true })).toBe("tools");
    expect(wheelForModifiers({ altKey: true })).toBe("actions");
    expect(wheelForModifiers({})).toBeNull();
    expect(wheelForKey("Shift")).toBe("convert");
    expect(wheelForKey("Control")).toBe("tools");
    expect(wheelForKey("Alt")).toBe("actions");
    expect(wheelForKey("a")).toBeNull();
  });

  it("falls back to the first non-empty wheel", () => {
    const set: WheelSet = { convert: [], tools: mk1(), actions: [] };
    expect(firstNonEmpty(set, "convert")).toBe("tools");
    expect(firstNonEmpty(set, "tools")).toBe("tools");
    expect(firstNonEmpty({ convert: [], tools: [], actions: [] }, "convert")).toBeNull();
  });
});

function mk1(): Wedge[] {
  return [{ id: "x", wheel: "tools", operation: "x", needsOptions: false, multi: false }];
}
