// Safe handling of archive entries: path sanitising (zip-slip), size/ratio caps and a pure ZIP central-directory
// reader. Nothing here inflates data; the impl does that only after every check passed.

import { ConvertError } from "../types";
import { extOf, sanitizeFileName } from "../names";

export interface ArchiveLimits {
  maxEntries: number;
  maxTotalBytes: number;
  maxEntryBytes: number;
  /** Largest allowed uncompressed/compressed ratio for entries bigger than `ratioFloorBytes`. */
  maxRatio: number;
  ratioFloorBytes: number;
}

const MIB = 1024 * 1024;

export const LIMITS: ArchiveLimits = {
  maxEntries: 5000,
  maxTotalBytes: 1024 * MIB,
  maxEntryBytes: 512 * MIB,
  maxRatio: 1000,
  ratioFloorBytes: MIB,
};

const MAX_PATH = 255;
const MAX_DEPTH = 32;
// Bidi embedding/override characters let "photo‮gpj.exe" masquerade as a different name.
const BIDI_CONTROLS = /[‪-‮⁦-⁩]/g;

/**
 * Turns an archive entry name into a safe relative path with "/" separators, or null when it must be rejected.
 * Rejected: ".." segments (zip-slip), NUL/control characters, empty results, absurd depth/length.
 * Normalised: backslashes, drive letters, leading slashes, "." and empty segments, illegal/reserved characters
 * (via sanitizeFileName), trailing dots/spaces, bidi override characters. Arabic and other Unicode is preserved.
 */
export function sanitizeEntryPath(raw: string): string | null {
  if (typeof raw !== "string" || raw === "") return null;
  if (/[\u0000-\u001f\u007f]/.test(raw)) return null;
  const path = raw.replace(/\\/g, "/").replace(BIDI_CONTROLS, "").replace(/^[A-Za-z]:/, "");
  const segments: string[] = [];
  for (const seg of path.split("/")) {
    if (seg === "" || seg === ".") continue;
    if (/^\.{2,}[\s.]*$/.test(seg) || seg.trim() === "..") return null;
    if (/^[\s.]+$/.test(seg)) continue;
    segments.push(sanitizeFileName(seg));
  }
  if (segments.length === 0 || segments.length > MAX_DEPTH) return null;
  let joined = segments.join("/");
  if (joined.length > MAX_PATH) {
    const last = segments[segments.length - 1];
    const ext = extOf(last);
    const keepExt = ext && ext.length <= 16 ? "." + ext : "";
    const dirPart = joined.slice(0, joined.length - last.length);
    const room = MAX_PATH - dirPart.length - keepExt.length;
    if (room < 1) return null;
    const stem = last.slice(0, last.length - keepExt.length).slice(0, room);
    joined = dirPart + stem + keepExt;
  }
  return joined;
}

/** Last segment of an entry path ("a/b/c.txt" -> "c.txt"). */
export function entryBaseName(path: string): string {
  return path.split("/").pop() ?? path;
}

/** Entry paths are carried in File.name with "/" separators; some browsers show ":" for "/" in File names. */
export function entryPathFromFileName(name: string): string {
  return name.replace(/:/g, "/");
}

/**
 * Checks the declared sizes of the entries that are about to be extracted. Throws ConvertError("tooManyFiles") or
 * ConvertError("tooLarge") BEFORE anything is inflated. `entries` are the files only (no directories).
 */
export function checkLimits(entries: readonly { size: number; compressedSize: number }[], limits: ArchiveLimits = LIMITS): void {
  if (entries.length > limits.maxEntries) throw new ConvertError("tooManyFiles", { max: limits.maxEntries });
  let total = 0;
  for (const e of entries) {
    if (e.size > limits.maxEntryBytes) throw new ConvertError("tooLarge");
    if (e.size > limits.ratioFloorBytes && (e.compressedSize <= 0 || e.size / e.compressedSize > limits.maxRatio)) throw new ConvertError("tooLarge");
    total += e.size;
    if (total > limits.maxTotalBytes) throw new ConvertError("tooLarge");
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// CRC-32 (used to verify inflated data, which also catches headers that lie about sizes)

const CRC_TABLE = (() => {
  const t = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c;
  }
  return t;
})();

export function crc32(data: Uint8Array, previous = 0): number {
  let c = ~previous;
  for (let i = 0; i < data.length; i++) c = CRC_TABLE[(c ^ data[i]) & 0xff] ^ (c >>> 8);
  return ~c >>> 0;
}

// ---------------------------------------------------------------------------------------------------------------------
// ZIP central directory

export interface ZipEntry {
  /** Raw (unsanitised) entry name. */
  name: string;
  flags: number;
  method: number;
  crc: number;
  compressedSize: number;
  size: number;
  localOffset: number;
  mtime: Date | null;
  isDirectory: boolean;
  isSymlink: boolean;
  encrypted: boolean;
}

const u16 = (d: Uint8Array, o: number) => d[o] | (d[o + 1] << 8);
const u32 = (d: Uint8Array, o: number) => (d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24)) >>> 0;
const u64 = (d: Uint8Array, o: number) => u32(d, o) + u32(d, o + 4) * 4294967296;

function corrupt(): never {
  throw new ConvertError("corrupt");
}

