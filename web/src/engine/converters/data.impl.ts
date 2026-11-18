// SPDX-License-Identifier: GPL-3.0-or-later
// Implementation of the data conversions (see data.ts). Pure text work; XML input needs DOMParser (main thread).

import { readICal, readVCard, writeICal, writeVCard } from "../lib/data-cards";
import {
  parseDelimited,
  parseJson,
  parseJsonLines,
  records,
  tableToTree,
  toTable,
  writeDelimited,
  writeJson,
  writeJsonLines,
  writeXml,
  xmlToTree,
  type DNode,
  type Table,
  type XmlDocumentLike,
} from "../lib/data-tree";
import { decodeText } from "../lib/text-decode";
import { extensionOf, stemOf } from "../names";
import { ConvertError, throwIfAborted, type Job, type ProgressFn } from "../types";

const TYPES: Record<string, string> = {
  json: "application/json;charset=utf-8",
  jsonl: "application/x-ndjson;charset=utf-8",
  xml: "application/xml;charset=utf-8",
  csv: "text/csv;charset=utf-8",
  tsv: "text/tab-separated-values;charset=utf-8",
  vcf: "text/vcard;charset=utf-8",
  ics: "text/calendar;charset=utf-8",
};

/** What reads XML text into a DOM. The browser's DOMParser by default; tests pass their own. */
export type XmlParse = (text: string) => XmlDocumentLike;

const browserXml: XmlParse = (text) => {
  if (typeof DOMParser === "undefined") throw new ConvertError("unsupported");
  return new DOMParser().parseFromString(text, "application/xml") as unknown as XmlDocumentLike;
};

interface Doc {
  tree: DNode;
  table: Table | null;
}

/** Reads one data file (canonical extension). Exposed for tests. */
export function readData(text: string, ext: string, xml: XmlParse = browserXml): Doc {
  switch (ext) {
    case "json":
      return { tree: parseJson(text), table: null };
    case "jsonl":
      return { tree: parseJsonLines(text), table: null };
    case "xml":
      // A DTD is refused before parsing, so no entity is ever expanded (the DOM check repeats it).
      if (DOCTYPE_IN_PROLOG.test(text)) throw new ConvertError("dataInvalid", { format: "XML" });
      return { tree: xmlToTree(xml(text)), table: null };
    case "csv":
    case "tsv": {
      const table = parseDelimited(text, ext === "tsv" ? "\t" : null);
      return { tree: tableToTree(table), table };
    }
    case "vcf": {
      const table = readVCard(text);
      return { tree: tableToTree(table), table };
    }
    case "ics": {
      const table = readICal(text);
      return { tree: tableToTree(table), table };
    }
    default:
      throw new ConvertError("unsupported");
  }
}

function tableOf(doc: Doc): Table {
  const t = doc.table ?? toTable(doc.tree);
  if (!t) throw new ConvertError("notTable");
  return t;
}

const BOM = String.fromCharCode(0xfeff);

// A DOCTYPE can only come before the root element (after the declaration, comments and processing instructions).
const DOCTYPE_IN_PROLOG = /^\s*(?:<\?[\s\S]*?\?>\s*|<!--[\s\S]*?-->\s*)*<!DOCTYPE/i;

/** The output text and extension for one conversion. Exposed for tests. */
export async function convertData(
  text: string,
  ext: string,
  operation: string,
  options: { xml?: XmlParse; now?: Date } = {},
): Promise<{ text: string; ext: string }> {
  const doc = readData(text, ext, options.xml);
  if (operation === "data.to-vcard") {
    const out = writeVCard(tableOf(doc));
    if (out.written === 0) throw new ConvertError("noContacts");
    return { text: out.text, ext: "vcf" };
  }
  if (operation === "data.to-ical") {
    const out = await writeICal(tableOf(doc), options.now ?? new Date());
    if (out.written === 0) throw new ConvertError("noEvents");
    return { text: out.text, ext: "ics" };
  }
  const target = operation.slice("convert:".length);
  switch (target) {
    case "json":
      return { text: writeJson(doc.tree), ext: target };
    case "jsonl":
      return { text: writeJsonLines(records(doc.tree)), ext: target };
    case "xml":
      return { text: writeXml(doc.tree), ext: target };
    case "csv":
      return { text: BOM + writeDelimited(tableOf(doc), ","), ext: target }; // UTF-8 with BOM: Excel shows Arabic
    case "tsv":
      return { text: BOM + writeDelimited(tableOf(doc), "\t"), ext: target };
    default:
      throw new ConvertError("unsupported");
  }
}

export async function runData(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
  const file = job.files[0];
  if (!file) throw new ConvertError("noFiles");
  throwIfAborted(signal);
  onProgress(0.1);
  const { text } = decodeText(new Uint8Array(await file.arrayBuffer()));
  throwIfAborted(signal);
  onProgress(0.4);
  const out = await convertData(text, extensionOf(file.name), job.operation);
  throwIfAborted(signal);
  onProgress(1);
  return [new File([out.text], `${stemOf(file.name)}.${out.ext}`, { type: TYPES[out.ext] ?? "text/plain;charset=utf-8" })];
}
