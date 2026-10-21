// Minimal, defensive EXIF/TIFF-IFD reader. Finds the Exif block in JPEG (APP1), PNG (eXIf), WebP (EXIF) or a bare TIFF
// and reads the few tags DropFlow shows. It NEVER throws: truncated or garbage input yields partial or empty data.
// Pure, Node-compatible.

import { ascii, be16, be32, le32 } from "./image-formats";

export interface ExifGps {
  latitude?: number;
  longitude?: number;
  altitude?: number;
}

export interface ExifData {
  make?: string;
  model?: string;
  software?: string;
  dateTime?: string;
  dateTimeOriginal?: string;
  orientation?: number;
  /** seconds */
  exposureTime?: number;
  fNumber?: number;
  iso?: number;
  /** millimetres */
  focalLength?: number;
  lensModel?: string;
  gps?: ExifGps;
}

const EXIF_PREFIX = "Exif\0\0";

function startsWithExifPrefix(b: Uint8Array, o: number): boolean {
  return ascii(b, o, o + 6) === EXIF_PREFIX;
}

function isTiffHeader(b: Uint8Array, o = 0): boolean {
  return (b[o] === 0x49 && b[o + 1] === 0x49 && b[o + 2] === 0x2a && b[o + 3] === 0x00) ||
    (b[o] === 0x4d && b[o + 1] === 0x4d && b[o + 2] === 0x00 && b[o + 3] === 0x2a);
}

/** Returns the TIFF block (starting at "II*\0" / "MM\0*") of an image file or raw EXIF payload, or null. */
export function findExifBlock(bytes: Uint8Array): Uint8Array | null {
  try {
    if (bytes.length < 8) return null;
    if (isTiffHeader(bytes)) return bytes;
    if (startsWithExifPrefix(bytes, 0) && isTiffHeader(bytes, 6)) return bytes.subarray(6);
    if (bytes[0] === 0xff && bytes[1] === 0xd8) return findInJpeg(bytes);
    if (bytes[0] === 0x89 && ascii(bytes, 1, 4) === "PNG") return findInPng(bytes);
    if (ascii(bytes, 0, 4) === "RIFF" && ascii(bytes, 8, 12) === "WEBP") return findInWebp(bytes);
  } catch {
    /* fall through */
  }
  return null;
}

function payloadToTiff(p: Uint8Array): Uint8Array | null {
  if (startsWithExifPrefix(p, 0) && isTiffHeader(p, 6)) return p.subarray(6);
  if (isTiffHeader(p)) return p;
  return null;
}

function findInJpeg(b: Uint8Array): Uint8Array | null {
  let pos = 2;
  while (pos + 4 <= b.length) {
    if (b[pos] !== 0xff) return null;
    while (pos + 1 < b.length && b[pos + 1] === 0xff) pos++;
    const marker = b[pos + 1];
    if (marker === 0xd9 || marker === 0xda) return null;
    if (marker === 0x01 || (marker >= 0xd0 && marker <= 0xd8)) {
      pos += 2;
      continue;
    }
    const len = be16(b, pos + 2);
    if (len < 2) return null;
    const end = pos + 2 + len;
    if (marker === 0xe1) {
      // The segment may be cut off by a truncated buffer; parse what is there.
      const tiff = payloadToTiff(b.subarray(pos + 4, Math.min(end, b.length)));
      if (tiff) return tiff;
    }
    pos = end;
  }
  return null;
}

function findInPng(b: Uint8Array): Uint8Array | null {
  let pos = 8;
  while (pos + 8 <= b.length) {
    const len = be32(b, pos);
    const type = ascii(b, pos + 4, pos + 8);
    const start = pos + 8;
    if (type === "eXIf") return payloadToTiff(b.subarray(start, Math.min(start + len, b.length)));
    if (type === "IEND") return null;
    pos = start + len + 4;
  }
  return null;
}

