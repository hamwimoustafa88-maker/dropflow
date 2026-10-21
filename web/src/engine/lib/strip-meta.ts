// Lossless metadata removal for JPEG, PNG and WebP, plus a verifier that re-parses the output. Pure, Node-compatible.
//
// JPEG: drops APP1 (Exif + XMP), APP13 (Photoshop/IPTC) and COM; keeps APP0/JFIF, APP2 (ICC, MPF), APP14 (Adobe colour
//       transform) and every coding segment, and the entropy-coded data is copied byte for byte.
// PNG:  drops eXIf, tEXt, iTXt, zTXt, tIME (and anything after IEND).
// WebP: drops EXIF and XMP chunks and clears the matching VP8X flags; the RIFF size is rewritten.

import { ConvertError } from "../types";
import { hasNonTrivialOrientation } from "./exif";
import { ascii, be16, be32, le32, sniffKind, type MetaKind } from "./image-formats";

export type { MetaKind };

function concat(parts: Uint8Array[]): Uint8Array {
  const total = parts.reduce((n, p) => n + p.length, 0);
  const out = new Uint8Array(total);
  let o = 0;
  for (const p of parts) {
    out.set(p, o);
    o += p.length;
  }
  return out;
}

// ---- JPEG ---------------------------------------------------------------------------------------------------------

interface JpegSegment {
  marker: number;
  /** start of the 0xFF marker byte */
  start: number;
  end: number;
}

/** Splits a JPEG into header segments up to (not including) SOS. `tail` is where the scan data begins. */
function jpegSegments(b: Uint8Array): { segments: JpegSegment[]; tail: number } {
  if (b.length < 4 || b[0] !== 0xff || b[1] !== 0xd8) throw new ConvertError("corrupt");
  const segments: JpegSegment[] = [];
  let pos = 2;
  while (pos < b.length) {
    if (b[pos] !== 0xff) throw new ConvertError("corrupt");
    while (pos + 1 < b.length && b[pos + 1] === 0xff) pos++; // fill bytes
    if (pos + 1 >= b.length) throw new ConvertError("corrupt");
    const marker = b[pos + 1];
    if (marker === 0x00) throw new ConvertError("corrupt");
    if (marker === 0xda || marker === 0xd9) return { segments, tail: pos };
    if (marker === 0x01 || (marker >= 0xd0 && marker <= 0xd8)) {
      segments.push({ marker, start: pos, end: pos + 2 });
      pos += 2;
      continue;
    }
    if (pos + 4 > b.length) throw new ConvertError("corrupt");
    const len = be16(b, pos + 2);
    if (len < 2 || pos + 2 + len > b.length) throw new ConvertError("corrupt");
    segments.push({ marker, start: pos, end: pos + 2 + len });
    pos += 2 + len;
  }
  throw new ConvertError("corrupt"); // never reached a scan
}

const JPEG_DROP = new Set([0xe1, 0xed, 0xfe]);

export function stripJpeg(b: Uint8Array): Uint8Array {
  const { segments, tail } = jpegSegments(b);
  const parts: Uint8Array[] = [b.subarray(0, 2)];
  for (const s of segments) if (!JPEG_DROP.has(s.marker)) parts.push(b.subarray(s.start, s.end));
  parts.push(b.subarray(tail));
  return concat(parts);
}

/**
 * MPO / Ultra HDR JPEGs carry a second complete JPEG (gain map, preview) after the first EOI. It can hold its own
 * EXIF/XMP, and a lossless strip copies everything after the scan verbatim, so such files must be re-encoded instead.
 * (Inside entropy-coded data 0xFF is always followed by 0x00, so FF D9 FF D8 FF cannot occur by accident.)
 */
export function hasSecondJpeg(b: Uint8Array): boolean {
  for (let i = 0; i + 4 < b.length; i++) {
    if (b[i] === 0xff && b[i + 1] === 0xd9 && b[i + 2] === 0xff && b[i + 3] === 0xd8 && b[i + 4] === 0xff) return true;
  }
  return false;
}

