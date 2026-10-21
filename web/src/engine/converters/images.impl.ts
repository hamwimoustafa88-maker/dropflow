// Heavy half of the images converter: canvas work, EXIF/strip, BMP/ICO, pdf-lib. Loaded lazily by images.ts on first
// run; runs inside the DropFlow Web Worker (OffscreenCanvas) or on the main thread (SVG input, old engines).

import { getImageCaps } from "../caps";
import { encodeBmp24 } from "../lib/bmp";
import { encodeIco, icoSizesFor } from "../lib/ico";
import { canvasToBlob, decodeImage, drawScaled, get2d, makeCanvas, rgbaOf, type AnyCanvas } from "../lib/canvas";
import { parseExif } from "../lib/exif";
import {
  extForMime,
  imageDimensions,
  mimeForExt,
  sameFormatForCompress,
  sameFormatForEdit,
  sniffKind,
  type EncodeFormat,
  type MetaKind,
} from "../lib/image-formats";
import { containRect, cropRect, parseRatio, resizeDims, searchQuality } from "../lib/image-math";
import { buildImagePdf, type PdfPage } from "../lib/image-pdf";
import { formatReport } from "../lib/image-report";
import { findMetadata, needsReencode, readPngText, stripLossless } from "../lib/strip-meta";
import { extensionOf, normalizeExt, stemOf } from "../names";
import { ConvertError, throwIfAborted, type Job, type OptionValue, type ProgressFn } from "../types";

const DEFAULT_QUALITY = 0.92;
const STRIP_REENCODE_QUALITY = 0.95;

const str = (v: OptionValue | undefined, d: string): string => (typeof v === "string" ? v : d);
const num = (v: OptionValue | undefined, d: number): number => (typeof v === "number" && Number.isFinite(v) ? v : d);
const bool = (v: OptionValue | undefined, d: boolean): boolean => (typeof v === "boolean" ? v : d);

function effectiveExt(file: File): string {
  const e = extensionOf(file.name);
  if (e) return e;
  return normalizeExt(extForMime(file.type) ?? "");
}

function asFile(data: BlobPart, name: string, type: string): File {
  return new File([data], name, { type });
}

async function bytesOf(blob: Blob): Promise<Uint8Array> {
  return new Uint8Array(await blob.arrayBuffer());
}

export async function runImages(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
  const op = job.operation;
  const opts = job.options;
  if (job.files.length === 0) throw new ConvertError("decodeFailed");

  if (op === "img.to-pdf") {
    const page: PdfPage = str(opts.page, "fit") === "a4" ? "a4" : "fit";
    return [await toPdf(job.files, page, signal, onProgress)];
  }

  const out: File[] = [];
  const n = job.files.length;
  for (let i = 0; i < n; i++) {
    throwIfAborted(signal);
    const file = job.files[i];
    const ext = effectiveExt(file);
    if (op.startsWith("convert:")) out.push(...(await convertOne(normalizeExt(op.slice(8)), file, ext, signal)));
    else if (op === "img.compress") out.push(...(await compressOne(file, ext, job, signal)));
    else if (op === "img.resize") out.push(...(await resizeOne(file, ext, job, signal)));
    else if (op === "img.crop") out.push(...(await cropOne(file, ext, job, signal)));
    else if (op === "img.strip-gps") out.push(...(await stripOne(file, signal)));
    else if (op === "img.metadata") {
      if (str(opts.action, "view") === "remove") out.push(...(await stripOne(file, signal)));
      else out.push(await viewMetadata(file, ext));
    } else throw new ConvertError("decodeFailed");
    onProgress((i + 1) / n);
  }
  return out;
}

// ---- convert --------------------------------------------------------------------------------------------------------

