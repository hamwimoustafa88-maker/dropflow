// PDF operations built on pdf-lib. Runs inside the DropFlow Web Worker (worker-safe: no DOM). All functions take and
// return bytes so they can be tested in Node; `runPdfWorkerOp` adapts them to the Converter contract.

import { PDFDict, PDFDocument, PDFName, PDFRef, StandardFonts, degrees, rgb, EncryptedPDFError } from "pdf-lib";
import { ConvertError, throwIfAborted, type Job, type ProgressFn } from "../types";
import { pageNumberPlacement, watermarkPlacement, type PageNumberPosition } from "../lib/pdf-layout";
import { parsePageSpec, parseSplitGroups } from "../lib/ranges";

export interface RunHooks {
  signal?: AbortSignal;
  onProgress?: ProgressFn;
}

const PDF_TYPE = "application/pdf";

function tick(h: RunHooks | undefined, fraction: number): void {
  if (h?.signal) throwIfAborted(h.signal);
  h?.onProgress?.(Math.min(1, Math.max(0, fraction)));
}

/** Loads a PDF; encrypted -> "encrypted", anything unreadable -> "corrupt", zero pages -> "emptyPdf". */
export async function loadPdf(bytes: Uint8Array): Promise<PDFDocument> {
  let doc: PDFDocument;
  try {
    doc = await PDFDocument.load(bytes, { updateMetadata: false });
    // A header without a catalog loads fine but blows up on first use (TypeError): treat it as corrupt too.
    if (doc.getPageCount() === 0) throw new ConvertError("emptyPdf");
  } catch (e) {
    if (e instanceof ConvertError) throw e;
    // pdf-lib is compiled to ES5, where `instanceof EncryptedPDFError` is unreliable for Error subclasses: also match the message.
    if (e instanceof EncryptedPDFError || (e instanceof Error && /is encrypted/i.test(e.message))) throw new ConvertError("encrypted");
    throw new ConvertError("corrupt");
  }
  return doc;
}

function newDoc(): Promise<PDFDocument> {
  return PDFDocument.create({ updateMetadata: false });
}

function allIndices(n: number): number[] {
  return Array.from({ length: n }, (_, i) => i);
}

// ---------------------------------------------------------------- merge / split / rotate / organize

export async function mergePdfs(sources: Uint8Array[], hooks?: RunHooks): Promise<Uint8Array> {
  const out = await newDoc();
  for (let i = 0; i < sources.length; i++) {
    tick(hooks, i / sources.length);
    const src = await loadPdf(sources[i]);
    const pages = await out.copyPages(src, allIndices(src.getPageCount()));
    for (const p of pages) out.addPage(p);
  }
  tick(hooks, 0.95);
  return out.save();
}

/** One PDF per group of the spec ("1-3, 5"); empty spec -> one PDF per page. */
export async function splitPdf(bytes: Uint8Array, spec: string, hooks?: RunHooks): Promise<Uint8Array[]> {
  const src = await loadPdf(bytes);
  const groups = parseSplitGroups(spec, src.getPageCount());
  const outputs: Uint8Array[] = [];
  for (let g = 0; g < groups.length; g++) {
    tick(hooks, g / groups.length);
    const doc = await newDoc();
    const pages = await doc.copyPages(src, groups[g]);
    for (const p of pages) doc.addPage(p);
    outputs.push(await doc.save());
  }
  tick(hooks, 1);
  return outputs;
}

/** Adds `angle` (90/180/270, clockwise) to the existing /Rotate of the selected pages (empty spec = all). */
export async function rotatePdf(bytes: Uint8Array, angle: number, spec: string, hooks?: RunHooks): Promise<Uint8Array> {
  const doc = await loadPdf(bytes);
  const indices = [...new Set(parsePageSpec(spec, doc.getPageCount(), { allowEmpty: true }))];
  indices.forEach((i, k) => {
    tick(hooks, k / indices.length);
    const page = doc.getPage(i);
    const next = (((page.getRotation().angle + angle) % 360) + 360) % 360;
    page.setRotation(degrees(next));
  });
  return doc.save();
}

/** New document made of the pages in `order` (0-based; duplicates and descending ranges allowed, omitted pages dropped). */
export async function organizePdf(bytes: Uint8Array, spec: string, hooks?: RunHooks): Promise<Uint8Array> {
  const src = await loadPdf(bytes);
  const order = parsePageSpec(spec, src.getPageCount());
  tick(hooks, 0.1);
  const out = await newDoc();
  const pages = await out.copyPages(src, order);
  for (const p of pages) out.addPage(p);
  tick(hooks, 0.9);
  return out.save();
}

// ---------------------------------------------------------------- metadata

export interface MetadataEdit {
  title?: string;
  author?: string;
  subject?: string;
  keywords?: string;
  /** Remove every Info entry (title, author, creator, producer, dates...) and the XMP metadata stream. */
  clear?: boolean;
}

