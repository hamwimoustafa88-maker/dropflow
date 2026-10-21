import { copyFileSync, existsSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { afterAll, afterEach, beforeAll, describe, expect, it } from "vitest";
import { LOADED_KEY, aiConverter } from "../converters/ai";
import { ocrScale } from "../converters/ocr.impl";
import { cleanOcrText, closeOcr, hasRealText, langsFor, recognize } from "../lib/ocr-engine";
import { closeRembg, predictMask } from "../lib/rembg-engine";
import { U2NETP, applyAlpha, flattenOnto, toMask, toTensor } from "../lib/segmentation";
import catalog from "@/data/catalog.json";

const here = (p: string) => fileURLToPath(new URL(p, import.meta.url));
const nm = here("../../../node_modules/");

describe("AI manifest (OCR + background removal)", () => {
  const store = new Map<string, string>();
  const fake = {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => void store.set(k, v),
    removeItem: (k: string) => void store.delete(k),
  };
  afterEach(() => {
    store.clear();
    delete (globalThis as { localStorage?: unknown }).localStorage;
  });

  it("image -> TXT is a convert target and Text (OCR) / Remove background are tools, for every decodable picture", () => {
    for (const op of ["convert:txt", "img.ocr", "img.remove-bg"]) {
      for (const ext of ["png", "jpg", "jpeg", "webp", "heic", "tif", "bmp", "gif", "svg"]) expect(aiConverter.supports(op, ext), `${op} ${ext}`).toBe(true);
      for (const ext of ["txt", "pdf", "mp3", ""]) expect(aiConverter.supports(op, ext), `${op} ${ext}`).toBe(false);
    }
    expect(aiConverter.supports("convert:pdf", "png")).toBe(false);
    for (const id of ["img.ocr", "img.remove-bg"]) expect((catalog.tools.find((t) => t.id === id) as { platforms: string[] }).platforms).toContain("web");
  });

  it("OCR runs on the page (Tesseract has its own worker); the cut-out runs in the DropFlow worker", () => {
    (globalThis as { OffscreenCanvas?: unknown }).OffscreenCanvas ??= class {};
    expect(aiConverter.runtime("convert:txt", "png")).toBe("main");
    expect(aiConverter.runtime("img.ocr", "png")).toBe("main");
    expect(aiConverter.runtime("img.remove-bg", "png")).toBe("worker");
    expect(aiConverter.runtime("img.remove-bg", "svg")).toBe("main");
  });

  it("options: language for OCR (auto = Arabic + English), fill for the cut-out; TXT has none", () => {
    expect(aiConverter.options("convert:txt")).toEqual([]);
    expect(aiConverter.options("img.ocr")).toEqual([{ key: "lang", kind: "choice", default: "auto", choices: ["auto", "ara", "eng"] }]);
    expect(aiConverter.options("img.remove-bg")[0]).toMatchObject({ key: "fill", default: "transparent" });
  });

  it("the first-use download note shows until the person has run the tool once; blocked storage keeps showing it", () => {
    expect(aiConverter.warning?.("convert:txt")).toBe("ocrFirstUse"); // no storage at all
    (globalThis as { localStorage?: unknown }).localStorage = fake;
    expect(aiConverter.warning?.("img.ocr")).toBe("ocrFirstUse");
    expect(aiConverter.warning?.("img.remove-bg")).toBe("rembgFirstUse");
    aiConverter.acknowledgeWarning?.("convert:txt");
    expect(store.get(LOADED_KEY.ocr)).toBe("1");
    expect(aiConverter.warning?.("img.ocr")).toBeUndefined();
    expect(aiConverter.warning?.("img.remove-bg")).toBe("rembgFirstUse");
    (globalThis as { localStorage?: unknown }).localStorage = { getItem: () => { throw new Error("blocked"); }, setItem: () => { throw new Error("blocked"); } };
    expect(aiConverter.warning?.("img.ocr")).toBe("ocrFirstUse");
    expect(() => aiConverter.acknowledgeWarning?.("img.ocr")).not.toThrow();
  });
});

describe("OCR text helpers", () => {
  it("language list: English first for auto (verified: it reads digits better), single languages as asked", () => {
    expect(langsFor("auto")).toEqual(["eng", "ara"]);
    expect(langsFor("ara")).toEqual(["ara"]);
    expect(langsFor("eng")).toEqual(["eng"]);
  });

  it("cleans line ends and blank runs but keeps the right-to-left marks around Arabic lines", () => {
    expect(cleanOcrText("a  \r\nb\n\n\n\nc\n")).toBe("a\nb\n\nc\n");
    expect(cleanOcrText("‏مرحبا‎\n")).toBe("‏مرحبا‎\n");
    expect(cleanOcrText("  \n ")).toBe("");
    expect(hasRealText("‏‎ . ,")).toBe(false);
    expect(hasRealText("٣")).toBe(true);
  });

  it("scales small pictures up (2x at most) and huge ones down for recognition", () => {
    expect(ocrScale(760, 220)).toBe(2); // never more than 2x
    expect(ocrScale(1000, 300)).toBe(1);
    expect(ocrScale(2000, 1000)).toBe(1);
    expect(ocrScale(10000, 100)).toBe(0.5);
  });
});

describe("OCR on a real Arabic + English picture (Tesseract.js, the same models the site serves)", () => {
  let langDir = "";
  beforeAll(() => {
    langDir = mkdtempSync(join(tmpdir(), "dropflow-ocr-"));
    copyFileSync(join(nm, "@tesseract.js-data/eng/4.0.0_best_int/eng.traineddata.gz"), join(langDir, "eng.traineddata.gz"));
    copyFileSync(join(nm, "@tesseract.js-data/ara/4.0.0_best_int/ara.traineddata.gz"), join(langDir, "ara.traineddata.gz"));
  });
  afterAll(async () => {
    await closeOcr();
    rmSync(langDir, { recursive: true, force: true });
  });

  it("recognises both lines, Arabic in logical (reading) order", async () => {
    const png = new Uint8Array(readFileSync(here("./fixtures/ocr-ar-en.png")));
    const progress: number[] = [];
    const text = await recognize(png, "auto", (p) => progress.push(p), new AbortController().signal, {
      importer: () => import("tesseract.js"),
      langPath: langDir,
    });
    expect(text).toContain("Hello DropFlow 2026");
    expect(text).toContain("مرحبا");
    expect(text).toContain("بالعالم");
    expect(text.indexOf("مرحبا")).toBeLessThan(text.indexOf("بالعالم"));
    expect(text.endsWith("\n")).toBe(true);
    expect(progress.length).toBeGreaterThan(0);
  }, 120_000);

  it("a cancelled job rejects with AbortError and the next job still works", async () => {
    const png = new Uint8Array(readFileSync(here("./fixtures/ocr-ar-en.png")));
    const config = { importer: () => import("tesseract.js"), langPath: langDir };
    const ac = new AbortController();
    const p = recognize(png, "eng", () => {}, ac.signal, config);
    ac.abort();
    await expect(p).rejects.toMatchObject({ name: "AbortError" });
    const text = await recognize(png, "eng", () => {}, new AbortController().signal, config);
    expect(text).toContain("Hello");
  }, 120_000);
});

describe("background removal maths (port of the desktop SegmentationMath)", () => {
  it("tensor: planar NCHW, divided by the largest value, ImageNet mean/std", () => {
    const n = 2;
    const rgba = new Uint8ClampedArray([200, 100, 50, 255, 0, 0, 0, 255, 100, 100, 100, 255, 200, 200, 200, 0]);
    const t = toTensor(rgba, n);
    expect(t.length).toBe(3 * n * n);
    expect(t[0]).toBeCloseTo((1 - 0.485) / 0.229, 5); // 200/200
    expect(t[4]).toBeCloseTo((0.5 - 0.456) / 0.224, 5); // G of pixel 0 = 100/200, plane 1
    expect(() => toTensor(new Uint8ClampedArray(4), n)).toThrow();
  });

  it("mask: min-max stretched to 0..255; alpha multiplies (transparent stays transparent); flatten blends", () => {
    expect([...toMask([0, 0.5, 1])]).toEqual([0, 128, 255]);
    expect([...toMask([2, 4, 6])]).toEqual([0, 128, 255]);
    expect([...toMask([0.5, 0.5])]).toEqual([128, 128]);
    const rgba = new Uint8ClampedArray([10, 20, 30, 255, 10, 20, 30, 0, 10, 20, 30, 255]);
    applyAlpha(rgba, [255, 255, 0]);
    expect([rgba[3], rgba[7], rgba[11]]).toEqual([255, 0, 0]);
    flattenOnto(rgba, 255, 255, 255);
    expect([...rgba.slice(8, 12)]).toEqual([255, 255, 255, 255]);
    expect([...rgba.slice(0, 4)]).toEqual([10, 20, 30, 255]);
  });
});

const MODEL = here("../../../public/vendor/models/u2netp.onnx");
describe.skipIf(!existsSync(MODEL))("background removal on the real U²-Net-P model (ONNX Runtime Web, wasm)", () => {
  afterAll(() => closeRembg());

  it("keeps a red disc on a white background and drops the background", async () => {
    const n = U2NETP.size;
    const rgba = new Uint8ClampedArray(n * n * 4);
    for (let y = 0; y < n; y++)
      for (let x = 0; x < n; x++) {
        const inside = (x - 160) ** 2 + (y - 160) ** 2 < 80 ** 2;
        rgba.set(inside ? [200, 30, 30, 255] : [255, 255, 255, 255], (y * n + x) * 4);
      }
    const mask = await predictMask(rgba, { importer: () => import("onnxruntime-web/wasm"), model: new Uint8Array(readFileSync(MODEL)) });
    expect(mask.length).toBe(n * n);
    expect(mask[160 * n + 160]).toBeGreaterThan(230);
    expect(mask[5 * n + 5]).toBeLessThan(25);
    expect(mask[5 * n + (n - 5)]).toBeLessThan(25);
  }, 120_000);
});
