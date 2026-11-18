// SPDX-License-Identifier: GPL-3.0-or-later
// Worker-side comic conversion (see comics.ts), the web twin of ComicTools.cs on Windows. JPEG and PNG pages go into the PDF byte for
// byte and JPEG, PNG, GIF and WebP pages into the EPUB as they are; other pictures (and photos turned by an EXIF orientation) are
// drawn on a canvas first.

import { PDFDocument, ReadingDirection, type PDFImage } from "pdf-lib";
import { ConvertError, throwIfAborted, type Job, type ProgressFn } from "../types";
import { extensionOf, stemOf } from "../names";
import { readComic, type Comic, type ComicPage } from "../lib/comic";
import { writePictureBook, type PicturePage } from "../lib/epub";
import { hasNonTrivialOrientation } from "../lib/exif";
import { imageDimensions } from "../lib/image-formats";
import { sniffImage } from "../lib/book-model";

/** A page drawn on a canvas and encoded again (orientation applied). Injected in tests; the default uses lib/canvas. */
export type Reencode = (page: ComicPage, prefer: "png" | "jpeg") => Promise<PicturePage>;

const canvasReencode: Reencode = async (page, prefer) => {
  const { canvasToBlob, decodeImage, drawScaled } = await import("../lib/canvas");
  const dec = await decodeImage(new Blob([page.bytes as BlobPart]), extensionOf(page.name));
  try {
    const canvas = drawScaled(dec, dec.width, dec.height, { flatten: prefer === "jpeg" });
    const mime = prefer === "jpeg" ? "image/jpeg" : "image/png";
    const blob = await canvasToBlob(canvas, mime, prefer === "jpeg" ? 0.92 : undefined);
    return { bytes: new Uint8Array(await blob.arrayBuffer()), mime, width: dec.width, height: dec.height };
  } finally {
    dec.close();
  }
};

function gifSize(b: Uint8Array): { width: number; height: number } | null {
  return b.length >= 10 ? { width: b[6] | (b[7] << 8), height: b[8] | (b[9] << 8) } : null;
}

/** The page as an EPUB core picture type (JPEG, PNG, GIF, WebP) with its size; anything else is re-encoded. */
export async function epubPicture(page: ComicPage, reencode: Reencode): Promise<PicturePage> {
  const mime = sniffImage(page.bytes);
  const upright = mime !== "image/jpeg" || !hasNonTrivialOrientation(page.bytes);
  const size = mime === "image/gif" ? gifSize(page.bytes) : mime && mime !== "image/bmp" ? imageDimensions(page.bytes) : null;
  if (mime && mime !== "image/bmp" && upright && size && size.width > 0 && size.height > 0) return { bytes: page.bytes, mime, ...size };
  return reencode(page, mime === "image/jpeg" ? "jpeg" : "png");
}

export async function comicToPdf(comic: Comic, reencode: Reencode, onProgress: ProgressFn, signal: AbortSignal): Promise<Uint8Array> {
  const doc = await PDFDocument.create();
  doc.setTitle(comic.title);
  if (comic.author) doc.setAuthor(comic.author);
  doc.setProducer("DropFlow");
  doc.setCreator("DropFlow");
  if (comic.language) doc.setLanguage(comic.language);
  if (comic.rtl) doc.catalog.getOrCreateViewerPreferences().setReadingDirection(ReadingDirection.R2L);
  for (let i = 0; i < comic.pages.length; i++) {
    throwIfAborted(signal);
    const page = comic.pages[i];
    const mime = sniffImage(page.bytes);
    let image: PDFImage | null = null;
    try {
      if (mime === "image/jpeg" && !hasNonTrivialOrientation(page.bytes)) image = await doc.embedJpg(page.bytes);
      else if (mime === "image/png") image = await doc.embedPng(page.bytes);
    } catch {
      image = null; // an exotic variant: drawn again below
    }
    if (!image) {
      const again = await reencode(page, mime === "image/jpeg" ? "jpeg" : "png");
      try {
        image = again.mime === "image/jpeg" ? await doc.embedJpg(again.bytes) : await doc.embedPng(again.bytes);
      } catch {
        throw new ConvertError("decodeFailed");
      }
    }
    // One page per picture, sized to it (a pixel is a point, like the images-to-PDF tool).
    const pdfPage = doc.addPage([image.width, image.height]);
    pdfPage.drawImage(image, { x: 0, y: 0, width: image.width, height: image.height });
    onProgress(0.1 + (0.85 * (i + 1)) / comic.pages.length);
  }
  return doc.save();
}

export async function comicToEpub(comic: Comic, reencode: Reencode, onProgress: ProgressFn, signal: AbortSignal): Promise<Uint8Array> {
  const pages: PicturePage[] = [];
  for (let i = 0; i < comic.pages.length; i++) {
    throwIfAborted(signal);
    pages.push(await epubPicture(comic.pages[i], reencode));
    onProgress(0.1 + (0.8 * (i + 1)) / comic.pages.length);
  }
  return writePictureBook({ title: comic.title, author: comic.author, language: comic.language ?? "en", rtl: comic.rtl, pages });
}

export async function runComics(job: Job, onProgress: ProgressFn, signal: AbortSignal, reencode: Reencode = canvasReencode): Promise<File[]> {
  const target = job.operation.slice("convert:".length);
  const file = job.files[0];
  if (!file || (target !== "pdf" && target !== "epub")) throw new ConvertError("unsupported");
  onProgress(0.02);
  const bytes = new Uint8Array(await file.arrayBuffer());
  throwIfAborted(signal);
  const comic = readComic(bytes, stemOf(file.name));
  onProgress(0.1);
  const out = target === "pdf" ? await comicToPdf(comic, reencode, onProgress, signal) : await comicToEpub(comic, reencode, onProgress, signal);
  onProgress(1);
  return [new File([out as BlobPart], `${stemOf(file.name)}.${target}`, { type: target === "pdf" ? "application/pdf" : "application/epub+zip" })];
}