function findInWebp(b: Uint8Array): Uint8Array | null {
  let pos = 12;
  while (pos + 8 <= b.length) {
    const type = ascii(b, pos, pos + 4);
    const len = le32(b, pos + 4);
    const start = pos + 8;
    if (type === "EXIF") return payloadToTiff(b.subarray(start, Math.min(start + len, b.length)));
    pos = start + len + (len & 1);
  }
  return null;
}

// ---- TIFF IFD reader -------------------------------------------------------------------------------------------

const TYPE_SIZE: Record<number, number> = { 1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 6: 1, 7: 1, 8: 2, 9: 4, 10: 8, 11: 4, 12: 8 };

interface Entry {
  tag: number;
  type: number;
  count: number;
  /** absolute offset of the value bytes inside the TIFF block, or -1 when out of bounds */
  at: number;
}

class Tiff {
  readonly dv: DataView;
  constructor(readonly bytes: Uint8Array, readonly le: boolean) {
    this.dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  }

  entries(ifdOffset: number): Entry[] {
    const out: Entry[] = [];
    if (ifdOffset < 8 || ifdOffset + 2 > this.bytes.length) return out;
    const n = this.dv.getUint16(ifdOffset, this.le);
    for (let i = 0; i < Math.min(n, 1024); i++) {
      const e = ifdOffset + 2 + i * 12;
      if (e + 12 > this.bytes.length) break;
      const tag = this.dv.getUint16(e, this.le);
      const type = this.dv.getUint16(e + 2, this.le);
      const count = this.dv.getUint32(e + 4, this.le);
      const size = (TYPE_SIZE[type] ?? 0) * count;
      let at = -1;
      if (size > 0 && Number.isFinite(size)) {
        const start = size <= 4 ? e + 8 : this.dv.getUint32(e + 8, this.le);
        if (start >= 0 && start + size <= this.bytes.length) at = start;
      }
      out.push({ tag, type, count, at });
    }
    return out;
  }

  string(e: Entry): string | undefined {
    if (e.type !== 2 || e.at < 0) return undefined;
    let end = e.at + e.count;
    const nul = this.bytes.indexOf(0, e.at);
    if (nul !== -1 && nul < end) end = nul;
    const s = new TextDecoder().decode(this.bytes.subarray(e.at, Math.min(end, e.at + 512))).trim();
    return s.length ? s : undefined;
  }

  uint(e: Entry, i = 0): number | undefined {
    if (e.at < 0 || i >= e.count) return undefined;
    switch (e.type) {
      case 1:
      case 7:
        return this.bytes[e.at + i];
      case 3:
        return this.dv.getUint16(e.at + 2 * i, this.le);
      case 4:
        return this.dv.getUint32(e.at + 4 * i, this.le);
      default:
        return undefined;
    }
  }

  rational(e: Entry, i = 0): number | undefined {
    if (e.at < 0 || i >= e.count) return undefined;
    if (e.type !== 5 && e.type !== 10) return undefined;
    const o = e.at + 8 * i;
    const num = e.type === 5 ? this.dv.getUint32(o, this.le) : this.dv.getInt32(o, this.le);
    const den = e.type === 5 ? this.dv.getUint32(o + 4, this.le) : this.dv.getInt32(o + 4, this.le);
    if (den === 0) return undefined;
    const v = num / den;
    return Number.isFinite(v) ? v : undefined;
  }
}

function dms(t: Tiff, e: Entry | undefined, ref: string | undefined, positive: string, negative: string, limit: number): number | undefined {
  if (!e || e.count < 3) return undefined;
  const d = t.rational(e, 0);
  const m = t.rational(e, 1);
  const s = t.rational(e, 2);
  if (d === undefined || m === undefined || s === undefined) return undefined;
  let v = d + m / 60 + s / 3600;
  if (ref?.toUpperCase().startsWith(negative)) v = -v;
  else if (ref && !ref.toUpperCase().startsWith(positive)) return undefined;
  return Math.abs(v) <= limit ? v : undefined;
}

