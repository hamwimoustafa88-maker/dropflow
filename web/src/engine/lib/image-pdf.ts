// Images -> one PDF (pdf-lib). JPEG and PNG are embedded byte-for-byte (no recompression) when their EXIF orientation is
// normal; everything else (and rotated photos) goes through `reencodePng`, which the caller backs with a canvas.
// Heavy: only imported by images.impl.ts.

import { PDFDocument } from "pdf-lib";
import { ConvertError, throwIfAborted } from "../types";
import { extensionOf } from "../names";
import { hasNonTrivialOrientation } from "./exif";
import { pdfPlacement } from "./image-math";

export type PdfPage = "fit" | "a4";

export interface ImagePdfDeps {
  /** Decodes `file` (orientation applied) and returns a PNG file's bytes. */
  reencodePng(file: File, ext: string): Promise<Uint8Array>;
}

export async function buildImagePdf(
  files: File[],
  page: PdfPage,
  deps: ImagePdfDeps,
  signal: AbortSignal,
  onProgress: (fraction: number) => void = () => {},
): Promise<Uint8Array> {
  if (files.length === 0) throw new ConvertError("decodeFailed");
  const doc = await PDFDocument.create();
  doc.setProducer("DropFlow");
  doc.setCreator("DropFlow");

  for (let i = 0; i < files.length; i++) {
    throwIfAborted(signal);
    const file = files[i];
    const ext = extensionOf(file.name);
    const bytes = new Uint8Array(await file.arrayBuffer());
    let image: Awaited<ReturnType<PDFDocument["embedPng"]>> | null = null;

    const upright = !hasNonTrivialOrientation(bytes);
    if (upright && ext === "jpg") {
      try {
        image = await doc.embedJpg(bytes);
      } catch {
        image = null; // exotic JPEG variant: re-encode below
      }
    } else if (upright && ext === "png") {
      try {
        image = await doc.embedPng(bytes);
      } catch {
        image = null;
      }
    }
    if (!image) {
      const png = await deps.reencodePng(file, ext);
      try {
        image = await doc.embedPng(png);
      } catch {
        throw new ConvertError("decodeFailed");
      }
    }

    const place = pdfPlacement(image.width, image.height, page);
    const pdfPage = doc.addPage([place.pageWidth, place.pageHeight]);
    pdfPage.drawImage(image, { x: place.x, y: place.y, width: place.width, height: place.height });
    onProgress((i + 1) / files.length);
  }
  throwIfAborted(signal);
  return doc.save();
}
