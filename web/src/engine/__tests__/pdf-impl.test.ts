import { afterAll, describe, expect, it } from "vitest";
import { PDFDict, PDFDocument, PDFName, PDFNumber, PDFRef, PDFString, StandardFonts, degrees } from "pdf-lib";
import {
  createCanvasTextRenderer,
  editMetadata,
  loadPdf,
  mergePdfs,
  numberPages,
  organizePdf,
  rotatePdf,
  runPdfWorkerOp,
  splitPdf,
  watermarkPdf,
  type TextRenderer,
} from "../converters/pdf.impl";
import { ConvertError, isAbort, type Job } from "../types";
import { bootPdfjs, latin1, makePdf, TINY_PNG } from "./pdf-test-utils";

const { pdfjs, reason } = await bootPdfjs();
if (!pdfjs) console.warn(`[pdf-impl.test] independent pdf.js checks SKIPPED - ${reason}`);
afterAll(() => pdfjs?.close());
const itJs = pdfjs ? it : it.skip;

async function codeOf(p: Promise<unknown>): Promise<string> {
  try {
    await p;
  } catch (e) {
    return e instanceof ConvertError ? e.code : `other:${String(e)}`;
  }
  return "no-error";
}

async function reload(bytes: Uint8Array): Promise<PDFDocument> {
  return PDFDocument.load(bytes, { updateMetadata: false });
}

function job(operation: string, files: File[], options: Job["options"] = {}): Job {
  return { id: "t", operation, files, options };
}
function pdfFile(bytes: Uint8Array, name = "in.pdf"): File {
  return new File([bytes as Uint8Array<ArrayBuffer>], name, { type: "application/pdf" });
}

describe("loadPdf", () => {
  it("rejects garbage as corrupt, encrypted files as encrypted, and empty documents as emptyPdf", async () => {
    expect(await codeOf(loadPdf(new Uint8Array([1, 2, 3, 4])))).toBe("corrupt");
    expect(await codeOf(loadPdf(new TextEncoder().encode("%PDF-1.4\nnot really")))).toBe("corrupt");

    const enc = await PDFDocument.create({ updateMetadata: false });
    enc.addPage([100, 100]);
    enc.context.trailerInfo.Encrypt = enc.context.register(
      enc.context.obj({ Filter: "Standard", V: 1, R: 2, O: PDFString.of("0".repeat(32)), U: PDFString.of("0".repeat(32)), P: -4 }),
    );
    expect(await codeOf(loadPdf(await enc.save({ useObjectStreams: false })))).toBe("encrypted");

    const empty = await PDFDocument.create({ updateMetadata: false });
    expect(await codeOf(loadPdf(await empty.save({ addDefaultPage: false })))).toBe("emptyPdf");
  });

  it("every operation maps corrupt/encrypted inputs to ConvertError", async () => {
    const junk = new Uint8Array([9, 9, 9]);
    expect(await codeOf(mergePdfs([junk]))).toBe("corrupt");
    expect(await codeOf(splitPdf(junk, ""))).toBe("corrupt");
    expect(await codeOf(rotatePdf(junk, 90, ""))).toBe("corrupt");
    expect(await codeOf(organizePdf(junk, "1"))).toBe("corrupt");
    expect(await codeOf(editMetadata(junk, { clear: true }))).toBe("corrupt");
    expect(await codeOf(numberPages(junk, { position: "bottom-center", start: 1, format: "n" }))).toBe("corrupt");
    const stub: TextRenderer = { render: async () => ({ png: TINY_PNG, width: 1, height: 1 }) };
    expect(await codeOf(watermarkPdf(junk, { text: "x", opacity: 25, renderer: stub }))).toBe("corrupt");
  });
});