function jpegFindings(b: Uint8Array): string[] {
  const { segments } = jpegSegments(b);
  const found: string[] = [];
  if (hasSecondJpeg(b)) found.push("embedded-image");
  for (const s of segments) {
    if (s.marker === 0xe1) {
      const id = ascii(b, s.start + 4, Math.min(s.end, s.start + 33));
      found.push(id.startsWith("Exif") ? "exif" : id.startsWith("http://ns.adobe.com/xap") ? "xmp" : "app1");
    } else if (s.marker === 0xed) found.push("iptc");
    else if (s.marker === 0xfe) found.push("comment");
  }
  return found;
}

// ---- PNG ----------------------------------------------------------------------------------------------------------

const PNG_DROP = new Set(["eXIf", "tEXt", "iTXt", "zTXt", "tIME"]);

interface PngChunk {
  type: string;
  start: number;
  end: number;
  dataStart: number;
  dataEnd: number;
}

function pngChunks(b: Uint8Array): PngChunk[] {
  if (sniffKind(b) !== "png") throw new ConvertError("corrupt");
  const chunks: PngChunk[] = [];
  let pos = 8;
  while (pos + 12 <= b.length) {
    const len = be32(b, pos);
    const type = ascii(b, pos + 4, pos + 8);
    const end = pos + 12 + len;
    if (end > b.length) throw new ConvertError("corrupt");
    chunks.push({ type, start: pos, end, dataStart: pos + 8, dataEnd: pos + 8 + len });
    pos = end;
    if (type === "IEND") return chunks;
  }
  throw new ConvertError("corrupt"); // no IEND
}

export function stripPng(b: Uint8Array): Uint8Array {
  const chunks = pngChunks(b);
  const parts: Uint8Array[] = [b.subarray(0, 8)];
  for (const c of chunks) if (!PNG_DROP.has(c.type)) parts.push(b.subarray(c.start, c.end));
  return concat(parts);
}

function pngFindings(b: Uint8Array): string[] {
  const names: Record<string, string> = { eXIf: "exif", tEXt: "text", iTXt: "text", zTXt: "text", tIME: "time" };
  return pngChunks(b).filter((c) => PNG_DROP.has(c.type)).map((c) => names[c.type]);
}

export interface PngTextEntry {
  keyword: string;
  text: string;
}

const latin1 = (b: Uint8Array) => ascii(b, 0, b.length);

/** tEXt / iTXt / zTXt entries of a PNG (compressed ones are reported as "(compressed)"). Never throws. */
export function readPngText(b: Uint8Array): PngTextEntry[] {
  const out: PngTextEntry[] = [];
  try {
    for (const c of pngChunks(b)) {
      const d = b.subarray(c.dataStart, c.dataEnd);
      const nul = d.indexOf(0);
      if (nul < 1) continue;
      const keyword = latin1(d.subarray(0, nul)).slice(0, 80);
      if (c.type === "tEXt") {
        out.push({ keyword, text: latin1(d.subarray(nul + 1)).slice(0, 1000) });
      } else if (c.type === "zTXt") {
        out.push({ keyword, text: "(compressed)" });
      } else if (c.type === "iTXt") {
        const compressed = d[nul + 1] === 1;
        // keyword\0 flag method lang\0 translated\0 text
        const langEnd = d.indexOf(0, nul + 3);
        const transEnd = langEnd === -1 ? -1 : d.indexOf(0, langEnd + 1);
        if (transEnd === -1) continue;
        out.push({ keyword, text: compressed ? "(compressed)" : new TextDecoder().decode(d.subarray(transEnd + 1)).slice(0, 1000) });
      }
    }
  } catch {
    /* partial is fine */
  }
  return out;
}

// ---- WebP ---------------------------------------------------------------------------------------------------------

const WEBP_DROP = new Set(["EXIF", "XMP "]);