function dropXmp(doc: PDFDocument): void {
  const key = PDFName.of("Metadata");
  const ref = doc.catalog.get(key);
  doc.catalog.delete(key);
  // Deleting only the catalog entry would leave the XMP stream inside the saved file (pdf-lib writes every object).
  if (ref instanceof PDFRef) doc.context.delete(ref);
}

export async function editMetadata(bytes: Uint8Array, edit: MetadataEdit, hooks?: RunHooks): Promise<Uint8Array> {
  const doc = await loadPdf(bytes);
  tick(hooks, 0.2);
  if (edit.clear) {
    const infoRef = doc.context.trailerInfo.Info;
    const info = infoRef ? doc.context.lookup(infoRef) : undefined;
    if (info instanceof PDFDict) for (const key of info.keys()) info.delete(key);
    dropXmp(doc);
  }
  const title = edit.title?.trim();
  const author = edit.author?.trim();
  const subject = edit.subject?.trim();
  const keywords = edit.keywords?.trim();
  if (title) doc.setTitle(title);
  if (author) doc.setAuthor(author);
  if (subject) doc.setSubject(subject);
  if (keywords) doc.setKeywords([keywords]);
  // An XMP packet that still says the old title would override the new Info entries in some viewers: drop it.
  if (title || author || subject || keywords) dropXmp(doc);
  tick(hooks, 0.8);
  // No object streams: keeps the Info dictionary readable as plain objects, which makes "cleared" verifiable.
  return doc.save({ useObjectStreams: false });
}

// ---------------------------------------------------------------- watermark

export interface RenderedText {
  /** PNG bytes with a transparent background. */
  png: Uint8Array;
  width: number;
  height: number;
}

/** Turns text into a transparent PNG. Injected so tests can stub it; the real one draws on a canvas (Arabic shaping + bidi). */
export interface TextRenderer {
  render(text: string): Promise<RenderedText>;
}

export interface WatermarkOptions {
  text: string;
  /** 5..100 (percent). */
  opacity: number;
  renderer: TextRenderer;
}

export async function watermarkPdf(bytes: Uint8Array, opts: WatermarkOptions, hooks?: RunHooks): Promise<Uint8Array> {
  const text = opts.text.trim();
  if (!text) throw new ConvertError("needText");
  const doc = await loadPdf(bytes);
  const rendered = await opts.renderer.render(text);
  const image = await doc.embedPng(rendered.png);
  const opacity = Math.min(1, Math.max(0.05, opts.opacity / 100));
  const pages = doc.getPages();
  pages.forEach((page, i) => {
    tick(hooks, i / pages.length);
    const p = watermarkPlacement({
      box: page.getCropBox(),
      rotation: page.getRotation().angle,
      imageWidth: rendered.width,
      imageHeight: rendered.height,
    });
    page.drawImage(image, { x: p.x, y: p.y, width: p.width, height: p.height, rotate: degrees(p.angle), opacity });
  });
  return doc.save();
}

/** The real renderer: OffscreenCanvas in a worker (or a DOM canvas when the page is not in a worker). */
export function createCanvasTextRenderer(): TextRenderer {
  return {
    async render(text: string): Promise<RenderedText> {
      const FONT = '"Segoe UI", "Noto Sans Arabic", "Noto Sans", Tahoma, Arial, sans-serif';
      const pad = 24;
      let size = 160;
      const make = (w: number, h: number): OffscreenCanvas | HTMLCanvasElement => {
        if (typeof OffscreenCanvas !== "undefined") return new OffscreenCanvas(w, h);
        if (typeof document !== "undefined") {
          const c = document.createElement("canvas");
          c.width = w;
          c.height = h;
          return c;
        }
        throw new ConvertError("encodeUnsupported");
      };
      const probe = make(8, 8).getContext("2d") as CanvasRenderingContext2D | OffscreenCanvasRenderingContext2D | null;
      if (!probe) throw new ConvertError("encodeUnsupported");
      probe.font = `bold ${size}px ${FONT}`;
      let width = probe.measureText(text).width;
      // Auto-fit very long text: keep the bitmap within ~4096 px wide.
      if (width + pad * 2 > 4096) {
        size = Math.max(24, Math.floor((size * (4096 - pad * 2)) / width));
        probe.font = `bold ${size}px ${FONT}`;
        width = probe.measureText(text).width;
      }
      const w = Math.ceil(width + pad * 2);
      const h = Math.ceil(size * 1.4 + pad * 2);
      const canvas = make(w, h);
      const ctx = canvas.getContext("2d") as CanvasRenderingContext2D | OffscreenCanvasRenderingContext2D | null;
      if (!ctx) throw new ConvertError("encodeUnsupported");
      ctx.clearRect(0, 0, w, h);
      ctx.font = `bold ${size}px ${FONT}`;
      ctx.fillStyle = "#6b6b6b";
      ctx.textBaseline = "middle";
      ctx.textAlign = "left";
      ctx.direction = /[֐-ࣿיִ-﷿ﹰ-﻿]/.test(text) ? "rtl" : "ltr";
      // textAlign "left" is physical; with direction rtl the run still starts at x. Draw from the left padding.
      ctx.fillText(text, pad, h / 2);
      const blob: Blob =
        "convertToBlob" in canvas
          ? await canvas.convertToBlob({ type: "image/png" })
          : await new Promise<Blob>((resolve, reject) =>
              (canvas as HTMLCanvasElement).toBlob((b) => (b ? resolve(b) : reject(new ConvertError("encodeUnsupported"))), "image/png"),
            );
      if (blob.type !== "image/png") throw new ConvertError("encodeUnsupported");
      return { png: new Uint8Array(await blob.arrayBuffer()), width: w, height: h };
    },
  };
}