function dosDate(time: number, date: number): Date | null {
  const day = date & 31;
  const month = (date >> 5) & 15;
  if (day === 0 || month === 0 || month > 12) return null;
  return new Date(((date >> 9) & 127) + 1980, month - 1, day, (time >> 11) & 31, (time >> 5) & 63, (time & 31) * 2);
}

function decodeName(raw: Uint8Array, utf8Flag: boolean, extra: Uint8Array): string {
  // 1. Info-ZIP Unicode Path extra field (0x7075), trusted only when its CRC matches the raw name.
  for (let p = 0; p + 4 <= extra.length; ) {
    const id = u16(extra, p);
    const len = u16(extra, p + 2);
    if (id === 0x7075 && len >= 5 && p + 4 + len <= extra.length && extra[p + 4] === 1 && u32(extra, p + 5) === crc32(raw)) {
      return new TextDecoder("utf-8").decode(extra.subarray(p + 9, p + 4 + len));
    }
    p += 4 + len;
  }
  if (utf8Flag) return new TextDecoder("utf-8").decode(raw);
  // 2. Many Unix tools write UTF-8 without setting the flag.
  try {
    return new TextDecoder("utf-8", { fatal: true }).decode(raw);
  } catch {
    // 3. Legacy single-byte names: windows-1256 is the best guess for an Arabic audience.
    try {
      return new TextDecoder("windows-1256").decode(raw);
    } catch {
      return new TextDecoder("latin1").decode(raw);
    }
  }
}

/** Reads the central directory of a ZIP (ZIP64 aware). Throws ConvertError("corrupt") / ("tooManyFiles"). */
export function readZipDirectory(data: Uint8Array, maxEntries: number = LIMITS.maxEntries): ZipEntry[] {
  if (data.length < 22) corrupt();
  const lowest = Math.max(0, data.length - 22 - 65535);
  let e = data.length - 22;
  while (e >= lowest && u32(data, e) !== 0x06054b50) e--;
  if (e < lowest) corrupt();

  let count = u16(data, e + 10);
  let offset = u32(data, e + 16);
  if (e >= 20 && u32(data, e - 20) === 0x07064b50) {
    const z = u64(data, e - 12);
    if (z + 56 <= data.length && u32(data, z) === 0x06064b50) {
      count = u64(data, z + 32);
      offset = u64(data, z + 48);
    }
  }
  if (count > maxEntries) throw new ConvertError("tooManyFiles", { max: maxEntries });

  const entries: ZipEntry[] = [];
  let o = offset;
  for (let i = 0; i < count; i++) {
    if (o + 46 > data.length || u32(data, o) !== 0x02014b50) corrupt();
    const madeBy = u16(data, o + 4);
    const flags = u16(data, o + 8);
    const method = u16(data, o + 10);
    const time = u16(data, o + 12);
    const date = u16(data, o + 14);
    const crc = u32(data, o + 16);
    let compressedSize = u32(data, o + 20);
    let size = u32(data, o + 24);
    const nameLen = u16(data, o + 28);
    const extraLen = u16(data, o + 30);
    const commentLen = u16(data, o + 32);
    const externalAttrs = u32(data, o + 38);
    let localOffset = u32(data, o + 42);
    const nameStart = o + 46;
    const extraStart = nameStart + nameLen;
    const next = extraStart + extraLen + commentLen;
    if (next > data.length) corrupt();
    const extra = data.subarray(extraStart, extraStart + extraLen);

    if (size === 0xffffffff || compressedSize === 0xffffffff || localOffset === 0xffffffff) {
      for (let p = 0; p + 4 <= extra.length; ) {
        const id = u16(extra, p);
        const len = u16(extra, p + 2);
        if (id === 1) {
          let q = p + 4;
          if (size === 0xffffffff && q + 8 <= p + 4 + len) {
            size = u64(extra, q);
            q += 8;
          }
          if (compressedSize === 0xffffffff && q + 8 <= p + 4 + len) {
            compressedSize = u64(extra, q);
            q += 8;
          }
          if (localOffset === 0xffffffff && q + 8 <= p + 4 + len) localOffset = u64(extra, q);
          break;
        }
        p += 4 + len;
      }
    }

    const name = decodeName(data.subarray(nameStart, extraStart), (flags & 0x800) !== 0, extra);
    const unixMode = madeBy >> 8 === 3 ? (externalAttrs >>> 16) & 0xf000 : 0;
    entries.push({
      name,
      flags,
      method,
      crc,
      compressedSize,
      size,
      localOffset,
      mtime: dosDate(time, date),
      isDirectory: /[\\/]$/.test(name) || (!!(externalAttrs & 0x10) && size === 0 && compressedSize <= 2),
      isSymlink: unixMode === 0xa000,
      encrypted: (flags & 0x1) !== 0 || (flags & 0x40) !== 0,
    });
    o = next;
  }
  return entries;
}

/** Byte range of an entry's (still compressed) data inside the archive. Throws "corrupt" when it does not fit. */
export function zipEntryRange(data: Uint8Array, entry: Pick<ZipEntry, "localOffset" | "compressedSize">): [number, number] {
  const o = entry.localOffset;
  if (o + 30 > data.length || u32(data, o) !== 0x04034b50) corrupt();
  const start = o + 30 + u16(data, o + 26) + u16(data, o + 28);
  const end = start + entry.compressedSize;
  if (end > data.length) corrupt();
  return [start, end];
}
