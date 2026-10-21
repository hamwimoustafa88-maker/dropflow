// Pure format knowledge for the images converter: mime/extension maps, magic-byte sniffing, header-only
// dimension readers and SVG size parsing. Node-compatible (no DOM).
//
// HEIC and TIFF inputs are deliberately NOT offered: no browser decodes them reliably (only Safari does HEIC) and a
// license-clean WASM decoder (libheif is LGPL + HEVC patents; ~1.5 MB) is not justified yet. The Windows app handles both.

import type { ImageCaps } from "../caps";
import { normalizeExt } from "../names";

export type Mime =
  | "image/png"
  | "image/jpeg"
  | "image/webp"
  | "image/avif"
  | "image/bmp"
  | "image/x-icon"
  | "image/gif"
  | "image/svg+xml"
  | "application/pdf";

const MIME_BY_EXT: Record<string, Mime> = {
  png: "image/png",
  jpg: "image/jpeg",
  webp: "image/webp",
  avif: "image/avif",
  bmp: "image/bmp",
  ico: "image/x-icon",
  gif: "image/gif",
  svg: "image/svg+xml",
  pdf: "application/pdf",
};

export function mimeForExt(ext: string): Mime | undefined {
  return MIME_BY_EXT[normalizeExt(ext)];
}

export function extForMime(mime: string): string | undefined {
  const m = mime.toLowerCase().split(";")[0].trim();
  if (m === "image/jpg") return "jpg";
  for (const [ext, v] of Object.entries(MIME_BY_EXT)) if (v === m) return ext;
  if (m === "image/vnd.microsoft.icon") return "ico";
  return undefined;
}

/** Canvas can encode these (subject to runtime detection for webp/avif). */
export type EncodeFormat = "png" | "jpg" | "webp" | "avif";

/** Extensions the browser can decode through createImageBitmap / <img> (already normalized). AVIF depends on caps. */
export function canDecode(ext: string, caps: ImageCaps): boolean {
  switch (normalizeExt(ext)) {
    case "png":
    case "jpg":
    case "webp":
    case "gif":
    case "bmp":
    case "ico":
    case "svg":
      return true;
    case "avif":
      return caps.decodeAvif;
    default:
      return false;
  }
}

export function canEncode(format: string, caps: ImageCaps): boolean {
  switch (normalizeExt(format)) {
    case "png":
      return caps.encode.png;
    case "jpg":
      return caps.encode.jpg;
    case "webp":
      return caps.encode.webp;
    case "avif":
      return caps.encode.avif;
    default:
      return false;
  }
}

/** "Keep the format" for a re-encode: png/jpg stay, webp stays only when encodable, everything else becomes jpg. */
export function sameFormatForCompress(ext: string, caps: ImageCaps): EncodeFormat {
  const e = normalizeExt(ext);
  if (e === "png") return "png";
  if (e === "webp" && caps.encode.webp) return "webp";
  return "jpg";
}

/** Output format for resize/crop: png/jpg/webp(if encodable) stay the same, everything else becomes png. */
export function sameFormatForEdit(ext: string, caps: ImageCaps): EncodeFormat {
  const e = normalizeExt(ext);
  if (e === "jpg") return "jpg";
  if (e === "webp" && caps.encode.webp) return "webp";
  return "png";
}

export type MetaKind = "jpeg" | "png" | "webp";

export function sniffKind(bytes: Uint8Array): MetaKind | null {
  if (bytes.length >= 3 && bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) return "jpeg";
  if (
    bytes.length >= 8 &&
    bytes[0] === 0x89 && bytes[1] === 0x50 && bytes[2] === 0x4e && bytes[3] === 0x47 &&
    bytes[4] === 0x0d && bytes[5] === 0x0a && bytes[6] === 0x1a && bytes[7] === 0x0a
  )
    return "png";
  if (bytes.length >= 12 && ascii(bytes, 0, 4) === "RIFF" && ascii(bytes, 8, 12) === "WEBP") return "webp";
  return null;
}

export function ascii(bytes: Uint8Array, start: number, end: number): string {
  let s = "";
  for (let i = start; i < end && i < bytes.length; i++) s += String.fromCharCode(bytes[i]);
  return s;
}

export function be16(b: Uint8Array, o: number): number {
  return (b[o] << 8) | b[o + 1];
}
export function be32(b: Uint8Array, o: number): number {
  return ((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]) >>> 0;
}
export function le16(b: Uint8Array, o: number): number {
  return b[o] | (b[o + 1] << 8);
}
export function le32(b: Uint8Array, o: number): number {
  return (b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)) >>> 0;
}

export interface Dimensions {
  width: number;
  height: number;
}

