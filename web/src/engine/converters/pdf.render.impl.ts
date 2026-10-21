// PDF operations that need pdf.js. They run on the MAIN thread: pdf.js parses in its own worker, and only the canvas
// painting happens here. This is the ONLY module that imports pdf.js (through lib/pdfjs-setup, dynamically).

import type { PDFDocumentLoadingTask, PDFDocumentProxy, PDFPageProxy } from "pdfjs-dist";
import { ConvertError, throwIfAborted, type Job, type ProgressFn } from "../types";
import { fitRenderScale } from "../lib/pdf-canvas";
import { loadPdfjs, pdfjsDocParams, type PdfjsModule } from "../lib/pdfjs-setup";
import { hasNoText, itemsToLines, linesToMarkdown, linesToText, type TextLine } from "../lib/pdf-text";
import { parsePageSpec } from "../lib/ranges";

interface OpenPdf {
  pdfjs: PdfjsModule;
  doc: PDFDocumentProxy;
  task: PDFDocumentLoadingTask;
}

function abortError(): DOMException {
  return new DOMException("Cancelled", "AbortError");
}

async function openPdf(file: File, signal: AbortSignal): Promise<OpenPdf> {
  throwIfAborted(signal);
  const pdfjs = await loadPdfjs();
  const bytes = new Uint8Array(await file.arrayBuffer());
  throwIfAborted(signal);
  const task = pdfjs.getDocument(pdfjsDocParams(bytes));
  try {
    const doc = await task.promise;
    if (doc.numPages < 1) throw new ConvertError("emptyPdf");
    return { pdfjs, doc, task };
  } catch (e) {
    await task.destroy().catch(() => undefined);
    if (e instanceof ConvertError) throw e;
    if (e instanceof pdfjs.PasswordException) throw new ConvertError("encrypted");
    throw new ConvertError("corrupt");
  }
}

function closePdf(open: OpenPdf): Promise<void> {
  return open.task.destroy().catch(() => undefined);
}

function makeCanvas(): HTMLCanvasElement {
  return document.createElement("canvas");
}

function freeCanvas(canvas: HTMLCanvasElement): void {
  canvas.width = 0;
  canvas.height = 0;
}

function canvasBlob(canvas: HTMLCanvasElement, type: string, quality?: number): Promise<Blob> {
  return new Promise<Blob>((resolve, reject) => {
    canvas.toBlob(
      (b) => {
        if (!b || b.type !== type) reject(new ConvertError("encodeUnsupported"));
        else resolve(b);
      },
      type,
      quality,
    );
  });
}

/** Paints one page; the scale is reduced automatically if the canvas would exceed the size/pixel budget. */
async function paintPage(open: OpenPdf, page: PDFPageProxy, scale: number, canvas: HTMLCanvasElement, signal: AbortSignal): Promise<void> {
  const base = page.getViewport({ scale: 1 });
  const fit = fitRenderScale({ widthPt: base.width, heightPt: base.height, scale });
  const viewport = page.getViewport({ scale: fit.scale });
  canvas.width = Math.max(1, Math.ceil(viewport.width));
  canvas.height = Math.max(1, Math.ceil(viewport.height));
  const task = page.render({ canvas, viewport, background: "rgb(255,255,255)" });
  const onAbort = () => task.cancel();
  signal.addEventListener("abort", onAbort, { once: true });
  try {
    await task.promise;
  } catch (e) {
    if (signal.aborted || e instanceof open.pdfjs.RenderingCancelledException) throw abortError();
    throw new ConvertError("corrupt", {}, e instanceof Error ? e.message : String(e));
  } finally {
    signal.removeEventListener("abort", onAbort);
  }
}

function num(job: Job, key: string, fallback: number): number {
  const v = Number(job.options[key]);
  return Number.isFinite(v) ? v : fallback;
}
function str(job: Job, key: string, fallback = ""): string {
  const v = job.options[key];
  return typeof v === "string" ? v : v === undefined ? fallback : String(v);
}

// ---------------------------------------------------------------- pdf -> images

export async function renderToImages(
  file: File,
  opts: { format: "png" | "jpg"; scale: number; pages: string },
  onProgress: ProgressFn,
  signal: AbortSignal,
): Promise<File[]> {
  const open = await openPdf(file, signal);
  const canvas = makeCanvas();
  try {
    const indices = parsePageSpec(opts.pages, open.doc.numPages, { allowEmpty: true });
    const mime = opts.format === "jpg" ? "image/jpeg" : "image/png";
    const out: File[] = [];
    for (let k = 0; k < indices.length; k++) {
      throwIfAborted(signal);
      const page = await open.doc.getPage(indices[k] + 1);
      try {
        await paintPage(open, page, opts.scale, canvas, signal);
        const blob = await canvasBlob(canvas, mime, opts.format === "jpg" ? 0.92 : undefined);
        out.push(new File([blob], `page-${indices[k] + 1}.${opts.format}`, { type: mime }));
      } finally {
        page.cleanup();
      }
      onProgress((k + 1) / indices.length);
    }
    return out;
  } finally {
    freeCanvas(canvas);
    await closePdf(open);
  }
}