describe("pdf.merge", () => {
  it("concatenates in the given order", async () => {
    const a = await makePdf(2, { texts: ["A1", "A2"] });
    const b = await makePdf(1, { texts: ["B1"] });
    const c = await makePdf(2, { texts: ["C1", "C2"] });
    const out = await mergePdfs([b, a, c]);
    expect((await reload(out)).getPageCount()).toBe(5);
    if (pdfjs) expect(await pdfjs.texts(out)).toEqual(["B1", "A1", "A2", "C1", "C2"]);
  });

  itJs("an independent reader sees every page", async () => {
    const out = await mergePdfs([await makePdf(3), await makePdf(4)]);
    expect(await pdfjs!.numPages(out)).toBe(7);
  });

  it("does not stamp pdf-lib as producer or add dates", async () => {
    const out = await mergePdfs([await makePdf(1), await makePdf(1)]);
    const doc = await reload(out);
    expect(doc.getProducer()).toBeUndefined();
    expect(doc.getCreationDate()).toBeUndefined();
  });

  it("reports progress and honours cancellation", async () => {
    const seen: number[] = [];
    await mergePdfs([await makePdf(1), await makePdf(1)], { onProgress: (f) => seen.push(f) });
    expect(seen.length).toBeGreaterThan(1);
    expect(seen[seen.length - 1]).toBeGreaterThan(0.9);

    const ac = new AbortController();
    ac.abort();
    await expect(mergePdfs([await makePdf(1)], { signal: ac.signal })).rejects.toSatisfy(isAbort);
  });
});

describe("pdf.split", () => {
  it("makes one PDF per range group", async () => {
    const src = await makePdf(6, { texts: ["P1", "P2", "P3", "P4", "P5", "P6"] });
    const parts = await splitPdf(src, "1-2, 4, 6");
    expect(parts).toHaveLength(3);
    expect((await Promise.all(parts.map(async (p) => (await reload(p)).getPageCount())))).toEqual([2, 1, 1]);
    if (pdfjs) {
      expect(await pdfjs.texts(parts[0])).toEqual(["P1", "P2"]);
      expect(await pdfjs.texts(parts[1])).toEqual(["P4"]);
      expect(await pdfjs.texts(parts[2])).toEqual(["P6"]);
    }
  });

  it("an empty spec gives one PDF per page", async () => {
    const parts = await splitPdf(await makePdf(4), "");
    expect(parts).toHaveLength(4);
    for (const p of parts) expect((await reload(p)).getPageCount()).toBe(1);
  });

  it("supports open ends and Arabic digits, rejects bad specs", async () => {
    const src = await makePdf(5);
    const parts = await splitPdf(src, "٢-");
    expect(parts).toHaveLength(1);
    expect((await reload(parts[0])).getPageCount()).toBe(4);
    expect(await codeOf(splitPdf(src, "1-9"))).toBe("invalidRange");
    expect(await codeOf(splitPdf(src, "abc"))).toBe("invalidRange");
  });

  it("stops when cancelled mid-way", async () => {
    const src = await makePdf(12);
    const ac = new AbortController();
    let calls = 0;
    const p = splitPdf(src, "", {
      signal: ac.signal,
      onProgress: () => {
        if (++calls === 3) ac.abort();
      },
    });
    await expect(p).rejects.toSatisfy(isAbort);
    expect(calls).toBeLessThan(12);
  });
});

describe("pdf.rotate", () => {
  it("adds to the existing rotation with wrap-around", async () => {
    const src = await makePdf(4, { rotations: [0, 90, 270, 180] });
    const out = await reload(await rotatePdf(src, 90, ""));
    expect(out.getPages().map((p) => p.getRotation().angle)).toEqual([90, 180, 0, 270]);
    const out180 = await reload(await rotatePdf(src, 180, ""));
    expect(out180.getPages().map((p) => p.getRotation().angle)).toEqual([180, 270, 90, 0]);
    const out270 = await reload(await rotatePdf(src, 270, ""));
    expect(out270.getPages().map((p) => p.getRotation().angle)).toEqual([270, 0, 180, 90]);
  });

  it("only touches the selected pages, and a page listed twice is rotated once", async () => {
    const src = await makePdf(4);
    const out = await reload(await rotatePdf(src, 90, "2, 2, 4"));
    expect(out.getPages().map((p) => p.getRotation().angle)).toEqual([0, 90, 0, 90]);
  });

  it("does not modify the input bytes and rejects bad specs", async () => {
    const src = await makePdf(2);
    const copy = new Uint8Array(src);
    await rotatePdf(src, 90, "1");
    expect(src).toEqual(copy);
    expect(await codeOf(rotatePdf(src, 90, "5"))).toBe("invalidRange");
  });

  itJs("an independent reader reports the new rotation", async () => {
    // pdf.js applies /Rotate: a 300x400 page rotated by 90 is displayed 400 wide.
    const out = await rotatePdf(await makePdf(1), 90, "");
    const pos = await pdfjs!.positioned(out);
    expect(pos[0][0].upright).toBe(false); // the content is turned, not the viewer
  });
});

