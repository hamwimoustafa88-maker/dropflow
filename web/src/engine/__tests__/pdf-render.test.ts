// Orchestration tests for the pdf.js-based operations. pdf.js itself and the DOM canvas are replaced by small fakes so the
// logic around them (page selection, naming, scale budget, progress, cancellation, error mapping, PDF rebuild) runs in
// Node; the real pixels / glyphs are covered by the lead's headless-browser e2e.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { PDFDocument } from "pdf-lib";
import { ConvertError, isAbort, type Job } from "../types";
import type { TextItemLike } from "../lib/pdf-text";
import { TINY_JPEG } from "./pdf-test-utils";


class PasswordException extends Error {}
class RenderingCancelledException extends Error {}

interface FakePage {
  /** Displayed size in PDF points (already includes /Rotate). */
  width: number;
  height: number;
  items?: Array<Partial<TextItemLike>>;
}

interface Spy {
  rendered: Array<{ page: number; w: number; h: number; scale: number; background?: string }>;
  destroyed: number;
  cleaned: number;
  canvases: Array<{ width: number; height: number }>;
  blobCalls: Array<{ type: string; quality?: number }>;
  blobType?: string; // force a wrong blob type
  hangRender?: boolean;
}

let spy: Spy;
let fake: { pages: FakePage[]; failWith?: "password" | "corrupt" };
/** The on-device OCR used for scanned PDFs (Tesseract is covered by ai-tools.test.ts and the e2e): what it "reads" per page. */
let ocr: { text: string; calls: number };

vi.mock("../lib/ocr-engine", () => ({
  recognize: async (_img: Blob, lang: string, onProgress: (p: number) => void) => {
    ocr.calls++;
    onProgress(1);
    return lang === "auto" ? ocr.text : "";
  },
  hasRealText: (t: string) => /[\p{L}\p{N}]/u.test(t),
}));

vi.mock("../lib/pdfjs-setup", () => ({
  pdfjsDocParams: (data: Uint8Array) => ({ data }),
  loadPdfjs: async () => ({
    PasswordException,
    RenderingCancelledException,
    getDocument: () => {
      const doc = {
        numPages: fake.pages.length,
        getPage: async (n: number) => {
          const fp = fake.pages[n - 1];
          return {
            getViewport: ({ scale }: { scale: number }) => ({ width: fp.width * scale, height: fp.height * scale, scale }),
            render: ({ canvas, viewport, background }: { canvas: { width: number; height: number }; viewport: { width: number; height: number; scale: number }; background?: string }) => {
              spy.rendered.push({ page: n, w: canvas.width, h: canvas.height, scale: viewport.scale, background });
              let cancel: () => void = () => undefined;
              const promise = new Promise<void>((resolve, reject) => {
                cancel = () => reject(new RenderingCancelledException("cancelled"));
                if (!spy.hangRender) resolve();
              });
              return { promise, cancel: () => cancel() };
            },
            cleanup: () => {
              spy.cleaned++;
            },
            getTextContent: async () => ({ items: fp.items ?? [] }),
          };
        },
      };
      return {
        promise:
          fake.failWith === "password"
            ? Promise.reject(new PasswordException("password"))
            : fake.failWith === "corrupt"
              ? Promise.reject(new Error("Invalid PDF structure"))
              : Promise.resolve(doc),
        destroy: async () => {
          spy.destroyed++;
        },
      };
    },
  }),
}));

function installDocument(): void {
  vi.stubGlobal("document", {
    createElement: () => {
      const canvas = {
        width: 300,
        height: 150,
        toBlob(cb: (b: Blob | null) => void, type: string, quality?: number) {
          spy.blobCalls.push({ type, quality });
          cb(new Blob([type === "image/jpeg" ? TINY_JPEG : new Uint8Array([137, 80, 78, 71])], { type: spy.blobType ?? type }));
        },
      };
      spy.canvases.push(canvas);
      return canvas;
    },
  });
}

beforeEach(() => {
  spy = { rendered: [], destroyed: 0, cleaned: 0, canvases: [], blobCalls: [] };
  fake = { pages: [] };
  ocr = { text: "", calls: 0 };
  installDocument();
});
afterEach(() => {
  vi.unstubAllGlobals();
});

function pdfFile(size = 5000, name = "in.pdf"): File {
  return new File([new Uint8Array(size).fill(37)], name, { type: "application/pdf" });
}
function job(operation: string, options: Job["options"] = {}, file = pdfFile()): Job {
  return { id: "t", operation, files: [file], options };
}
const noop: (fraction: number) => void = () => undefined;
const run = async (j: Job, onProgress: (fraction: number) => void = noop, signal: AbortSignal = new AbortController().signal) => (await import("../converters/pdf.render.impl")).runPdfRenderOp(j, onProgress, signal);
async function codeOf(p: Promise<unknown>): Promise<string> {
  try {
    await p;
  } catch (e) {
    return e instanceof ConvertError ? e.code : `other:${String(e)}`;
  }
  return "no-error";
}