async function convertOne(target: string, file: File, ext: string, signal: AbortSignal): Promise<File[]> {
  const stem = stemOf(file.name);
  if (target === "pdf") return [await toPdf([file], "fit", signal)];

  const dec = await decodeImage(file, ext);
  try {
    throwIfAborted(signal);
    if (target === "png" || target === "jpg" || target === "webp" || target === "avif") {
      const canvas = drawScaled(dec, dec.width, dec.height, { flatten: target === "jpg" });
      const mime = mimeForExt(target)!;
      const blob = await canvasToBlob(canvas, mime, target === "png" ? undefined : DEFAULT_QUALITY);
      return [asFile(blob, `${stem}.${target}`, mime)];
    }
    if (target === "bmp") {
      const canvas = drawScaled(dec, dec.width, dec.height);
      return [asFile(encodeBmp24(rgbaOf(canvas), canvas.width, canvas.height) as BlobPart, `${stem}.bmp`, "image/bmp")];
    }
    if (target === "gif" || target === "tiff") {
      const canvas = drawScaled(dec, dec.width, dec.height);
      const rgba = rgbaOf(canvas);
      const bytes =
        target === "gif"
          ? (await import("../lib/gif")).encodeGif(rgba, canvas.width, canvas.height)
          : (await import("../lib/tiff")).encodeTiff(rgba, canvas.width, canvas.height);
      return [asFile(bytes as BlobPart, `${stem}.${target}`, target === "gif" ? "image/gif" : "image/tiff")];
    }
    if (target === "ico") {
      const entries = [];
      for (const size of icoSizesFor(dec.width, dec.height)) {
        throwIfAborted(signal);
        const r = containRect(dec.width, dec.height, size);
        const inner = drawScaled(dec, r.width, r.height);
        const square = makeCanvas(size, size); // transparent
        get2d(square).drawImage(inner, r.x, r.y);
        entries.push({ size, png: await bytesOf(await canvasToBlob(square, "image/png")) });
      }
      return [asFile(encodeIco(entries) as BlobPart, `${stem}.ico`, "image/x-icon")];
    }
    throw new ConvertError("encodeUnsupported", { type: target });
  } finally {
    dec.close();
  }
}

// ---- compress / resize / crop ---------------------------------------------------------------------------------------

async function compressOne(file: File, ext: string, job: Job, signal: AbortSignal): Promise<File[]> {
  const quality = Math.min(100, Math.max(10, Math.round(num(job.options.quality, 75))));
  const targetKb = Math.max(0, num(job.options.targetKb, 0));
  const choice = str(job.options.format, "same");
  const caps = getImageCaps();
  const fmt: EncodeFormat = choice === "jpg" ? "jpg" : choice === "webp" ? "webp" : sameFormatForCompress(ext, caps);
  const mime = mimeForExt(fmt)!;

  const dec = await decodeImage(file, ext);
  try {
    throwIfAborted(signal);
    const canvas = drawScaled(dec, dec.width, dec.height, { flatten: fmt === "jpg" });
    let blob: Blob;
    if (fmt === "png") {
      blob = await canvasToBlob(canvas, mime);
      if (targetKb > 0 && blob.size > targetKb * 1024) throw new ConvertError("targetUnreachable", { kb: targetKb });
    } else if (targetKb > 0) {
      const found = await searchQuality((q) => canvasToBlob(canvas, mime, q / 100), targetKb * 1024, { min: 10, max: quality });
      if (!found) throw new ConvertError("targetUnreachable", { kb: targetKb });
      blob = found.result;
    } else {
      blob = await canvasToBlob(canvas, mime, quality / 100);
    }
    if (normalizeExt(ext) === fmt && blob.size >= file.size) throw new ConvertError("notSmaller");
    return [asFile(blob, `${stemOf(file.name)}.${fmt}`, mime)];
  } finally {
    dec.close();
  }
}

async function resizeOne(file: File, ext: string, job: Job, signal: AbortSignal): Promise<File[]> {
  const w = Math.floor(num(job.options.width, 0));
  const h = Math.floor(num(job.options.height, 0));
  const keep = bool(job.options.keepRatio, true);
  const fmt = sameFormatForEdit(ext, getImageCaps());
  const mime = mimeForExt(fmt)!;
  const dec = await decodeImage(file, ext);
  try {
    throwIfAborted(signal);
    const dims = resizeDims(dec.width, dec.height, w, h, keep);
    if (!dims) throw new ConvertError("invalidSize");
    const canvas = drawScaled(dec, dims.width, dims.height, { flatten: fmt === "jpg" });
    const blob = await canvasToBlob(canvas, mime, fmt === "png" ? undefined : DEFAULT_QUALITY);
    return [asFile(blob, `${stemOf(file.name)}.${fmt}`, mime)];
  } finally {
    dec.close();
  }
}