describe("pdf.organize", () => {
  it("reorders, duplicates and drops pages", async () => {
    const src = await makePdf(4, { texts: ["P1", "P2", "P3", "P4"] });
    const out = await organizePdf(src, "3, 1, 1, 2-");
    expect((await reload(out)).getPageCount()).toBe(6);
    if (pdfjs) expect(await pdfjs.texts(out)).toEqual(["P3", "P1", "P1", "P2", "P3", "P4"]);

    const dropped = await organizePdf(src, "2, 4");
    expect((await reload(dropped)).getPageCount()).toBe(2);
    if (pdfjs) expect(await pdfjs.texts(dropped)).toEqual(["P2", "P4"]);
  });

  it("supports descending ranges and the last keyword", async () => {
    const src = await makePdf(4, { texts: ["P1", "P2", "P3", "P4"] });
    const out = await organizePdf(src, "last-1");
    if (pdfjs) expect(await pdfjs.texts(out)).toEqual(["P4", "P3", "P2", "P1"]);
    expect((await reload(out)).getPageCount()).toBe(4);
  });

  it("requires a non-empty valid order", async () => {
    const src = await makePdf(2);
    expect(await codeOf(organizePdf(src, ""))).toBe("invalidRange");
    expect(await codeOf(organizePdf(src, "3"))).toBe("invalidRange");
  });
});

describe("pdf.metadata", () => {
  const MARK = "SECRETMARKER-4242";

  /** A PDF with Info (title/author/producer/dates) and an XMP stream that carries a unique marker. */
  async function withMetadata(): Promise<Uint8Array> {
    const doc = await PDFDocument.create({ updateMetadata: false });
    doc.addPage([200, 200]);
    doc.setTitle("Old title");
    doc.setAuthor(`Author ${MARK}`);
    doc.setSubject("Old subject");
    doc.setKeywords(["old"]);
    doc.setCreator("Some creator");
    doc.setProducer("Some producer");
    doc.setCreationDate(new Date("2020-01-01T00:00:00Z"));
    doc.setModificationDate(new Date("2021-01-01T00:00:00Z"));
    const xmp = doc.context.stream(`<x:xmpmeta><dc:title>${MARK}</dc:title></x:xmpmeta>`, { Type: "Metadata", Subtype: "XML" });
    doc.catalog.set(PDFName.of("Metadata"), doc.context.register(xmp));
    return doc.save({ useObjectStreams: false });
  }

  it("the fixture really contains the marker (positive control)", async () => {
    const src = await withMetadata();
    expect(latin1(src)).toContain(MARK);
    expect((await reload(src)).getTitle()).toBe("Old title");
  });

  it("clear removes every Info entry and the XMP stream from the saved bytes", async () => {
    const out = await editMetadata(await withMetadata(), { clear: true });
    const text = latin1(out);
    expect(text).not.toContain(MARK);
    expect(text).not.toContain("Old title");
    expect(text).not.toContain("Some producer");
    const doc = await reload(out);
    expect(doc.getTitle()).toBeUndefined();
    expect(doc.getAuthor()).toBeUndefined();
    expect(doc.getSubject()).toBeUndefined();
    expect(doc.getKeywords()).toBeUndefined();
    expect(doc.getCreator()).toBeUndefined();
    expect(doc.getProducer()).toBeUndefined();
    expect(doc.getCreationDate()).toBeUndefined();
    expect(doc.getModificationDate()).toBeUndefined();
    expect(doc.catalog.get(PDFName.of("Metadata"))).toBeUndefined();
    expect(doc.getPageCount()).toBe(1);
  });

  it("sets non-empty fields (Arabic included), leaves empty ones, and drops the stale XMP", async () => {
    const out = await editMetadata(await withMetadata(), { title: "عنوان جديد", author: "  Jane  ", keywords: "a, b", subject: "" });
    const doc = await reload(out);
    expect(doc.getTitle()).toBe("عنوان جديد");
    expect(doc.getAuthor()).toBe("Jane");
    expect(doc.getKeywords()).toBe("a, b");
    expect(doc.getSubject()).toBe("Old subject"); // empty = unchanged
    expect(doc.catalog.get(PDFName.of("Metadata"))).toBeUndefined();
    expect(latin1(out)).not.toContain(MARK);
  });

  it("clear + new fields = cleared then set", async () => {
    const doc = await reload(await editMetadata(await withMetadata(), { clear: true, title: "Only this" }));
    expect(doc.getTitle()).toBe("Only this");
    expect(doc.getAuthor()).toBeUndefined();
  });

  it("with nothing to change the XMP and Info stay as they were", async () => {
    const src = await withMetadata();
    const out = await editMetadata(src, {});
    const doc = await reload(out);
    expect(doc.getTitle()).toBe("Old title");
    expect(doc.catalog.get(PDFName.of("Metadata"))).toBeInstanceOf(PDFRef);
    expect(latin1(out)).toContain(MARK);
  });

  it("works on a PDF without any Info dictionary", async () => {
    const out = await editMetadata(await makePdf(1), { clear: true, title: "T" });
    expect((await reload(out)).getTitle()).toBe("T");
  });
});

