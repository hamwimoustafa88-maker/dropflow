// SPDX-License-Identifier: GPL-3.0-or-later
// OpenType "sfnt" fonts and WOFF 1.0 (W3C Recommendation), written from the specifications. A font is its flavor plus its tables;
// the writer lays tables out in tag order on four-byte boundaries with correct checksums and head.checkSumAdjustment. WOFF stores each
// table zlib-compressed (RFC 1950) when that is smaller. Pure functions: usable in the worker and in Node tests.

import { unzlibSync, zlibSync } from "fflate";
import { ConvertError } from "../types";

export const TRUETYPE = 0x00010000;
export const CFF = 0x4f54544f; // 'OTTO'
const APPLE_TRUE = 0x74727565; // 'true'
const COLLECTION = 0x74746366; // 'ttcf'
export const WOFF = 0x774f4646; // 'wOFF'
export const WOFF2 = 0x774f4632; // 'wOF2'
const MAGIC = 0xb1b0afba;

export interface Sfnt {
  flavor: number;
  /** Tables by four-character tag. */
  tables: Map<string, Uint8Array>;
}

const u32 = (b: Uint8Array, o: number) => ((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]) >>> 0;
const u16 = (b: Uint8Array, o: number) => (b[o] << 8) | b[o + 1];
const tagAt = (b: Uint8Array, o: number) => String.fromCharCode(b[o], b[o + 1], b[o + 2], b[o + 3]);
const pad4 = (n: number) => (n + 3) & ~3;

function put32(b: Uint8Array, o: number, v: number) {
  b[o] = v >>> 24;
  b[o + 1] = (v >>> 16) & 0xff;
  b[o + 2] = (v >>> 8) & 0xff;
  b[o + 3] = v & 0xff;
}

function put16(b: Uint8Array, o: number, v: number) {
  b[o] = (v >>> 8) & 0xff;
  b[o + 1] = v & 0xff;
}

function putTag(b: Uint8Array, o: number, tag: string) {
  for (let i = 0; i < 4; i++) b[o + i] = tag.charCodeAt(i);
}

/** Sum of the big-endian 32-bit words of `data` (the last one zero padded), modulo 2^32. */
export function checksum(data: Uint8Array): number {
  let sum = 0;
  const whole = data.length & ~3;
  for (let i = 0; i < whole; i += 4) sum = (sum + u32(data, i)) >>> 0;
  if (whole < data.length) {
    let last = 0;
    for (let i = 0; i < 4; i++) last = (last << 8) | (whole + i < data.length ? data[whole + i] : 0);
    sum = (sum + (last >>> 0)) >>> 0;
  }
  return sum;
}

/** The checksum a table directory records: head.checkSumAdjustment counts as zero. */
export function tableChecksum(tag: string, data: Uint8Array): number {
  if (tag !== "head" || data.length < 12) return checksum(data);
  const copy = data.slice();
  put32(copy, 8, 0);
  return checksum(copy);
}

/** What the first four bytes say the file is. */
export function containerOf(bytes: Uint8Array): "sfnt" | "woff" | "woff2" | "collection" | "unknown" {
  if (bytes.length < 12) return "unknown";
  const sig = u32(bytes, 0);
  if (sig === WOFF) return "woff";
  if (sig === WOFF2) return "woff2";
  if (sig === COLLECTION) return "collection";
  if (sig === TRUETYPE || sig === CFF || sig === APPLE_TRUE) return "sfnt";
  return "unknown";
}

export function readSfnt(bytes: Uint8Array): Sfnt {
  const kind = containerOf(bytes);
  if (kind === "collection") throw new ConvertError("fontCollection");
  if (kind !== "sfnt") throw new ConvertError("notAFont");
  const flavor = u32(bytes, 0);
  const count = u16(bytes, 4);
  if (count === 0 || 12 + count * 16 > bytes.length) throw new ConvertError("corrupt");
  const tables = new Map<string, Uint8Array>();
  for (let i = 0; i < count; i++) {
    const r = 12 + i * 16;
    const tag = tagAt(bytes, r);
    const offset = u32(bytes, r + 8);
    const length = u32(bytes, r + 12);
    if (offset > bytes.length || length > bytes.length - offset || tables.has(tag)) throw new ConvertError("corrupt");
    tables.set(tag, bytes.slice(offset, offset + length));
  }
  if (!tables.has("head")) throw new ConvertError("corrupt");
  return { flavor, tables };
}

function sortedTags(font: Sfnt): string[] {
  // Byte order of the tags, which is what the specification sorts by (plain string order of ASCII).
  return [...font.tables.keys()].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
}

export function sfntSize(font: Sfnt): number {
  let size = 12 + 16 * font.tables.size;
  for (const t of font.tables.values()) size += pad4(t.length);
  return size;
}