/** Raw (un-oriented) JPEG dimensions from the first SOFn marker; null when not found in `bytes`. */
export function jpegDimensions(bytes: Uint8Array): Dimensions | null {
  if (bytes.length < 4 || bytes[0] !== 0xff || bytes[1] !== 0xd8) return null;
  let pos = 2;
  while (pos + 3 < bytes.length) {
    if (bytes[pos] !== 0xff) return null;
    while (pos + 1 < bytes.length && bytes[pos + 1] === 0xff) pos++;
    const marker = bytes[pos + 1];
    if (marker === 0xd9 || marker === 0xda) return null;
    if (marker === 0x01 || (marker >= 0xd0 && marker <= 0xd8)) {
      pos += 2;
      continue;
    }
    const len = be16(bytes, pos + 2);
    // SOF0..SOF15 except DHT (C4), JPG (C8), DAC (CC)
    if (marker >= 0xc0 && marker <= 0xcf && marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc) {
      if (pos + 9 > bytes.length) return null;
      const height = be16(bytes, pos + 5);
      const width = be16(bytes, pos + 7);
      return width > 0 && height > 0 ? { width, height } : null;
    }
    if (len < 2) return null;
    pos += 2 + len;
  }
  return null;
}

/** Dimensions from the header of PNG, JPEG, WebP, GIF or BMP without decoding. null when unknown. */
export function imageDimensions(bytes: Uint8Array): Dimensions | null {
  const kind = sniffKind(bytes);
  if (kind === "jpeg") return jpegDimensions(bytes);
  if (kind === "png") {
    if (bytes.length < 24) return null;
    return { width: be32(bytes, 16), height: be32(bytes, 20) };
  }
  if (kind === "webp") return webpDimensions(bytes);
  if (bytes.length >= 10 && ascii(bytes, 0, 3) === "GIF") return { width: le16(bytes, 6), height: le16(bytes, 8) };
  if (bytes.length >= 26 && bytes[0] === 0x42 && bytes[1] === 0x4d) {
    const w = le32(bytes, 18) | 0;
    const h = Math.abs(le32(bytes, 22) | 0);
    return w > 0 && h > 0 ? { width: w, height: h } : null;
  }
  return null;
}

function webpDimensions(b: Uint8Array): Dimensions | null {
  if (b.length < 30) return null;
  const fourcc = ascii(b, 12, 16);
  if (fourcc === "VP8X") {
    const w = (b[24] | (b[25] << 8) | (b[26] << 16)) + 1;
    const h = (b[27] | (b[28] << 8) | (b[29] << 16)) + 1;
    return { width: w, height: h };
  }
  if (fourcc === "VP8L") {
    if (b[20] !== 0x2f) return null;
    const bits = le32(b, 21);
    return { width: (bits & 0x3fff) + 1, height: ((bits >>> 14) & 0x3fff) + 1 };
  }
  if (fourcc === "VP8 ") {
    if (b[23] !== 0x9d || b[24] !== 0x01 || b[25] !== 0x2a) return null;
    return { width: le16(b, 26) & 0x3fff, height: le16(b, 28) & 0x3fff };
  }
  return null;
}

/**
 * Intrinsic size of an SVG from its root element: numeric width+height (px or unitless), else the viewBox scaled so the
 * longest side is 1024. null when the root has neither (the caller then falls back to the browser's natural size).
 */
export function svgSize(text: string): Dimensions | null {
  const m = /<svg\b([^>]*)>/i.exec(text);
  if (!m) return null;
  const attrs = m[1];
  const attr = (name: string): string | null => {
    const r = new RegExp(`(?:^|\\s)${name}\\s*=\\s*("([^"]*)"|'([^']*)')`, "i").exec(attrs);
    return r ? (r[2] ?? r[3] ?? "") : null;
  };
  const num = (v: string | null): number | null => {
    if (v === null) return null;
    const r = /^\s*([0-9]*\.?[0-9]+)\s*(px)?\s*$/i.exec(v);
    if (!r) return null;
    const n = parseFloat(r[1]);
    return n > 0 ? n : null;
  };
  const w = num(attr("width"));
  const h = num(attr("height"));
  if (w && h) return clampSide({ width: w, height: h });
  const vb = attr("viewBox");
  if (vb) {
    const p = vb.trim().split(/[\s,]+/).map(Number);
    if (p.length === 4 && p[2] > 0 && p[3] > 0 && p.every(Number.isFinite)) {
      if (w) return clampSide({ width: w, height: (w * p[3]) / p[2] });
      if (h) return clampSide({ width: (h * p[2]) / p[3], height: h });
      const k = 1024 / Math.max(p[2], p[3]);
      return clampSide({ width: p[2] * k, height: p[3] * k });
    }
  }
  return null;
}

function clampSide(d: Dimensions): Dimensions {
  const k = Math.min(1, 4096 / Math.max(d.width, d.height));
  return { width: Math.max(1, Math.round(d.width * k)), height: Math.max(1, Math.round(d.height * k)) };
}
