// SPDX-License-Identifier: GPL-3.0-or-later
// The one tree every data format is read into and written from (mirrors src/DropFlow.Engines.Data on the desktop):
// JSON, JSON Lines, XML, CSV and TSV here; contacts and calendars in ./data-cards. No YAML in the browser (no library).
// Rules for tables and XML are documented in docs/data-formats.md and are the same as on the desktop.

import { ConvertError } from "../types";

export type DValue =
  | { k: "null" }
  | { k: "bool"; v: boolean }
  | { k: "num"; v: string } // the number exactly as written (valid JSON syntax), so 1.10 and 20-digit ids survive
  | { k: "str"; v: string };
export type DObject = { k: "obj"; e: Map<string, DNode> };
export type DArray = { k: "arr"; i: DNode[] };
export type DNode = DValue | DObject | DArray;

export const NULL: DValue = { k: "null" };
export const str = (v: string): DValue => ({ k: "str", v });
export const obj = (entries: [string, DNode][] = []): DObject => ({ k: "obj", e: new Map(entries) });
export const arr = (items: DNode[] = []): DArray => ({ k: "arr", i: items });

export const MAX_DEPTH = 256;
const JSON_NUMBER = /^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]+)?$/;
const PLAIN_NUMBER = /^-?(0|[1-9][0-9]*)(\.[0-9]+)?$/;

export function isValue(n: DNode): n is DValue {
  return n.k !== "obj" && n.k !== "arr";
}

/** Text of a scalar ("" for null). */
export function textOf(v: DValue): string {
  return v.k === "null" ? "" : v.k === "bool" ? String(v.v) : v.v;
}

/** CSV/TSV cells are numbers only when plainly so: no leading zero ("0123" is a code), no plus sign, at most 15 digits. */
export function infer(text: string): DValue {
  if (PLAIN_NUMBER.test(text) && text.replace(/[^0-9]/g, "").length <= 15) return { k: "num", v: text };
  return str(text);
}

function bad(format: string): ConvertError {
  return new ConvertError("dataInvalid", { format });
}

// ---- JSON ----------------------------------------------------------------------------------------------------------------

/** A small JSON reader that keeps numbers as written, keeps key order and refuses nesting deeper than MAX_DEPTH. */
export function parseJson(text: string): DNode {
  let i = 0;
  const n = text.length;
  const ws = () => {
    while (i < n) {
      const c = text.charCodeAt(i);
      if (c === 32 || c === 9 || c === 10 || c === 13) i++;
      else break;
    }
  };
  const fail = (why: string): never => {
    throw new ConvertError("dataInvalid", { format: "JSON" }, `${why} at ${i}`);
  };
  const value = (depth: number): DNode => {
    if (depth > MAX_DEPTH) fail("nested too deeply");
    ws();
    const c = text[i];
    if (c === "{") {
      i++;
      const out = obj();
      ws();
      if (text[i] === "}") {
        i++;
        return out;
      }
      for (;;) {
        ws();
        if (text[i] !== '"') fail("expected a key");
        const key = string();
        ws();
        if (text[i++] !== ":") fail("expected ':'");
        out.e.set(key, value(depth + 1));
        ws();
        const d = text[i++];
        if (d === "}") return out;
        if (d !== ",") fail("expected ',' or '}'");
      }
    }
    if (c === "[") {
      i++;
      const out = arr();
      ws();
      if (text[i] === "]") {
        i++;
        return out;
      }
      for (;;) {
        out.i.push(value(depth + 1));
        ws();
        const d = text[i++];
        if (d === "]") return out;
        if (d !== ",") fail("expected ',' or ']'");
      }
    }
    if (c === '"') return str(string());
    if (text.startsWith("true", i)) return (i += 4), { k: "bool", v: true };
    if (text.startsWith("false", i)) return (i += 5), { k: "bool", v: false };
    if (text.startsWith("null", i)) return (i += 4), NULL;
    const m = /^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]+)?/.exec(text.slice(i, i + 400));
    if (!m || m[0].length === 0) return fail("unexpected character");
    i += m[0].length;
    return { k: "num", v: m[0] };
  };
  const string = (): string => {
    i++; // opening quote
    let out = "";
    let start = i;
    for (;;) {
      if (i >= n) fail("unterminated string");
      const c = text.charCodeAt(i);
      if (c === 34) {
        out += text.slice(start, i);
        i++;
        return out;
      }
      if (c === 92) {
        out += text.slice(start, i);
        const e = text[i + 1];
        const simple: Record<string, string> = { '"': '"', "\\": "\\", "/": "/", b: "\b", f: "\f", n: "\n", r: "\r", t: "\t" };
        if (e in simple) {
          out += simple[e];
          i += 2;
        } else if (e === "u" && /^[0-9a-fA-F]{4}$/.test(text.slice(i + 2, i + 6))) {
          out += String.fromCharCode(parseInt(text.slice(i + 2, i + 6), 16));
          i += 6;
        } else fail("bad escape");
        start = i;
        continue;
      }
      if (c < 32) fail("control character in a string");
      i++;
    }
  };
  if (text.charCodeAt(0) === 0xfeff) i = 1;
  const root = value(0);
  ws();
  if (i < n) fail("text after the end");
  return root;
}

