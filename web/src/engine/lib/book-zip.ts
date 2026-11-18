// SPDX-License-Identifier: GPL-3.0-or-later
// Zip containers of e-books and comics (EPUB, HTMLZ, TXTZ, CBZ): a reader that inflates only the entries asked for, after the same
// checks as archive extraction (sizes, ratio, encryption, CRC), and a writer that puts "mimetype" first and stored, as EPUB requires.

import { inflateSync, zipSync, type Zippable } from "fflate";
import { ConvertError } from "../types";
import { LIMITS, checkLimits, crc32, readZipDirectory, zipEntryRange, type ArchiveLimits, type ZipEntry } from "./zip-safe";

const MIB = 1024 * 1024;
export const BOOK_LIMITS: ArchiveLimits = { ...LIMITS, maxEntries: 20_000, maxTotalBytes: 2048 * MIB, maxEntryBytes: 256 * MIB };

export class BookZip {
  private readonly entries = new Map<string, ZipEntry>();
  /** Entry names in archive order ("/" separators, no leading slash). */
  readonly names: string[] = [];

  constructor(private readonly data: Uint8Array, limits: ArchiveLimits = BOOK_LIMITS) {
    let list: ZipEntry[];
    try {
      list = readZipDirectory(data, limits.maxEntries);
    } catch (e) {
      if (e instanceof ConvertError && e.code === "tooManyFiles") throw e;
      throw new ConvertError("corrupt");
    }
    const files = list.filter((e) => !e.isDirectory && !e.isSymlink);
    checkLimits(files, limits);
    for (const e of files) {
      const name = e.name.replace(/\\/g, "/").replace(/^\/+/, "");
      if (this.entries.has(name)) continue;
      this.entries.set(name, e);
      this.names.push(name);
    }
  }

  has(name: string): boolean {
    return this.entries.has(name);
  }

  /** Case-insensitive lookup of an entry name. */
  find(name: string): string | undefined {
    if (this.entries.has(name)) return name;
    const lower = name.toLowerCase();
    return this.names.find((n) => n.toLowerCase() === lower);
  }

  size(name: string): number {
    return this.entries.get(name)?.size ?? -1;
  }

  isEncrypted(name: string): boolean {
    return this.entries.get(name)?.encrypted ?? false;
  }

  /** The entry's bytes (CRC checked), or undefined when there is no such entry. */
  read(name: string): Uint8Array | undefined {
    const entry = this.entries.get(name);
    if (!entry) return undefined;
    if (entry.encrypted) throw new ConvertError("encrypted");
    if (entry.method !== 0 && entry.method !== 8) throw new ConvertError("unsupportedArchive");
    const [start, end] = zipEntryRange(this.data, entry);
    let out: Uint8Array;
    if (entry.method === 0) out = this.data.subarray(start, end);
    else {
      try {
        out = inflateSync(this.data.subarray(start, end), { out: new Uint8Array(entry.size) });
      } catch {
        throw new ConvertError("corrupt");
      }
    }
    if (out.length !== entry.size || crc32(out) !== entry.crc) throw new ConvertError("corrupt");
    return out;
  }

  readText(name: string): string | undefined {
    const bytes = this.read(name);
    return bytes ? decodeUtf(bytes) : undefined;
  }
}

/** UTF-8 (or UTF-16 with a byte order mark) text of a container member. */
export function decodeUtf(bytes: Uint8Array): string {
  if (bytes.length >= 2 && bytes[0] === 0xff && bytes[1] === 0xfe) return new TextDecoder("utf-16le").decode(bytes.subarray(2));
  if (bytes.length >= 2 && bytes[0] === 0xfe && bytes[1] === 0xff) return new TextDecoder("utf-16be").decode(bytes.subarray(2));
  return new TextDecoder("utf-8").decode(bytes).replace(/^﻿/, "");
}

/** True when the bytes start like a zip file. */
export function isZip(bytes: Uint8Array): boolean {
  return bytes.length >= 4 && bytes[0] === 0x50 && bytes[1] === 0x4b && (bytes[2] === 3 || bytes[2] === 5) && (bytes[3] === 4 || bytes[3] === 6);
}

/** True when the bytes are a RAR (any version) or 7z archive: comics the Windows app reads. */
export function isRarOr7z(bytes: Uint8Array): boolean {
  const rar = bytes.length >= 7 && bytes[0] === 0x52 && bytes[1] === 0x61 && bytes[2] === 0x72 && bytes[3] === 0x21 && bytes[4] === 0x1a && bytes[5] === 0x07;
  const sevenZip = bytes.length >= 6 && bytes[0] === 0x37 && bytes[1] === 0x7a && bytes[2] === 0xbc && bytes[3] === 0xaf && bytes[4] === 0x27 && bytes[5] === 0x1c;
  return rar || sevenZip;
}

export interface ZipItem {
  name: string;
  data: Uint8Array | string;
  /** Store without compression (pictures, the EPUB mimetype). */
  store?: boolean;
}

const utf8 = new TextEncoder();

/** Writes a zip in the given order. */
export function writeZip(items: readonly ZipItem[]): Uint8Array {
  const zippable: Zippable = {};
  for (const item of items) {
    const data = typeof item.data === "string" ? utf8.encode(item.data) : item.data;
    zippable[item.name] = [data, { level: item.store ? 0 : 6 }];
  }
  return zipSync(zippable);
}