const t = (str: string, x: number, y: number, size = 12, dir = "ltr", width?: number): Partial<TextItemLike> => ({
  str,
  dir,
  transform: [size, 0, 0, size, x, y],
  width: width ?? str.length * size * 0.5,
  height: size,
});

describe("pages -> images", () => {
  it("convert:png renders every page at scale 2 on a white background, one file per page", async () => {
    fake.pages = [
      { width: 300, height: 400 },
      { width: 400, height: 300 },
      { width: 300, height: 400 },
    ];
    const seen: number[] = [];
    const out = await run(job("convert:png"), (f) => seen.push(f));
    expect(out.map((f) => f.name)).toEqual(["page-1.png", "page-2.png", "page-3.png"]);
    expect(out.every((f) => f.type === "image/png")).toBe(true);
    expect(spy.rendered.map((r) => [r.w, r.h])).toEqual([
      [600, 800],
      [800, 600],
      [600, 800],
    ]);
    expect(spy.rendered.every((r) => r.background === "rgb(255,255,255)")).toBe(true);
    expect(seen[seen.length - 1]).toBe(1);
    expect(spy.cleaned).toBe(3);
    expect(spy.destroyed).toBe(1);
    expect(spy.canvases[0].width).toBe(0); // canvas freed
    expect(spy.canvases[0].height).toBe(0);
  });

  it("pdf.to-images honours format, scale and the page spec; jpg uses quality 0.92", async () => {
    fake.pages = Array.from({ length: 5 }, () => ({ width: 200, height: 100 }));
    const out = await run(job("pdf.to-images", { format: "jpg", scale: "1.5", pages: "2-3, 5" }));
    expect(out.map((f) => f.name)).toEqual(["page-2.jpg", "page-3.jpg", "page-5.jpg"]);
    expect(out.every((f) => f.type === "image/jpeg")).toBe(true);
    expect(spy.rendered.map((r) => r.page)).toEqual([2, 3, 5]);
    expect(spy.rendered[0].w).toBe(300);
    expect(spy.blobCalls.every((c) => c.type === "image/jpeg" && c.quality === 0.92)).toBe(true);
  });

  it("convert:jpg is jpg at scale 2", async () => {
    fake.pages = [{ width: 100, height: 100 }];
    const out = await run(job("convert:jpg"));
    expect(out[0].name).toBe("page-1.jpg");
    expect(spy.rendered[0].w).toBe(200);
  });

  it("reduces the scale automatically for huge pages (canvas budget)", async () => {
    fake.pages = [{ width: 20000, height: 20000 }];
    await run(job("pdf.to-images", { format: "png", scale: "3", pages: "" }));
    const r = spy.rendered[0];
    expect(Math.max(r.w, r.h)).toBeLessThanOrEqual(8192);
    expect(r.w * r.h).toBeLessThanOrEqual(64_000_000);
    expect(r.scale).toBeLessThan(3);
  });

  it("rejects bad page specs and unsupported encoders", async () => {
    fake.pages = [{ width: 100, height: 100 }];
    expect(await codeOf(run(job("pdf.to-images", { format: "png", scale: "1", pages: "4" })))).toBe("invalidRange");
    expect(spy.destroyed).toBe(1);
    spy.blobType = "image/png"; // browser ignored the jpeg request
    expect(await codeOf(run(job("convert:jpg")))).toBe("encodeUnsupported");
  });

  it("maps pdf.js failures: password -> encrypted, parse error -> corrupt, no pages -> emptyPdf", async () => {
    fake.failWith = "password";
    expect(await codeOf(run(job("convert:png")))).toBe("encrypted");
    fake.failWith = "corrupt";
    expect(await codeOf(run(job("convert:png")))).toBe("corrupt");
    fake = { pages: [] };
    expect(await codeOf(run(job("convert:png")))).toBe("emptyPdf");
    expect(spy.destroyed).toBe(3); // the loading task is always destroyed
  });

  it("cancels: a pre-aborted signal, and an abort while a page is rendering", async () => {
    fake.pages = [{ width: 100, height: 100 }, { width: 100, height: 100 }];
    const pre = new AbortController();
    pre.abort();
    await expect(run(job("convert:png"), noop, pre.signal)).rejects.toSatisfy(isAbort);

    spy.hangRender = true; // render never finishes on its own
    const ac = new AbortController();
    const p = run(job("convert:png"), noop, ac.signal);
    setTimeout(() => ac.abort(), 20);
    await expect(p).rejects.toSatisfy(isAbort);
    expect(spy.destroyed).toBeGreaterThanOrEqual(1);
    expect(spy.canvases[spy.canvases.length - 1].width).toBe(0);
  });
});