/** JSON Lines: every non-empty line is one value. An error names the line. */
export function parseJsonLines(text: string): DArray {
  const out = arr();
  const lines = text.split("\n");
  for (let l = 0; l < lines.length; l++) {
    const line = lines[l].trim();
    if (!line) continue;
    try {
      out.i.push(parseJson(line));
    } catch (e) {
      if (e instanceof ConvertError) throw new ConvertError("dataInvalidLine", { format: "JSON Lines", line: l + 1 });
      throw e;
    }
  }
  return out;
}

function jsonString(s: string): string {
  // JSON.stringify escapes only what JSON requires (quotes, backslash, control characters); Arabic stays readable.
  return JSON.stringify(s);
}

export function writeJson(node: DNode, indent = 2): string {
  const pad = (d: number) => (indent ? "\n" + " ".repeat(indent * d) : "");
  const sep = indent ? ": " : ":";
  const w = (x: DNode, d: number): string => {
    switch (x.k) {
      case "null":
        return "null";
      case "bool":
        return String(x.v);
      case "num":
        return JSON_NUMBER.test(x.v) ? x.v : jsonString(x.v);
      case "str":
        return jsonString(x.v);
      case "arr":
        if (x.i.length === 0) return "[]";
        return "[" + x.i.map((v) => pad(d + 1) + w(v, d + 1)).join(",") + pad(d) + "]";
      case "obj":
        if (x.e.size === 0) return "{}";
        return "{" + [...x.e].map(([k, v]) => pad(d + 1) + jsonString(k) + sep + w(v, d + 1)).join(",") + pad(d) + "}";
    }
  };
  return w(node, 0) + (indent ? "\n" : "");
}

export function writeJsonLines(records: readonly DNode[]): string {
  return records.map((r) => writeJson(r, 0) + "\n").join("");
}

// ---- tables ------------------------------------------------------------------------------------------------------------

export interface Table {
  columns: string[];
  rows: DValue[][];
}

const MAX_COLUMNS = 16_384;
const MAX_ROWS = 1_048_575;

function unwrap(node: DNode): DNode {
  for (let depth = 0; depth < 64 && node.k === "obj"; depth++) {
    const entries = [...node.e];
    if (entries.length === 1 && !isValue(entries[0][1])) {
      node = entries[0][1];
      continue;
    }
    const arrays = entries.filter(([, v]) => v.k === "arr");
    const rest = entries.filter(([, v]) => v.k !== "arr");
    if (arrays.length === 1 && rest.every(([, v]) => isValue(v)) && (arrays[0][1] as DArray).i.every((x) => x.k === "obj")) {
      node = arrays[0][1];
      continue;
    }
    break;
  }
  return node;
}

/** The records of a tree (JSON Lines): the items of the table array, or the whole value. */
export function records(root: DNode): DNode[] {
  const node = unwrap(root);
  return node.k === "arr" ? node.i : [root];
}

/** Header names made unique and non-empty ("name", "name_2", "column3"). */
export function uniqueNames(names: readonly string[]): string[] {
  const used = new Set<string>();
  return names.map((raw, i) => {
    const name = raw.trim() || `column${i + 1}`;
    let candidate = name;
    for (let n = 2; used.has(candidate); n++) candidate = `${name}_${n}`;
    used.add(candidate);
    return candidate;
  });
}

