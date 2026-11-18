// SPDX-License-Identifier: GPL-3.0-or-later
// Data formats in the browser, through one tree (./lib/data-tree): JSON, JSON Lines, XML, CSV, TSV, contacts (vCard) and
// calendars (iCalendar) in; JSON, JSON Lines, XML, CSV and TSV out; and tables to contacts / calendars (data.to-vcard,
// data.to-ical). YAML and XLSX are desktop-only (no library is allowed here). Light manifest: the work loads from ./data.impl.

import { normalizeExt } from "../names";
import type { Converter, Job, ProgressFn } from "../types";

export const DATA_READERS: readonly string[] = ["json", "jsonl", "xml", "csv", "tsv", "vcf", "ics"];
export const DATA_WRITERS: readonly string[] = ["json", "jsonl", "xml", "csv", "tsv"];
export const DATA_TOOLS: readonly string[] = ["data.to-vcard", "data.to-ical"];
const TABLE_INPUTS: readonly string[] = ["csv", "tsv"];

/** Same limit as the desktop: larger files do not fit comfortably in memory as a tree. */
export const DATA_MAX_BYTES = 200 * 1024 * 1024;

export const dataConverter: Converter = {
  id: "data",
  operations: [...DATA_WRITERS.map((t) => `convert:${t}`), ...DATA_TOOLS],
  supports(operation, ext) {
    const input = normalizeExt(ext);
    if (DATA_TOOLS.includes(operation)) return TABLE_INPUTS.includes(input);
    if (!operation.startsWith("convert:")) return false;
    const target = operation.slice("convert:".length);
    return DATA_WRITERS.includes(target) && DATA_READERS.includes(input) && target !== input;
  },
  options: () => [],
  // XML is read with the browser's DOMParser, which exists on the main thread only (not in Web Workers).
  runtime: (_operation, ext) => (normalizeExt(ext) === "xml" ? "main" : "worker"),
  suffix: (operation) => (operation === "data.to-vcard" ? "contacts" : operation === "data.to-ical" ? "calendar" : "converted"),
  maxBytes: () => DATA_MAX_BYTES,
  async run(job: Job, onProgress: ProgressFn, signal: AbortSignal) {
    const impl = await import("./data.impl");
    return impl.runData(job, onProgress, signal);
  },
};