/**
 * Writes the font as an sfnt file and updates head.checkSumAdjustment (0xB1B0AFBA minus the sum of the whole file, computed with the
 * adjustment taken as zero). The head table in `font` is replaced by the adjusted one.
 */
export function writeSfnt(font: Sfnt): Uint8Array {
  const tags = sortedTags(font);
  const n = tags.length;
  const out = new Uint8Array(sfntSize(font));
  put32(out, 0, font.flavor);
  put16(out, 4, n);
  let power = 1;
  let log = 0;
  while (power * 2 <= n) {
    power *= 2;
    log++;
  }
  put16(out, 6, power * 16);
  put16(out, 8, log);
  put16(out, 10, n * 16 - power * 16);

  let offset = 12 + 16 * n;
  let headOffset = -1;
  tags.forEach((tag, i) => {
    let data = font.tables.get(tag)!;
    if (tag === "head" && data.length >= 12) {
      data = data.slice();
      put32(data, 8, 0);
      headOffset = offset;
    }
    const r = 12 + i * 16;
    putTag(out, r, tag);
    put32(out, r + 4, checksum(data));
    put32(out, r + 8, offset);
    put32(out, r + 12, data.length);
    out.set(data, offset);
    offset += pad4(data.length);
  });

  if (headOffset >= 0) {
    const adjustment = (MAGIC - checksum(out)) >>> 0;
    put32(out, headOffset + 8, adjustment);
    const head = font.tables.get("head")!.slice();
    put32(head, 8, adjustment);
    font.tables.set("head", head);
  }
  return out;
}

export function decodeWoff(bytes: Uint8Array): Sfnt {
  if (bytes.length < 44 || u32(bytes, 0) !== WOFF) throw new ConvertError("notAFont");
  const flavor = u32(bytes, 4);
  if (flavor === COLLECTION) throw new ConvertError("fontCollection");
  if (u32(bytes, 8) > bytes.length) throw new ConvertError("corrupt");
  const count = u16(bytes, 12);
  if (count === 0 || 44 + count * 20 > bytes.length) throw new ConvertError("corrupt");
  const tables = new Map<string, Uint8Array>();
  for (let i = 0; i < count; i++) {
    const e = 44 + i * 20;
    const tag = tagAt(bytes, e);
    const offset = u32(bytes, e + 4);
    const compLength = u32(bytes, e + 8);
    const origLength = u32(bytes, e + 12);
    if (offset > bytes.length || compLength > bytes.length - offset || compLength > origLength || tables.has(tag)) throw new ConvertError("corrupt");
    const stored = bytes.subarray(offset, offset + compLength);
    let data: Uint8Array;
    if (compLength === origLength) {
      data = stored.slice();
    } else {
      try {
        data = unzlibSync(stored);
      } catch {
        throw new ConvertError("corrupt");
      }
      if (data.length !== origLength) throw new ConvertError("corrupt");
    }
    tables.set(tag, data);
  }
  return { flavor, tables };
}

/** WOFF 1.0: header, directory sorted by tag, each table zlib-compressed when smaller, four-byte aligned. No metadata, no private data. */
export function encodeWoff(source: Sfnt): Uint8Array {
  const font: Sfnt = { flavor: source.flavor, tables: new Map([...source.tables].map(([t, d]) => [t, d.slice()])) };
  writeSfnt(font); // fixes head.checkSumAdjustment first
  const tags = sortedTags(font);
  const stored = tags.map((tag) => {
    const data = font.tables.get(tag)!;
    const compressed = zlibSync(data, { level: 9 });
    return { tag, data: compressed.length < data.length ? compressed : data, origLength: data.length, sum: tableChecksum(tag, data) };
  });
  let offset = 44 + 20 * stored.length;
  const total = offset + stored.reduce((a, s) => a + pad4(s.data.length), 0);
  const out = new Uint8Array(total);
  put32(out, 0, WOFF);
  put32(out, 4, font.flavor);
  put32(out, 8, total);
  put16(out, 12, stored.length);
  put32(out, 16, sfntSize(font));
  const head = font.tables.get("head")!;
  put16(out, 20, head.length >= 8 ? u16(head, 4) : 1); // the font's own revision as the WOFF version
  put16(out, 22, head.length >= 8 ? u16(head, 6) : 0);
  stored.forEach((s, i) => {
    const e = 44 + i * 20;
    putTag(out, e, s.tag);
    put32(out, e + 4, offset);
    put32(out, e + 8, s.data.length);
    put32(out, e + 12, s.origLength);
    put32(out, e + 16, s.sum);
    out.set(s.data, offset);
    offset += pad4(s.data.length);
  });
  return out;
}

/** True when the font has CFF (PostScript) outlines, false for TrueType outlines. */
export function hasCffOutlines(font: Sfnt): boolean {
  return font.tables.has("CFF ") || font.tables.has("CFF2");
}