function flatten(node: DNode, prefix: string | null, cells: [string, DValue][]): void {
  if (node.k === "obj" && node.e.size > 0) {
    for (const [k, v] of node.e) flatten(v, prefix === null ? k : `${prefix}.${k}`, cells);
  } else if (node.k === "arr" && node.i.length > 0) {
    node.i.forEach((v, i) => flatten(v, prefix === null ? String(i) : `${prefix}.${i}`, cells));
  } else if (isValue(node)) {
    cells.push([prefix ?? "value", node]);
  } else {
    cells.push([prefix ?? "value", NULL]); // an empty object or array: an empty cell, the column is kept
  }
}

function fromRecords(list: DObject[]): Table | null {
  const columns: string[] = [];
  const index = new Map<string, number>();
  const flat: [string, DValue][][] = [];
  for (const r of list) {
    const cells: [string, DValue][] = [];
    flatten(r, null, cells);
    for (const [k] of cells) {
      if (index.has(k)) continue;
      index.set(k, columns.length);
      columns.push(k);
      if (columns.length > MAX_COLUMNS) return null;
    }
    flat.push(cells);
  }
  const rows = flat.map((cells) => {
    const row: DValue[] = columns.map(() => NULL);
    for (const [k, v] of cells) row[index.get(k)!] = v;
    return row;
  });
  return checked({ columns, rows });
}

function checked(t: Table): Table | null {
  return t.columns.length === 0 || t.columns.length > MAX_COLUMNS || t.rows.length > MAX_ROWS ? null : t;
}

/**
 * The tree as a table, or null: records are found by unwrapping one-key objects (and an object whose only array holds the
 * records), nested values become dotted keys (address.city, tags.0), an array of arrays uses its first row as the header.
 */
export function toTable(root: DNode): Table | null {
  const node = unwrap(root);
  if (isValue(node)) return null;
  if (node.k === "arr") {
    if (node.i.length === 0) return null;
    if (node.i.every((x) => x.k === "arr")) {
      const rows = node.i as DArray[];
      if (rows.some((r) => r.i.some((c) => !isValue(c)))) return null;
      const width = Math.max(...rows.map((r) => r.i.length));
      if (width === 0) return null;
      const header = Array.from({ length: width }, (_, i) => {
        const c = rows[0].i[i] as DValue | undefined;
        return c && textOf(c) ? textOf(c) : `column${i + 1}`;
      });
      const body = rows.slice(1).map((r) => Array.from({ length: width }, (_, i) => (r.i[i] as DValue | undefined) ?? NULL));
      return checked({ columns: uniqueNames(header), rows: body });
    }
    if (node.i.every((x) => x.k === "obj")) return fromRecords(node.i as DObject[]);
    if (node.i.every(isValue)) return checked({ columns: ["value"], rows: node.i.map((v) => [v as DValue]) });
    return null;
  }
  return root.k === "obj" && root.e.size > 0 ? fromRecords([root]) : null;
}

export function tableToTree(t: Table): DArray {
  return arr(t.rows.map((r) => obj(t.columns.map((c, i) => [c, r[i] ?? NULL]))));
}

// ---- CSV / TSV ---------------------------------------------------------------------------------------------------------

export function detectSeparator(text: string): string {
  const counts: Record<string, number> = { ",": 0, ";": 0, "\t": 0 };
  let quoted = false;
  for (const c of text) {
    if (c === '"') quoted = !quoted;
    else if (!quoted && (c === "\n" || c === "\r")) break;
    else if (!quoted && c in counts) counts[c]++;
  }
  const best = Object.entries(counts).sort((a, b) => b[1] - a[1])[0];
  return best[1] === 0 ? "," : best[0];
}