interface WebpChunk {
  type: string;
  start: number;
  /** end including the pad byte */
  end: number;
  dataStart: number;
  dataEnd: number;
}

function webpChunks(b: Uint8Array): WebpChunk[] {
  if (sniffKind(b) !== "webp") throw new ConvertError("corrupt");
  const declared = le32(b, 4) + 8;
  const limit = Math.min(declared, b.length);
  const chunks: WebpChunk[] = [];
  let pos = 12;
  while (pos + 8 <= limit) {
    const type = ascii(b, pos, pos + 4);
    const len = le32(b, pos + 4);
    const dataStart = pos + 8;
    const dataEnd = dataStart + len;
    if (dataEnd > b.length) throw new ConvertError("corrupt");
    const end = Math.min(dataEnd + (len & 1), b.length);
    chunks.push({ type, start: pos, end, dataStart, dataEnd });
    pos = end;
  }
  return chunks;
}

export function stripWebp(b: Uint8Array): Uint8Array {
  const chunks = webpChunks(b);
  const parts: Uint8Array[] = [];
  for (const c of chunks) {
    if (WEBP_DROP.has(c.type)) continue;
    if (c.type === "VP8X" && c.dataEnd - c.dataStart >= 10) {
      const copy = b.slice(c.start, c.end);
      copy[8] &= ~(0x08 | 0x04); // EXIF, XMP flags
      parts.push(copy);
    } else {
      parts.push(b.subarray(c.start, c.end));
    }
  }
  const body = concat(parts);
  const out = new Uint8Array(12 + body.length);
  out.set(b.subarray(0, 12));
  out.set(body, 12);
  new DataView(out.buffer).setUint32(4, out.length - 8, true);
  return out;
}

function webpFindings(b: Uint8Array): string[] {
  const found: string[] = [];
  for (const c of webpChunks(b)) {
    if (c.type === "EXIF") found.push("exif");
    else if (c.type === "XMP ") found.push("xmp");
    else if (c.type === "VP8X" && c.dataEnd - c.dataStart >= 10) {
      if (b[c.dataStart] & 0x08) found.push("exif-flag");
      if (b[c.dataStart] & 0x04) found.push("xmp-flag");
    }
  }
  return found;
}

// ---- dispatch + verifier --------------------------------------------------------------------------------------------

/** What metadata is still present (empty = clean). Throws ConvertError("corrupt") on structurally broken input. */
export function findMetadata(bytes: Uint8Array, kind?: MetaKind): string[] {
  const k = kind ?? sniffKind(bytes);
  if (k === "jpeg") return jpegFindings(bytes);
  if (k === "png") return pngFindings(bytes);
  if (k === "webp") return webpFindings(bytes);
  throw new ConvertError("corrupt");
}

export function hasMetadata(bytes: Uint8Array, kind?: MetaKind): boolean {
  return findMetadata(bytes, kind).length > 0;
}

/** Lossless strip by container; throws ConvertError("corrupt") when the structure is broken or the format unknown. */
export function stripLossless(bytes: Uint8Array): { bytes: Uint8Array; kind: MetaKind } {
  const kind = sniffKind(bytes);
  if (kind === "jpeg") return { bytes: stripJpeg(bytes), kind };
  if (kind === "png") return { bytes: stripPng(bytes), kind };
  if (kind === "webp") return { bytes: stripWebp(bytes), kind };
  throw new ConvertError("corrupt");
}

/**
 * A lossless strip would remove the EXIF Orientation tag and so change how the photo displays. When this is true the
 * caller must re-encode through a canvas instead (the decoder bakes the orientation into the pixels).
 */
export function needsReencodeForOrientation(bytes: Uint8Array): boolean {
  return hasNonTrivialOrientation(bytes);
}

/** True when a lossless strip is not safe: orientation would be lost, or a second embedded JPEG would survive. */
export function needsReencode(bytes: Uint8Array): boolean {
  return hasNonTrivialOrientation(bytes) || (sniffKind(bytes) === "jpeg" && hasSecondJpeg(bytes));
}
