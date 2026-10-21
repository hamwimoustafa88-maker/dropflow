// Minimal TAR reader: POSIX ustar, GNU long names ('L') and pax extended headers ('x'). Regular files only
// (directories, links, devices are skipped). Returns views into the input (no copies). Names are returned RAW:
// the caller must run them through sanitizeEntryPath.

import { ConvertError } from "../types";
import { LIMITS } from "./zip-safe";

export interface TarEntry {
  name: string;
  size: number;
  data: Uint8Array;
  mtime: Date | null;
}

const BLOCK = 512;

function corrupt(): never {
  throw new ConvertError("corrupt");
}

function str(d: Uint8Array, start: number, end: number): string {
  let e = start;
  while (e < end && d[e] !== 0) e++;
  return new TextDecoder("utf-8").decode(d.subarray(start, e));
}

function readNumber(d: Uint8Array, start: number, end: number): number {
  if (d[start] & 0x80) {
    // GNU base-256 (used for sizes >= 8 GiB)
    let v = d[start] & 0x7f;
    for (let i = start + 1; i < end; i++) v = v * 256 + d[i];
    return v;
  }
  const text = str(d, start, end).trim();
  if (text === "") return 0;
  if (!/^[0-7]+$/.test(text)) corrupt();
  return parseInt(text, 8);
}

function checksumOk(h: Uint8Array): boolean {
  let unsigned = 0;
  let signed = 0;
  for (let i = 0; i < BLOCK; i++) {
    const b = i >= 148 && i < 156 ? 0x20 : h[i];
    unsigned += b;
    signed += b > 127 ? b - 256 : b;
  }
  const stored = readNumber(h, 148, 156);
  return stored === unsigned || stored === signed;
}

function isZeroBlock(d: Uint8Array, pos: number): boolean {
  for (let i = pos; i < pos + BLOCK; i++) if (d[i] !== 0) return false;
  return true;
}

function parsePax(data: Uint8Array): { path?: string; size?: number; mtime?: number } {
  const out: { path?: string; size?: number; mtime?: number } = {};
  let p = 0;
  while (p < data.length) {
    let sp = p;
    while (sp < data.length && data[sp] !== 0x20) sp++;
    const len = parseInt(new TextDecoder().decode(data.subarray(p, sp)), 10);
    if (!Number.isFinite(len) || len <= sp - p + 1 || p + len > data.length) corrupt();
    const record = new TextDecoder("utf-8").decode(data.subarray(sp + 1, p + len - 1));
    const eq = record.indexOf("=");
    if (eq > 0) {
      const key = record.slice(0, eq);
      const value = record.slice(eq + 1);
      if (key === "path") out.path = value;
      else if (key === "size") out.size = Number(value);
      else if (key === "mtime") out.mtime = Number(value);
    }
    p += len;
  }
  return out;
}

/** True when the bytes start with a plausible tar header (ustar magic, or a valid checksum on a non-empty name). */
export function looksLikeTar(d: Uint8Array): boolean {
  if (d.length < BLOCK) return false;
  const magic = String.fromCharCode(...d.subarray(257, 262));
  if (magic === "ustar") return true;
  if (d[0] === 0) return false;
  try {
    return checksumOk(d.subarray(0, BLOCK));
  } catch {
    return false;
  }
}

/**
 * Reads all regular files. Throws ConvertError("corrupt") for a bad checksum, an unreadable number or truncated
 * data, and ConvertError("tooManyFiles") past `maxEntries` regular files.
 */
export function readTar(bytes: Uint8Array, maxEntries: number = LIMITS.maxEntries): TarEntry[] {
  const out: TarEntry[] = [];
  let pos = 0;
  let longName: string | null = null;
  let pax: { path?: string; size?: number; mtime?: number } | null = null;
  while (pos + BLOCK <= bytes.length) {
    if (isZeroBlock(bytes, pos)) break;
    const h = bytes.subarray(pos, pos + BLOCK);
    if (!checksumOk(h)) corrupt();
    let size = readNumber(h, 124, 136);
    const type = h[156] === 0 ? "0" : String.fromCharCode(h[156]);
    const magic = String.fromCharCode(...h.subarray(257, 263));
    let name = str(h, 0, 100);
    if (magic === "ustar\u0000") {
      const prefix = str(h, 345, 500);
      if (prefix) name = `${prefix}/${name}`;
    }
    if (pax?.size !== undefined && (type === "0" || type === "7")) size = pax.size;
    const dataStart = pos + BLOCK;
    const dataEnd = dataStart + size;
    if (dataEnd > bytes.length) corrupt();
    const next = dataStart + Math.ceil(size / BLOCK) * BLOCK;

    if (type === "L") {
      longName = str(bytes, dataStart, dataEnd);
    } else if (type === "x") {
      pax = parsePax(bytes.subarray(dataStart, dataEnd));
    } else if (type === "g" || type === "K") {
      // global pax header / GNU long link name: nothing we need
    } else {
      if (type === "0" || type === "7") {
        if (out.length >= maxEntries) throw new ConvertError("tooManyFiles", { max: maxEntries });
        const mtimeSeconds = pax?.mtime ?? readNumber(h, 136, 148);
        out.push({
          name: pax?.path ?? longName ?? name,
          size,
          data: bytes.subarray(dataStart, dataEnd),
          mtime: mtimeSeconds > 0 ? new Date(mtimeSeconds * 1000) : null,
        });
      }
      longName = null;
      pax = null;
    }
    pos = next;
  }
  // Leftover non-zero bytes shorter than a block mean a truncated archive.
  if (pos < bytes.length && pos + BLOCK > bytes.length) {
    for (let i = pos; i < bytes.length; i++) if (bytes[i] !== 0) corrupt();
  }
  return out;
}
