// SPDX-License-Identifier: GPL-3.0-or-later
// E-books in the browser (catalog category `epub`): EPUB, FB2, HTMLZ and TXTZ to each other and to HTML, Markdown and plain text, all in
// our own code (lib/markup, lib/book-*, lib/epub, lib/fb2, lib/zipped-books) ported from the Windows app's Books module. MOBI/AZW3
// input and PDF, Word, ODT and RTF output stay with the Windows app. Light manifest: the work loads from ./books.impl.

import { normalizeExt } from "../names";
import type { Converter, Job, ProgressFn } from "../types";

export const BOOK_INPUTS: readonly string[] = ["epub", "fb2", "htmlz", "txtz"];
export const BOOK_TARGETS: readonly string[] = ["epub", "fb2", "htmlz", "txtz", "html", "md", "txt"];
const MAX_BYTES = 300 * 1024 * 1024;

export const booksConverter: Converter = {
  id: "books",
  operations: BOOK_TARGETS.map((t) => `convert:${t}`),
  supports(operation, ext) {
    if (!operation.startsWith("convert:")) return false;
    const target = operation.slice("convert:".length);
    const input = normalizeExt(ext);
    return BOOK_TARGETS.includes(target) && BOOK_INPUTS.includes(input) && target !== input;
  },
  options: () => [],
  runtime: () => "worker",
  suffix: () => "converted",
  maxBytes: () => MAX_BYTES,
  async run(job: Job, onProgress: ProgressFn, signal: AbortSignal) {
    const impl = await import("./books.impl");
    return impl.runBooks(job, onProgress, signal);
  },
};