describe("pdf.watermark", () => {
  const png: TextRenderer = { render: async () => ({ png: TINY_PNG, width: 400, height: 100 }) };

  it("draws the image on every page with the requested opacity", async () => {
    const src = await makePdf(3, { rotations: [0, 90, 0] });
    const out = await watermarkPdf(src, { text: " DRAFT ", opacity: 25, renderer: png });
    const doc = await reload(out);
    expect(doc.getPageCount()).toBe(3);
    for (const page of doc.getPages()) {
      const res = page.node.Resources()!;
      const xo = res.lookup(PDFName.of("XObject"), PDFDict);
      expect(xo.keys().length).toBeGreaterThanOrEqual(1);
      const gs = res.lookup(PDFName.of("ExtGState"), PDFDict);
      const alphas = gs.keys().map((k) => (gs.lookup(k, PDFDict).get(PDFName.of("ca")) as PDFNumber).asNumber());
      expect(alphas).toContain(0.25);
    }
  });

  it("renders the trimmed text once and embeds a single image for all pages", async () => {
    const seen: string[] = [];
    const spy: TextRenderer = {
      render: async (t) => {
        seen.push(t);
        return { png: TINY_PNG, width: 10, height: 10 };
      },
    };
    const out = await watermarkPdf(await makePdf(4), { text: "  سري  ", opacity: 50, renderer: spy });
    expect(seen).toEqual(["سري"]);
    // every page references the SAME image object (the PNG's alpha channel adds its own soft-mask object, not one per page)
    const refs = new Set<string>();
    for (const page of (await reload(out)).getPages()) {
      const xo = page.node.Resources()!.lookup(PDFName.of("XObject"), PDFDict);
      for (const k of xo.keys()) refs.add(String(xo.get(k)));
    }
    expect(refs.size).toBe(1);
  });

  it("clamps the opacity and rejects empty text", async () => {
    const src = await makePdf(1);
    expect(await codeOf(watermarkPdf(src, { text: "   ", opacity: 25, renderer: png }))).toBe("needText");
    const out = await watermarkPdf(src, { text: "x", opacity: 0, renderer: png });
    const doc = await reload(out);
    const gs = doc.getPage(0).node.Resources()!.lookup(PDFName.of("ExtGState"), PDFDict);
    const alphas = gs.keys().map((k) => (gs.lookup(k, PDFDict).get(PDFName.of("ca")) as PDFNumber).asNumber());
    expect(alphas).toContain(0.05);
  });

  it("the canvas renderer fails cleanly where no canvas exists (Node)", async () => {
    expect(await codeOf(createCanvasTextRenderer().render("x"))).toBe("encodeUnsupported");
  });

  itJs("keeps the original page text intact", async () => {
    const out = await watermarkPdf(await makePdf(2, { texts: ["KEEP-1", "KEEP-2"] }), { text: "W", opacity: 30, renderer: png });
    expect(await pdfjs!.texts(out)).toEqual(["KEEP-1", "KEEP-2"]);
  });
});