// ---------------------------------------------------------------- page numbers

export interface PageNumberOptions {
  position: PageNumberPosition;
  start: number;
  format: "n" | "n-of-total";
}

export const PAGE_NUMBER_FONT_SIZE = 10;

export async function numberPages(bytes: Uint8Array, opts: PageNumberOptions, hooks?: RunHooks): Promise<Uint8Array> {
  const doc = await loadPdf(bytes);
  const font = await doc.embedFont(StandardFonts.Helvetica);
  const pages = doc.getPages();
  const start = Math.max(1, Math.floor(opts.start) || 1);
  const last = start + pages.length - 1;
  pages.forEach((page, i) => {
    tick(hooks, i / pages.length);
    const n = start + i;
    const text = opts.format === "n-of-total" ? `${n} / ${last}` : String(n);
    const pl = pageNumberPlacement({
      box: page.getCropBox(),
      rotation: page.getRotation().angle,
      position: opts.position,
      textWidth: font.widthOfTextAtSize(text, PAGE_NUMBER_FONT_SIZE),
      fontSize: PAGE_NUMBER_FONT_SIZE,
    });
    page.drawText(text, { x: pl.x, y: pl.y, size: PAGE_NUMBER_FONT_SIZE, font, color: rgb(0.2, 0.2, 0.25), rotate: degrees(pl.angle) });
  });
  return doc.save();
}

// ---------------------------------------------------------------- Converter adapter

function pdfFile(bytes: Uint8Array, name: string): File {
  return new File([bytes as Uint8Array<ArrayBuffer>], name, { type: PDF_TYPE });
}

async function bytesOf(file: File): Promise<Uint8Array> {
  return new Uint8Array(await file.arrayBuffer());
}

function str(job: Job, key: string, fallback = ""): string {
  const v = job.options[key];
  return typeof v === "string" ? v : v === undefined ? fallback : String(v);
}
function num(job: Job, key: string, fallback: number): number {
  const v = Number(job.options[key]);
  return Number.isFinite(v) ? v : fallback;
}

export async function runPdfWorkerOp(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
  const hooks: RunHooks = { signal, onProgress };
  const first = job.files[0];
  switch (job.operation) {
    case "pdf.merge": {
      if (job.files.length < 2) throw new ConvertError("needTwo");
      const sources: Uint8Array[] = [];
      for (const f of job.files) sources.push(await bytesOf(f));
      return [pdfFile(await mergePdfs(sources, hooks), "merged.pdf")];
    }
    case "pdf.split": {
      const parts = await splitPdf(await bytesOf(first), str(job, "ranges"), hooks);
      return parts.map((b, i) => pdfFile(b, `part-${i + 1}.pdf`));
    }
    case "pdf.rotate": {
      const angle = num(job, "angle", 90);
      return [pdfFile(await rotatePdf(await bytesOf(first), angle, str(job, "pages"), hooks), "rotated.pdf")];
    }
    case "pdf.organize": {
      return [pdfFile(await organizePdf(await bytesOf(first), str(job, "order"), hooks), "organized.pdf")];
    }
    case "pdf.metadata": {
      const out = await editMetadata(
        await bytesOf(first),
        {
          title: str(job, "title"),
          author: str(job, "author"),
          subject: str(job, "subject"),
          keywords: str(job, "keywords"),
          clear: job.options.clear === true,
        },
        hooks,
      );
      return [pdfFile(out, "metadata.pdf")];
    }
    case "pdf.watermark": {
      const out = await watermarkPdf(
        await bytesOf(first),
        { text: str(job, "text"), opacity: num(job, "opacity", 25), renderer: createCanvasTextRenderer() },
        hooks,
      );
      return [pdfFile(out, "watermarked.pdf")];
    }
    case "pdf.page-numbers": {
      const out = await numberPages(
        await bytesOf(first),
        {
          position: str(job, "position", "bottom-center") as PageNumberPosition,
          start: num(job, "start", 1),
          format: str(job, "format", "n") === "n-of-total" ? "n-of-total" : "n",
        },
        hooks,
      );
      return [pdfFile(out, "numbered.pdf")];
    }
    default:
      throw new ConvertError("unsupported", { operation: job.operation });
  }
}