export function parseRows(text: string, sep: string): string[][] {
  const rows: string[][] = [];
  let row: string[] = [];
  let field = "";
  let quoted = false;
  let fieldStarted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"') {
        if (text[i + 1] === '"') {
          field += '"';
          i++;
        } else quoted = false;
      } else field += c;
      continue;
    }
    if (c === '"' && !fieldStarted) {
      quoted = true;
      fieldStarted = true;
    } else if (c === sep) {
      row.push(field);
      field = "";
      fieldStarted = false;
    } else if (c === "\r" || c === "\n") {
      row.push(field);
      rows.push(row);
      row = [];
      field = "";
      fieldStarted = false;
      if (c === "\r" && text[i + 1] === "\n") i++;
    } else {
      field += c;
      fieldStarted = true;
    }
  }
  if (field || row.length) {
    row.push(field);
    rows.push(row);
  }
  return rows;
}

/** CSV/TSV with a header row. `sep` null = detect (',' ';' or tab). Plain numbers become numbers. */
export function parseDelimited(text: string, sep: string | null): Table {
  const rows = parseRows(text.charCodeAt(0) === 0xfeff ? text.slice(1) : text, sep ?? detectSeparator(text));
  while (rows.length && rows[rows.length - 1].every((c) => c === "")) rows.pop();
  if (!rows.length) return { columns: [], rows: [] };
  const body = rows.slice(1).filter((r) => !(r.length === 1 && r[0] === ""));
  const width = Math.max(rows[0].length, ...body.map((r) => r.length));
  const header = Array.from({ length: width }, (_, i) => rows[0][i] ?? "");
  return {
    columns: uniqueNames(header),
    rows: body.map((r) => Array.from({ length: width }, (_, i) => (r[i] ? infer(r[i]) : NULL))),
  };
}