/** Reads the interesting tags of a TIFF/EXIF block. Never throws. */
export function parseTiff(block: Uint8Array): ExifData {
  const out: ExifData = {};
  try {
    if (block.length < 8) return out;
    const le = block[0] === 0x49 && block[1] === 0x49;
    const be = block[0] === 0x4d && block[1] === 0x4d;
    if (!le && !be) return out;
    const t = new Tiff(block, le);
    if (t.dv.getUint16(2, le) !== 42) return out;
    const seen = new Set<number>();
    const ifd0 = t.dv.getUint32(4, le);
    let exifIfd = 0;
    let gpsIfd = 0;
    seen.add(ifd0);
    for (const e of t.entries(ifd0)) {
      switch (e.tag) {
        case 0x010f:
          out.make = t.string(e);
          break;
        case 0x0110:
          out.model = t.string(e);
          break;
        case 0x0112: {
          const o = t.uint(e);
          if (o !== undefined && o >= 1 && o <= 8) out.orientation = o;
          break;
        }
        case 0x0131:
          out.software = t.string(e);
          break;
        case 0x0132:
          out.dateTime = t.string(e);
          break;
        case 0x8769:
          exifIfd = t.uint(e) ?? 0;
          break;
        case 0x8825:
          gpsIfd = t.uint(e) ?? 0;
          break;
      }
    }
    if (exifIfd && !seen.has(exifIfd)) {
      seen.add(exifIfd);
      for (const e of t.entries(exifIfd)) {
        switch (e.tag) {
          case 0x829a:
            out.exposureTime = t.rational(e);
            break;
          case 0x829d:
            out.fNumber = t.rational(e);
            break;
          case 0x8827:
            out.iso = t.uint(e);
            break;
          case 0x9003:
            out.dateTimeOriginal = t.string(e);
            break;
          case 0x920a:
            out.focalLength = t.rational(e);
            break;
          case 0xa434:
            out.lensModel = t.string(e);
            break;
        }
      }
    }
    if (gpsIfd && !seen.has(gpsIfd)) {
      seen.add(gpsIfd);
      const g: Record<number, Entry> = {};
      for (const e of t.entries(gpsIfd)) g[e.tag] = e;
      const latRef = g[1] ? t.string(g[1]) : undefined;
      const lonRef = g[3] ? t.string(g[3]) : undefined;
      const latitude = dms(t, g[2], latRef, "N", "S", 90);
      const longitude = dms(t, g[4], lonRef, "E", "W", 180);
      let altitude = g[6] ? t.rational(g[6]) : undefined;
      if (altitude !== undefined && g[5] && t.uint(g[5]) === 1) altitude = -altitude;
      const gps: ExifGps = {};
      if (latitude !== undefined) gps.latitude = latitude;
      if (longitude !== undefined) gps.longitude = longitude;
      if (altitude !== undefined) gps.altitude = altitude;
      if (Object.keys(gps).length) out.gps = gps;
    }
  } catch {
    /* partial data is fine */
  }
  for (const k of Object.keys(out) as (keyof ExifData)[]) if (out[k] === undefined) delete out[k];
  return out;
}

/** Parses the EXIF of an image file (JPEG/PNG/WebP), a raw TIFF block or an "Exif\0\0" payload. Never throws. */
export function parseExif(bytes: Uint8Array): ExifData {
  const block = findExifBlock(bytes);
  return block ? parseTiff(block) : {};
}

/** EXIF Orientation (1..8) or undefined when absent. */
export function readOrientation(bytes: Uint8Array): number | undefined {
  return parseExif(bytes).orientation;
}

/** True when an orientation other than "normal" is stored (stripping EXIF would change how the photo displays). */
export function hasNonTrivialOrientation(bytes: Uint8Array): boolean {
  const o = readOrientation(bytes);
  return o !== undefined && o !== 1;
}

export function hasExifData(d: ExifData): boolean {
  return Object.keys(d).length > 0;
}