async function cropOne(file: File, ext: string, job: Job, signal: AbortSignal): Promise<File[]> {
  const ratio = parseRatio(str(job.options.ratio, "1:1"));
  if (!ratio) throw new ConvertError("invalidSize");
  const fmt = sameFormatForEdit(ext, getImageCaps());
  const mime = mimeForExt(fmt)!;
  const dec = await decodeImage(file, ext);
  try {
    throwIfAborted(signal);
    const r = cropRect(dec.width, dec.height, ratio[0], ratio[1]);
    const canvas = drawScaled(dec, r.width, r.height, { flatten: fmt === "jpg", region: r });
    const blob = await canvasToBlob(canvas, mime, fmt === "png" ? undefined : DEFAULT_QUALITY);
    return [asFile(blob, `${stemOf(file.name)}.${fmt}`, mime)];
  } finally {
    dec.close();
  }
}

// ---- metadata ---------------------------------------------------------------------------------------------------------

const KIND_EXT: Record<MetaKind, string> = { jpeg: "jpg", png: "png", webp: "webp" };

async function stripOne(file: File, signal: AbortSignal): Promise<File[]> {
  const bytes = await bytesOf(file);
  const kind = sniffKind(bytes);
  if (!kind) throw new ConvertError("corrupt");
  const ext = KIND_EXT[kind];
  const mime = mimeForExt(ext)!;
  throwIfAborted(signal);

  let out: Uint8Array;
  if (needsReencode(bytes)) {
    // Dropping the Orientation tag would turn the photo sideways, and a second embedded JPEG (MPO / Ultra HDR) would
    // keep its own metadata: re-encode so the orientation is baked into the pixels and only one clean image remains.
    const dec = await decodeImage(file, ext);
    try {
      const canvas: AnyCanvas = drawScaled(dec, dec.width, dec.height, { flatten: kind === "jpeg" });
      out = await bytesOf(await canvasToBlob(canvas, mime, kind === "png" ? undefined : STRIP_REENCODE_QUALITY));
    } finally {
      dec.close();
    }
  } else {
    out = stripLossless(bytes).bytes;
  }

  // Verify by re-parsing what we produced.
  let residue: string[];
  try {
    residue = findMetadata(out, kind);
  } catch {
    residue = ["unreadable"];
  }
  if (residue.length > 0 || parseExif(out).gps) throw new ConvertError("stripFailed");
  return [asFile(out as BlobPart, `${stemOf(file.name)}.${ext}`, mime)];
}

async function viewMetadata(file: File, ext: string): Promise<File> {
  const bytes = await bytesOf(file);
  const kind = sniffKind(bytes);
  if (!kind) throw new ConvertError("corrupt");
  let hasXmp = false;
  try {
    hasXmp = findMetadata(bytes, kind).includes("xmp");
  } catch {
    /* a structurally odd file still gets a report */
  }
  const dims = imageDimensions(bytes);
  const report = formatReport({
    name: file.name,
    mime: file.type || mimeForExt(ext) || "",
    size: file.size,
    width: dims?.width,
    height: dims?.height,
    exif: parseExif(bytes),
    pngText: kind === "png" ? readPngText(bytes) : [],
    hasXmp,
  });
  return asFile(report, `${stemOf(file.name)}.txt`, "text/plain;charset=utf-8");
}

// ---- pdf ----------------------------------------------------------------------------------------------------------------

async function toPdf(files: File[], page: PdfPage, signal: AbortSignal, onProgress?: ProgressFn): Promise<File> {
  const bytes = await buildImagePdf(
    files,
    page,
    {
      async reencodePng(file, ext) {
        const dec = await decodeImage(file, ext);
        try {
          const canvas = drawScaled(dec, dec.width, dec.height);
          return await bytesOf(await canvasToBlob(canvas, "image/png"));
        } finally {
          dec.close();
        }
      },
    },
    signal,
    (f) => onProgress?.(f),
  );
  return asFile(bytes as BlobPart, `${stemOf(files[0].name)}.pdf`, "application/pdf");
}