// ---------------------------------------------------------------- pdf -> text / markdown

export async function extractText(file: File, format: "txt" | "md", onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
  const open = await openPdf(file, signal);
  try {
    const pages: TextLine[][] = [];
    for (let i = 1; i <= open.doc.numPages; i++) {
      throwIfAborted(signal);
      const page = await open.doc.getPage(i);
      try {
        const content = await page.getTextContent();
        pages.push(itemsToLines(content.items as Parameters<typeof itemsToLines>[0]));
      } finally {
        page.cleanup();
      }
      onProgress(i / open.doc.numPages);
    }
    const type = format === "md" ? "text/markdown;charset=utf-8" : "text/plain;charset=utf-8";
    if (hasNoText(pages)) {
      // A scanned PDF (pictures of pages, no text layer): read the pages with on-device OCR instead of giving up.
      const scanned = await ocrPages(open, onProgress, signal);
      if (!scanned) throw new ConvertError("noText");
      return [new File([scanned], `text.${format}`, { type })];
    }
    const text = format === "md" ? linesToMarkdown(pages) : linesToText(pages);
    return [new File([text], `text.${format}`, { type })];
  } finally {
    await closePdf(open);
  }
}

/** Renders every page (about 150 dpi) and recognises it with Tesseract (Arabic + English). "" when nothing was found. */
async function ocrPages(open: OpenPdf, onProgress: ProgressFn, signal: AbortSignal): Promise<string> {
  const { recognize, hasRealText } = await import("../lib/ocr-engine");
  const canvas = makeCanvas();
  const n = open.doc.numPages;
  const texts: string[] = [];
  try {
    for (let i = 1; i <= n; i++) {
      throwIfAborted(signal);
      const page = await open.doc.getPage(i);
      try {
        await paintPage(open, page, 2, canvas, signal);
      } finally {
        page.cleanup();
      }
      const png = await canvasBlob(canvas, "image/png");
      const text = await recognize(png, "auto", (p) => onProgress((i - 1 + p) / n), signal);
      texts.push(text.trim());
      onProgress(i / n);
    }
  } finally {
    freeCanvas(canvas);
  }
  if (!texts.some(hasRealText)) return "";
  return texts.join("\n\n") + "\n";
}

// ---------------------------------------------------------------- compress (render-to-JPEG rebuild)

export async function compressByRendering(
  file: File,
  opts: { quality: number; scale: number },
  onProgress: ProgressFn,
  signal: AbortSignal,
): Promise<File[]> {
  const open = await openPdf(file, signal);
  const canvas = makeCanvas();
  try {
    const { PDFDocument } = await import("pdf-lib");
    const out = await PDFDocument.create({ updateMetadata: false });
    const total = open.doc.numPages;
    for (let i = 1; i <= total; i++) {
      throwIfAborted(signal);
      const page = await open.doc.getPage(i);
      try {
        await paintPage(open, page, opts.scale, canvas, signal);
        const blob = await canvasBlob(canvas, "image/jpeg", Math.min(0.95, Math.max(0.1, opts.quality / 100)));
        const image = await out.embedJpg(new Uint8Array(await blob.arrayBuffer()));
        // The page keeps its ORIGINAL size in points (getViewport already accounts for /Rotate).
        const size = page.getViewport({ scale: 1 });
        const p = out.addPage([size.width, size.height]);
        p.drawImage(image, { x: 0, y: 0, width: size.width, height: size.height });
      } finally {
        page.cleanup();
      }
      onProgress(i / total);
    }
    const bytes = await out.save();
    if (bytes.length >= file.size) throw new ConvertError("notSmaller");
    return [new File([bytes as Uint8Array<ArrayBuffer>], "compressed.pdf", { type: "application/pdf" })];
  } finally {
    freeCanvas(canvas);
    await closePdf(open);
  }
}

// ---------------------------------------------------------------- Converter adapter

export async function runPdfRenderOp(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
  const file = job.files[0];
  switch (job.operation) {
    case "convert:png":
      return renderToImages(file, { format: "png", scale: 2, pages: "" }, onProgress, signal);
    case "convert:jpg":
      return renderToImages(file, { format: "jpg", scale: 2, pages: "" }, onProgress, signal);
    case "pdf.to-images":
      return renderToImages(
        file,
        { format: str(job, "format", "png") === "jpg" ? "jpg" : "png", scale: num(job, "scale", 2), pages: str(job, "pages") },
        onProgress,
        signal,
      );
    case "convert:txt":
      return extractText(file, "txt", onProgress, signal);
    case "convert:md":
      return extractText(file, "md", onProgress, signal);
    case "pdf.extract-text":
      return extractText(file, str(job, "format", "txt") === "md" ? "md" : "txt", onProgress, signal);
    case "pdf.compress":
      return compressByRendering(file, { quality: num(job, "quality", 60), scale: num(job, "scale", 1.5) }, onProgress, signal);
    default:
      throw new ConvertError("unsupported", { operation: job.operation });
  }
}
