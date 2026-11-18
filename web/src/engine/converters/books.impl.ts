// SPDX-License-Identifier: GPL-3.0-or-later
// Worker-side e-book conversion (see books.ts), the web twin of BookTools.cs on Windows.

import { ConvertError, throwIfAborted, type Job, type ProgressFn } from "../types";
import { extensionOf, stemOf } from "../names";
import type { Book, Picture } from "../lib/book-model";
import { blocksToMarkdown, blocksToText, htmlDocument, htmlToBlocks, withoutPictures } from "../lib/book-text";
import { readEpub, writeEpub } from "../lib/epub";
import { readFb2, writeFb2 } from "../lib/fb2";
import { readZippedBook, writeHtmlz, writeTxtz } from "../lib/zipped-books";

export const BOOK_TYPES: Record<string, string> = {
  epub: "application/epub+zip",
  fb2: "application/x-fictionbook+xml",
  htmlz: "application/zip",
  txtz: "application/zip",
  html: "text/html;charset=utf-8",
  md: "text/markdown;charset=utf-8",
  txt: "text/plain;charset=utf-8",
};

/** Reads any supported e-book (by its canonical extension) into the shared model. */
export function readBook(bytes: Uint8Array, ext: string, fallbackTitle: string): Book {
  switch (ext) {
    case "epub":
      return readEpub(bytes, fallbackTitle);
    case "fb2":
      return readFb2(bytes, fallbackTitle);
    case "htmlz":
    case "txtz":
      return readZippedBook(bytes, ext, fallbackTitle);
    default:
      throw new ConvertError("unsupported");
  }
}

/** Re-encodes a picture an FB2 reader cannot show (GIF, WebP, BMP...) as PNG; null when this runtime has no canvas. */
export type ToPng = (picture: Picture) => Promise<Uint8Array | null>;

const canvasPng: ToPng = async (picture) => {
  if (typeof OffscreenCanvas === "undefined" || typeof createImageBitmap !== "function") return null;
  try {
    const bitmap = await createImageBitmap(new Blob([picture.bytes as BlobPart], { type: picture.mime }));
    try {
      const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
      canvas.getContext("2d")!.drawImage(bitmap, 0, 0);
      return new Uint8Array(await (await canvas.convertToBlob({ type: "image/png" })).arrayBuffer());
    } finally {
      bitmap.close();
    }
  } catch {
    return null; // not a picture this browser decodes: the FB2 keeps its alt text
  }
};

/** Writes `book` as `target`. Exposed for tests. */
export async function writeBook(book: Book, target: string, toPng: ToPng = canvasPng): Promise<Uint8Array | string> {
  switch (target) {
    case "epub":
      return writeEpub(book);
    case "fb2": {
      // FB2 readers show JPEG and PNG: other pictures are drawn again as PNG. SVG, and what cannot be decoded, keep only their alt text.
      const pictures = await Promise.all(
        book.pictures.map(async (p) => {
          if (p.mime === "image/jpeg" || p.mime === "image/png" || p.mime === "image/svg+xml") return p;
          const png = await toPng(p);
          return png ? { mime: "image/png", bytes: png } : p;
        }),
      );
      return writeFb2({ ...book, pictures });
    }
    case "htmlz":
      return writeHtmlz(book);
    case "txtz":
      return writeTxtz(book);
    case "html":
      return htmlDocument(book);
    case "md":
      return blocksToMarkdown(htmlToBlocks(withoutPictures(book.body)));
    case "txt":
      // A byte order mark, as the Windows app writes: Notepad and old editors then read Arabic correctly.
      return "\ufeff" + blocksToText(htmlToBlocks(withoutPictures(book.body)));
    default:
      throw new ConvertError("unsupported");
  }
}

export async function runBooks(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
  const target = job.operation.slice("convert:".length);
  const file = job.files[0];
  if (!file || !(target in BOOK_TYPES)) throw new ConvertError("unsupported");
  onProgress(0.05);
  const bytes = new Uint8Array(await file.arrayBuffer());
  throwIfAborted(signal);
  const book = readBook(bytes, extensionOf(file.name), stemOf(file.name));
  throwIfAborted(signal);
  onProgress(0.5);
  const out = await writeBook(book, target);
  throwIfAborted(signal);
  onProgress(1);
  return [new File([out as BlobPart], `${stemOf(file.name)}.${target}`, { type: BOOK_TYPES[target] })];
}
