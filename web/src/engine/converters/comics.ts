// SPDX-License-Identifier: GPL-3.0-or-later
// Comic books in the browser (catalog category `comic`): a CBZ (zip of pictures) becomes a PDF with one page per picture or a
// fixed-layout EPUB. PDF -> CBZ belongs to the PDF converter (it draws the pages with pdf.js). CBR (RAR) and CB7 (7z) need the
// Windows app: no RAR or 7z reader is allowed here. Light manifest: the work loads from ./comics.impl.

import { normalizeExt } from "../names";
import type { Converter, Job, ProgressFn } from "../types";

export const COMIC_TARGETS: readonly string[] = ["pdf", "epub"];
const MAX_BYTES = 1024 * 1024 * 1024;

export const comicsConverter: Converter = {
  id: "comics",
  operations: COMIC_TARGETS.map((t) => `convert:${t}`),
  supports(operation, ext) {
    if (!operation.startsWith("convert:")) return false;
    return normalizeExt(ext) === "cbz" && COMIC_TARGETS.includes(operation.slice("convert:".length));
  },
  options: () => [],
  // pdf-lib and the picture re-encoding (OffscreenCanvas) both work in the DropFlow Web Worker.
  runtime: () => "worker",
  suffix: () => "converted",
  maxBytes: () => MAX_BYTES,
  async run(job: Job, onProgress: ProgressFn, signal: AbortSignal) {
    const impl = await import("./comics.impl");
    return impl.runComics(job, onProgress, signal);
  },
};