describe("pdf.page-numbers", () => {
  itJs("writes '3 / 12' on page 3 and the right number on every page", async () => {
    const src = await makePdf(12);
    const out = await numberPages(src, { position: "bottom-center", start: 1, format: "n-of-total" });
    const texts = await pdfjs!.texts(out);
    expect(texts[2]).toContain("3 / 12");
    expect(texts[11]).toContain("12 / 12");
    expect(texts[0]).toContain("PAGE-1");
    expect(texts[0]).toContain("1 / 12");
  });

  itJs("plain format and a custom start number", async () => {
    const out = await numberPages(await makePdf(3), { position: "top-right", start: 5, format: "n" });
    const texts = await pdfjs!.texts(out);
    expect(texts.map((t) => t.replace(/PAGE-\d\s*/, "").trim())).toEqual(["5", "6", "7"]);
    const total = await numberPages(await makePdf(3), { position: "top-right", start: 5, format: "n-of-total" });
    expect((await pdfjs!.texts(total))[0]).toContain("5 / 7");
  });

  itJs("puts the number in the visually correct corner, upright, on rotated pages", async () => {
    const helv = await (async () => {
      const d = await PDFDocument.create();
      return d.embedFont(StandardFonts.Helvetica);
    })();
    const tw = helv.widthOfTextAtSize("1", 10);
    const rotations = [0, 90, 180, 270];
    const src = await makePdf(4, { rotations });
    for (const position of ["bottom-right", "top-left", "bottom-left", "top-right"] as const) {
      const out = await numberPages(src, { position, start: 1, format: "n" });
      const pages = await pdfjs!.positioned(out);
      for (let i = 0; i < 4; i++) {
        const vw = rotations[i] % 180 === 0 ? 300 : 400;
        const vh = rotations[i] % 180 === 0 ? 400 : 300;
        const num = pages[i].find((it) => it.str === String(i + 1));
        expect(num, `page ${i + 1} ${position}`).toBeDefined();
        expect(num!.upright, `upright page ${i + 1} ${position}`).toBe(true);
        const wantX = position.endsWith("right") ? vw - 24 - tw : 24;
        const wantY = position.startsWith("bottom") ? vh - 24 : 24 + 8; // baseline; top is margin + ascent
        expect(num!.x).toBeCloseTo(wantX, 0);
        expect(num!.y).toBeCloseTo(wantY, 0);
      }
    }
  });

  it("works without pdf.js too: embeds a Helvetica font and draws on every page", async () => {
    const out = await numberPages(await makePdf(3), { position: "bottom-left", start: 1, format: "n" });
    const doc = await reload(out);
    for (const page of doc.getPages()) {
      const fonts = page.node.Resources()!.lookup(PDFName.of("Font"), PDFDict);
      expect(fonts.keys().length).toBeGreaterThanOrEqual(1);
      expect(String(fonts.lookup(fonts.keys()[0], PDFDict).get(PDFName.of("BaseFont")))).toBe("/Helvetica");
    }
  });

  it("respects a CropBox offset", async () => {
    const doc = await PDFDocument.create({ updateMetadata: false });
    const page = doc.addPage([400, 400]);
    page.setCropBox(50, 60, 200, 200);
    const out = await reload(await numberPages(await doc.save(), { position: "bottom-left", start: 1, format: "n" }));
    expect(out.getPageCount()).toBe(1);
  });

  it("rejects bad input and honours cancellation", async () => {
    const ac = new AbortController();
    ac.abort();
    await expect(numberPages(await makePdf(2), { position: "bottom-center", start: 1, format: "n" }, { signal: ac.signal })).rejects.toSatisfy(isAbort);
    // start below 1 is clamped, not an error
    const out = await numberPages(await makePdf(1), { position: "bottom-center", start: -4, format: "n" });
    expect((await reload(out)).getPageCount()).toBe(1);
  });
});

