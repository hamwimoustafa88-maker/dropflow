// Hand-built binary fixtures for the images tests (not a test file itself). Independent of the code under test.
import { deflateSync } from "node:zlib";

export function concat(...parts: Uint8Array[]): Uint8Array {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let o = 0;
  for (const p of parts) {
    out.set(p, o);
    o += p.length;
  }
  return out;
}

const enc = new TextEncoder();
export const ascii = (s: string) => enc.encode(s);

export function u32be(n: number): Uint8Array {
  return new Uint8Array([(n >>> 24) & 255, (n >>> 16) & 255, (n >>> 8) & 255, n & 255]);
}
export function u32le(n: number): Uint8Array {
  return new Uint8Array([n & 255, (n >>> 8) & 255, (n >>> 16) & 255, (n >>> 24) & 255]);
}
export function u16be(n: number): Uint8Array {
  return new Uint8Array([(n >>> 8) & 255, n & 255]);
}

// ---- PNG ----------------------------------------------------------------------------------------------------------

let crcTable: Uint32Array | null = null;
export function crc32(bytes: Uint8Array): number {
  if (!crcTable) {
    crcTable = new Uint32Array(256);
    for (let n = 0; n < 256; n++) {
      let c = n;
      for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      crcTable[n] = c >>> 0;
    }
  }
  let c = 0xffffffff;
  for (const b of bytes) c = crcTable[(c ^ b) & 255] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

export function pngChunk(type: string, data: Uint8Array): Uint8Array {
  const body = concat(ascii(type), data);
  return concat(u32be(data.length), body, u32be(crc32(body)));
}

export const PNG_SIG = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

/** A valid RGBA PNG; every pixel is the given colour. `extra` chunks are inserted before IDAT. */
export function buildPng(w: number, h: number, rgba: [number, number, number, number] = [200, 30, 30, 255], extra: Uint8Array[] = []): Uint8Array {
  const ihdr = concat(u32be(w), u32be(h), new Uint8Array([8, 6, 0, 0, 0]));
  const row = new Uint8Array(1 + w * 4);
  for (let x = 0; x < w; x++) row.set(rgba, 1 + x * 4);
  const raw = new Uint8Array(row.length * h);
  for (let y = 0; y < h; y++) raw.set(row, y * row.length);
  return concat(PNG_SIG, pngChunk("IHDR", ihdr), ...extra, pngChunk("IDAT", new Uint8Array(deflateSync(raw))), pngChunk("IEND", new Uint8Array()));
}

// ---- TIFF / EXIF --------------------------------------------------------------------------------------------------

export interface Ent {
  tag: number;
  /** 1 BYTE, 2 ASCII, 3 SHORT, 4 LONG, 5 RATIONAL */
  type: 1 | 2 | 3 | 4 | 5;
  /** strings for ASCII; numbers otherwise (RATIONAL = flat [num, den, num, den...]) */
  data: number[] | string;
}

const SIZE = { 1: 1, 2: 1, 3: 2, 4: 4, 5: 8 } as const;

export function buildTiff(le: boolean, ifd0: Ent[], exif?: Ent[], gps?: Ent[]): Uint8Array {
  const n0 = ifd0.length + (exif ? 1 : 0) + (gps ? 1 : 0);
  const ifdSize = (n: number) => 2 + 12 * n + 4;
  const ifd0Off = 8;
  const exifOff = ifd0Off + ifdSize(n0);
  const gpsOff = exifOff + (exif ? ifdSize(exif.length) : 0);
  let valueOff = gpsOff + (gps ? ifdSize(gps.length) : 0);
  const buf = new Uint8Array(8192);
  const dv = new DataView(buf.buffer);
  buf[0] = buf[1] = le ? 0x49 : 0x4d;
  dv.setUint16(2, 42, le);
  dv.setUint32(4, ifd0Off, le);

  const writeIfd = (off: number, ents: Ent[]) => {
    dv.setUint16(off, ents.length, le);
    ents.forEach((e, i) => {
      const p = off + 2 + 12 * i;
      dv.setUint16(p, e.tag, le);
      dv.setUint16(p + 2, e.type, le);
      let bytes: number[];
      let count: number;
      if (e.type === 2) {
        const s = Array.from(ascii(e.data as string));
        bytes = [...s, 0];
        count = bytes.length;
      } else {
        const d = e.data as number[];
        count = e.type === 5 ? d.length / 2 : d.length;
        const tmp = new Uint8Array(count * SIZE[e.type] + 0);
        const tdv = new DataView(tmp.buffer);
        d.forEach((v, j) => {
          if (e.type === 1) tmp[j] = v;
          else if (e.type === 3) tdv.setUint16(j * 2, v, le);
          else tdv.setUint32(j * 4, v, le); // LONG, and both halves of RATIONAL
        });
        bytes = Array.from(tmp);
      }
      dv.setUint32(p + 4, count, le);
      if (bytes.length <= 4) {
        bytes.forEach((b, j) => (buf[p + 8 + j] = b));
      } else {
        dv.setUint32(p + 8, valueOff, le);
        bytes.forEach((b, j) => (buf[valueOff + j] = b));
        valueOff += bytes.length + (bytes.length & 1);
      }
    });
    dv.setUint32(off + 2 + 12 * ents.length, 0, le);
  };

  const withPointers: Ent[] = [...ifd0];
  if (exif) withPointers.push({ tag: 0x8769, type: 4, data: [exifOff] });
  if (gps) withPointers.push({ tag: 0x8825, type: 4, data: [gpsOff] });
  withPointers.sort((a, b) => a.tag - b.tag);
  writeIfd(ifd0Off, withPointers);
  if (exif) writeIfd(exifOff, [...exif].sort((a, b) => a.tag - b.tag));
  if (gps) writeIfd(gpsOff, [...gps].sort((a, b) => a.tag - b.tag));
  return buf.slice(0, valueOff);
}

/** Full-featured EXIF fixture: camera, exposure and a GPS fix at 48 51' 30" N, 2 17' 40.2" E, 35 m. */
export function fullTiff(le: boolean, opts: { orientation?: number; latRef?: string; lonRef?: string; altRef?: number } = {}): Uint8Array {
  return buildTiff(
    le,
    [
      { tag: 0x010f, type: 2, data: "DropFlow Cam" },
      { tag: 0x0110, type: 2, data: "Model X" },
      ...(opts.orientation ? [{ tag: 0x0112, type: 3 as const, data: [opts.orientation] }] : []),
      { tag: 0x0131, type: 2, data: "Soft 1.0" },
      { tag: 0x0132, type: 2, data: "2026:10:21 12:00:00" },
    ],
    [
      { tag: 0x829a, type: 5, data: [1, 250] },
      { tag: 0x829d, type: 5, data: [28, 10] },
      { tag: 0x8827, type: 3, data: [200] },
      { tag: 0x9003, type: 2, data: "2026:10:20 08:15:30" },
      { tag: 0x920a, type: 5, data: [35, 1] },
      { tag: 0xa434, type: 2, data: "Lens 35mm" },
    ],
    [
      { tag: 1, type: 2, data: opts.latRef ?? "N" },
      { tag: 2, type: 5, data: [48, 1, 51, 1, 30, 1] },
      { tag: 3, type: 2, data: opts.lonRef ?? "E" },
      { tag: 4, type: 5, data: [2, 1, 17, 1, 402, 10] },
      { tag: 5, type: 1, data: [opts.altRef ?? 0] },
      { tag: 6, type: 5, data: [35, 1] },
    ],
  );
}

// ---- JPEG ---------------------------------------------------------------------------------------------------------

export function jpegSegment(marker: number, payload: Uint8Array): Uint8Array {
  return concat(new Uint8Array([0xff, marker]), u16be(payload.length + 2), payload);
}

export interface JpegOpts {
  width?: number;
  height?: number;
  exifTiff?: Uint8Array;
  xmp?: boolean;
  icc?: boolean;
  iptc?: boolean;
  com?: boolean;
}

/** Structurally valid JPEG (markers and lengths are right; the scan bytes are not a decodable picture). */
export function buildJpeg(o: JpegOpts = {}): { bytes: Uint8Array; scan: Uint8Array } {
  const w = o.width ?? 8;
  const h = o.height ?? 6;
  const segs: Uint8Array[] = [new Uint8Array([0xff, 0xd8])];
  segs.push(jpegSegment(0xe0, concat(ascii("JFIF\0"), new Uint8Array([1, 1, 0, 0, 1, 0, 1, 0, 0]))));
  if (o.exifTiff) segs.push(jpegSegment(0xe1, concat(ascii("Exif\0\0"), o.exifTiff)));
  if (o.xmp) segs.push(jpegSegment(0xe1, ascii('http://ns.adobe.com/xap/1.0/\0<x:xmpmeta xmlns:x="adobe:ns:meta/"/>')));
  if (o.icc) segs.push(jpegSegment(0xe2, concat(ascii("ICC_PROFILE\0"), new Uint8Array([1, 1, 9, 9, 9, 9]))));
  if (o.iptc) segs.push(jpegSegment(0xed, concat(ascii("Photoshop 3.0\0"), new Uint8Array([1, 2, 3]))));
  if (o.com) segs.push(jpegSegment(0xfe, ascii("hello comment")));
  segs.push(jpegSegment(0xdb, new Uint8Array([0, ...new Array(64).fill(16)])));
  segs.push(jpegSegment(0xc0, new Uint8Array([8, (h >> 8) & 255, h & 255, (w >> 8) & 255, w & 255, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1])));
  segs.push(jpegSegment(0xc4, new Uint8Array([0x00, ...new Array(16).fill(0), 0])));
  const sosHeader = jpegSegment(0xda, new Uint8Array([3, 1, 0, 2, 0x11, 3, 0x11, 0, 63, 0]));
  const scan = new Uint8Array([0x12, 0xff, 0x00, 0x34, 0xd5, 0xff, 0x00, 0x99, 0x01, 0x02, 0xff, 0xd0, 0x77]);
  const eoi = new Uint8Array([0xff, 0xd9]);
  const bytes = concat(...segs, sosHeader, scan, eoi);
  return { bytes, scan: concat(sosHeader, scan, eoi) };
}

// ---- WebP ---------------------------------------------------------------------------------------------------------

export function riffChunk(type: string, data: Uint8Array): Uint8Array {
  return concat(ascii(type), u32le(data.length), data, data.length & 1 ? new Uint8Array([0]) : new Uint8Array());
}

export function buildWebp(opts: { exif?: boolean; xmp?: boolean; width?: number; height?: number } = {}): Uint8Array {
  const w = (opts.width ?? 10) - 1;
  const h = (opts.height ?? 7) - 1;
  const flags = (opts.exif ? 0x08 : 0) | (opts.xmp ? 0x04 : 0) | 0x20; // + ICC flag, must survive
  const vp8x = riffChunk("VP8X", new Uint8Array([flags, 0, 0, 0, w & 255, (w >> 8) & 255, (w >> 16) & 255, h & 255, (h >> 8) & 255, (h >> 16) & 255]));
  const chunks = [vp8x, riffChunk("ICCP", new Uint8Array([1, 2, 3])), riffChunk("VP8 ", new Uint8Array([1, 2, 3, 4, 5]))];
  if (opts.exif) chunks.push(riffChunk("EXIF", concat(ascii("Exif\0\0"), fullTiff(true))));
  if (opts.xmp) chunks.push(riffChunk("XMP ", ascii("<xmp/>")));
  const body = concat(ascii("WEBP"), ...chunks);
  return concat(ascii("RIFF"), u32le(body.length), body);
}