/** CSV/TSV text with CRLF line ends. The caller adds the UTF-8 byte order mark Excel needs for Arabic. */
export function writeDelimited(t: Table, sep: string): string {
  const cell = (s: string) =>
    /["\r\n]/.test(s) || s.includes(sep) || /^\s|\s$/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  const line = (cells: string[]) => cells.map(cell).join(sep) + "\r\n";
  return line(t.columns) + t.rows.map((r) => line(r.map(textOf))).join("");
}

// ---- XML ---------------------------------------------------------------------------------------------------------------

/** The parts of the DOM the XML reader walks (a browser DOMParser Document satisfies it; tests pass a hand-made one). */
export interface XmlNodeLike {
  nodeType: number; // 1 element, 3 text, 4 CDATA
  localName?: string | null;
  nodeName: string;
  nodeValue: string | null;
  childNodes: ArrayLike<XmlNodeLike>;
  attributes?: ArrayLike<{ name: string; localName: string; prefix: string | null; value: string }>;
}
export interface XmlDocumentLike {
  doctype: unknown;
  documentElement: XmlNodeLike | null;
  getElementsByTagName(name: string): ArrayLike<unknown>;
}

/**
 * XML as the tree (same rules as the desktop): the document is {rootName: value}; an element with only text is that string;
 * an empty element is null; otherwise an object with "@attr" keys, child keys and "#text"; a repeated child name is an array.
 * Names are local names (prefixes and xmlns declarations dropped). A DTD is refused (no entities are ever expanded).
 */
export function xmlToTree(doc: XmlDocumentLike): DNode {
  if (doc.doctype) throw bad("XML");
  const root = doc.documentElement;
  if (!root || doc.getElementsByTagName("parsererror").length > 0 || root.nodeName === "parsererror") throw bad("XML");
  const name = (el: XmlNodeLike) => el.localName || el.nodeName.replace(/^.*:/, "");
  const element = (el: XmlNodeLike, depth: number): DNode => {
    if (depth > MAX_DEPTH) throw bad("XML");
    let out: DObject | null = null;
    for (const a of Array.from(el.attributes ?? [])) {
      if (a.prefix === "xmlns" || a.name === "xmlns" || a.name.startsWith("xmlns:")) continue;
      (out ??= obj()).e.set("@" + (a.localName || a.name), str(a.value));
    }
    let text = "";
    const children: [string, DNode][] = [];
    for (const c of Array.from(el.childNodes)) {
      if (c.nodeType === 1) children.push([name(c), element(c, depth + 1)]);
      else if (c.nodeType === 3 || c.nodeType === 4) text += c.nodeValue ?? "";
    }
    if (!out && children.length === 0) return text.length === 0 ? NULL : str(text);
    out ??= obj();
    const groups = new Map<string, DNode[]>();
    for (const [k, v] of children) {
      const list = groups.get(k);
      if (list) list.push(v);
      else groups.set(k, [v]);
    }
    for (const [k, list] of groups) {
      let key = k;
      for (let n = 2; out.e.has(key); n++) key = `${k}_${n}`;
      out.e.set(key, list.length === 1 ? list[0] : arr(list));
    }
    const content = children.length ? text.trim() : text;
    if (content.trim()) out.e.set("#text", str(content));
    return out;
  };
  return obj([[name(root), element(root, 1)]]);
}

/** A valid XML name for any key: "first name" -> first_x0020_name, "2024" -> _x0032_024 (the .NET XmlConvert rule). */
export function xmlName(key: string): string {
  if (!key) return "_";
  let out = "";
  const chars = Array.from(key.replace(/:/g, "_"));
  chars.forEach((ch, i) => {
    const ok = i === 0 ? /[\p{L}_]/u.test(ch) : /[\p{L}\p{N}_.\-\p{Mn}\p{Mc}]/u.test(ch);
    out += ok ? ch : `_x${ch.codePointAt(0)!.toString(16).toUpperCase().padStart(4, "0")}_`;
  });
  return out;
}

function xmlEscape(s: string, attr: boolean): string {
  // Characters XML 1.0 cannot carry (most control characters) are dropped.
  const clean = Array.from(s)
    .filter((ch) => {
      const c = ch.codePointAt(0)!;
      return c === 9 || c === 10 || c === 13 || (c >= 0x20 && c <= 0xd7ff) || (c >= 0xe000 && c <= 0xfffd) || c >= 0x10000;
    })
    .join("");
  const t = clean.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  return attr ? t.replace(/"/g, "&quot;").replace(/\n/g, "&#xA;").replace(/\r/g, "&#xD;").replace(/\t/g, "&#x9;") : t;
}

export function writeXml(node: DNode): string {
  const out: string[] = ['<?xml version="1.0" encoding="utf-8"?>'];
  const el = (key: string, x: DNode, depth: number) => {
    const pad = "  ".repeat(depth);
    const name = xmlName(key);
    if (x.k === "arr") {
      if (x.i.length === 0) return void out.push(`${pad}<${name} />`);
      out.push(`${pad}<${name}>`);
      for (const item of x.i) named("item", item, depth + 1);
      out.push(`${pad}</${name}>`);
      return;
    }
    if (x.k === "obj") {
      const attrs = [...x.e]
        .filter(([k, v]) => k.length > 1 && k.startsWith("@") && isValue(v))
        .map(([k, v]) => ` ${xmlName(k.slice(1))}="${xmlEscape(textOf(v as DValue), true)}"`)
        .join("");
      const rest = [...x.e].filter(([k, v]) => !(k.length > 1 && k.startsWith("@") && isValue(v)));
      const text = rest.find(([k, v]) => k === "#text" && isValue(v));
      const elements = rest.filter(([k, v]) => !(k === "#text" && isValue(v)));
      if (elements.length === 0) {
        out.push(text ? `${pad}<${name}${attrs}>${xmlEscape(textOf(text[1] as DValue), false)}</${name}>` : `${pad}<${name}${attrs} />`);
        return;
      }
      out.push(`${pad}<${name}${attrs}>${text ? xmlEscape(textOf(text[1] as DValue), false) : ""}`);
      for (const [k, v] of elements) named(k, v, depth + 1);
      out.push(`${pad}</${name}>`);
      return;
    }
    out.push(x.k === "null" ? `${pad}<${name} />` : `${pad}<${name}>${xmlEscape(textOf(x), false)}</${name}>`);
  };
  const named = (key: string, x: DNode, depth: number) => {
    if (x.k === "arr") for (const item of x.i) el(key, item.k === "arr" ? obj([["item", item]]) : item, depth);
    else el(key, x, depth);
  };
  if (node.k === "obj" && node.e.size === 1 && [...node.e.values()][0].k !== "arr") {
    const [[k, v]] = [...node.e];
    el(k, v, 0);
  } else el("root", node, 0);
  return out.join("\n") + "\n";
}