describe("pdf -> text / markdown", () => {
  const pageOne = [
    t("Report title", 40, 760, 24),
    t("First paragraph line one", 40, 720, 11),
    t("continues here", 40, 707, 11),
    t("• bullet", 40, 660, 11),
    t("مرحبا", 300, 620, 11, "rtl", 30),
    t("بالعالم", 250, 620, 11, "rtl", 35),
  ];

  it("convert:txt gives plain UTF-8 text with Arabic intact", async () => {
    fake.pages = [{ width: 400, height: 800, items: pageOne }, { width: 400, height: 800, items: [t("Second page", 40, 700, 11)] }];
    const [file] = await run(job("convert:txt"));
    expect(file.name).toBe("text.txt");
    expect(file.type).toBe("text/plain;charset=utf-8");
    const text = await file.text();
    expect(text).toContain("Report title\n");
    expect(text).toContain("مرحبا بالعالم");
    expect(text.trim().endsWith("Second page")).toBe(true);
  });

  it("convert:md and pdf.extract-text(format md) produce Markdown headings and lists", async () => {
    fake.pages = [{ width: 400, height: 800, items: pageOne }];
    const [a] = await run(job("convert:md"));
    expect(a.name).toBe("text.md");
    expect(a.type).toBe("text/markdown;charset=utf-8");
    const md = await a.text();
    expect(md.startsWith("# Report title")).toBe(true);
    expect(md).toContain("- bullet");
    const [b] = await run(job("pdf.extract-text", { format: "md" }));
    expect(await b.text()).toBe(md);
    const [c] = await run(job("pdf.extract-text", { format: "txt" }));
    expect(c.name).toBe("text.txt");
  });

  it("a PDF without any text layer is read with OCR (Arabic + English), page by page", async () => {
    fake.pages = [{ width: 400, height: 800, items: [] }, { width: 400, height: 800, items: [t("   ", 10, 10)] }];
    ocr.text = "‏مرحبا‎\n";
    const [f] = await run(job("convert:txt"));
    expect(await f.text()).toBe("‏مرحبا‎\n\n‏مرحبا‎\n");
    expect(ocr.calls).toBe(2);
    expect(spy.rendered.map((r) => r.page)).toEqual([1, 2]);
    expect(spy.destroyed).toBe(1);
  });

  it("a PDF without any text, even for OCR, fails with noText", async () => {
    fake.pages = [{ width: 400, height: 800, items: [] }];
    ocr.text = "‏ \n";
    expect(await codeOf(run(job("convert:txt")))).toBe("noText");
    expect(spy.destroyed).toBe(1);
  });

  it("ignores marked-content entries and reports progress per page", async () => {
    fake.pages = [{ width: 400, height: 800, items: [{ type: "beginMarkedContent" } as never, t("ok", 10, 700)] }];
    const seen: number[] = [];
    const [f] = await run(job("convert:txt"), (x) => seen.push(x));
    expect(await f.text()).toBe("ok\n");
    expect(seen).toEqual([1]);
  });
});

describe("pdf.compress (render-to-JPEG rebuild)", () => {
  it("rebuilds the PDF from JPEG pages at the original page size", async () => {
    fake.pages = [
      { width: 595, height: 842 },
      { width: 842, height: 595 }, // a landscape / rotated page: displayed size is what we keep
    ];
    const big = pdfFile(200_000);
    const [out] = await run(job("pdf.compress", { quality: 40, scale: "1" }, big));
    expect(out.name).toBe("compressed.pdf");
    expect(out.type).toBe("application/pdf");
    expect(out.size).toBeLessThan(big.size);
    const doc = await PDFDocument.load(new Uint8Array(await out.arrayBuffer()), { updateMetadata: false });
    expect(doc.getPageCount()).toBe(2);
    expect(doc.getPage(0).getSize()).toEqual({ width: 595, height: 842 });
    expect(doc.getPage(1).getSize()).toEqual({ width: 842, height: 595 });
    expect(spy.blobCalls.every((c) => c.type === "image/jpeg" && Math.abs((c.quality ?? 0) - 0.4) < 1e-9)).toBe(true);
    expect(spy.rendered[0].scale).toBe(1);
  });

  it("fails with notSmaller when the rebuilt file is not smaller than the input", async () => {
    fake.pages = [{ width: 100, height: 100 }];
    expect(await codeOf(run(job("pdf.compress", { quality: 60, scale: "1.5" }, pdfFile(200))))).toBe("notSmaller");
  });

  it("clamps the quality and passes the scale through the canvas budget", async () => {
    fake.pages = [{ width: 100, height: 100 }];
    await run(job("pdf.compress", { quality: 500, scale: "2" }, pdfFile(50_000)));
    expect(spy.blobCalls[0].quality).toBe(0.95);
    await run(job("pdf.compress", { quality: 1, scale: "2" }, pdfFile(50_000)));
    expect(spy.blobCalls[1].quality).toBe(0.1);
  });
});

describe("dispatch", () => {
  it("unknown operations are reported, not guessed", async () => {
    expect(await codeOf(run(job("pdf.merge")))).toBe("unsupported");
  });
});