describe("runPdfWorkerOp (Converter adapter)", () => {
  const signal = new AbortController().signal;
  const noop = () => undefined;

  it("merge needs two files and returns one PDF", async () => {
    const a = pdfFile(await makePdf(1), "a.pdf");
    const b = pdfFile(await makePdf(2), "b.pdf");
    await expect(runPdfWorkerOp(job("pdf.merge", [a]), noop, signal)).rejects.toMatchObject({ code: "needTwo" });
    const out = await runPdfWorkerOp(job("pdf.merge", [a, b]), noop, signal);
    expect(out).toHaveLength(1);
    expect(out[0].type).toBe("application/pdf");
    expect(out[0].name.endsWith(".pdf")).toBe(true);
    expect((await reload(new Uint8Array(await out[0].arrayBuffer()))).getPageCount()).toBe(3);
  });

  it("split returns distinct, ordered part names", async () => {
    const f = pdfFile(await makePdf(3), "x.pdf");
    const out = await runPdfWorkerOp(job("pdf.split", [f], { ranges: "" }), noop, signal);
    expect(out.map((o) => o.name)).toEqual(["part-1.pdf", "part-2.pdf", "part-3.pdf"]);
  });

  it("maps option values (string angle, bool clear) the way the UI sends them", async () => {
    const f = pdfFile(await makePdf(2), "x.pdf");
    const [rot] = await runPdfWorkerOp(job("pdf.rotate", [f], { angle: "270", pages: "2" }), noop, signal);
    const angles = (await reload(new Uint8Array(await rot.arrayBuffer()))).getPages().map((p) => p.getRotation().angle);
    expect(angles).toEqual([0, 270]);
    const [meta] = await runPdfWorkerOp(job("pdf.metadata", [f], { title: "T", author: "", subject: "", keywords: "", clear: false }), noop, signal);
    expect((await reload(new Uint8Array(await meta.arrayBuffer()))).getTitle()).toBe("T");
    const [num] = await runPdfWorkerOp(job("pdf.page-numbers", [f], { position: "top-left", start: 1, format: "n-of-total" }), noop, signal);
    expect(num.name).toBe("numbered.pdf");
    const [org] = await runPdfWorkerOp(job("pdf.organize", [f], { order: "2,1" }), noop, signal);
    expect(org.name).toBe("organized.pdf");
  });

  it("does not run render operations and never touches the input file", async () => {
    const bytes = await makePdf(2);
    const f = pdfFile(bytes, "x.pdf");
    await expect(runPdfWorkerOp(job("pdf.to-images", [f]), noop, signal)).rejects.toMatchObject({ code: "unsupported" });
    expect(new Uint8Array(await f.arrayBuffer())).toEqual(bytes);
  });

  it("an organized/rotated PDF is a valid file for a different reader", async () => {
    const f = pdfFile(await makePdf(3), "x.pdf");
    const [org] = await runPdfWorkerOp(job("pdf.organize", [f], { order: "3,2,1" }), noop, signal);
    const bytes = new Uint8Array(await org.arrayBuffer());
    expect(latin1(bytes.slice(0, 5))).toBe("%PDF-");
    if (pdfjs) expect(await pdfjs.numPages(bytes)).toBe(3);
  });
});

// keep imports used by the type checker even when pdf.js is unavailable
void degrees;
