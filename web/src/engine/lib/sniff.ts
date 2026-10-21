// Guesses a file extension (and MIME type) from the first bytes. Used for decoded base64 and gz payloads.

import { isValidUtf8 } from "./text-decode";

function startsWith(b: Uint8Array, sig: readonly number[], offset = 0): boolean {
  if (b.length < offset + sig.length) return false;
  for (let i = 0; i < sig.length; i++) if (b[offset + i] !== sig[i]) return false;
  return true;
}

function ascii(b: Uint8Array, start: number, end: number): string {
  let s = "";
  for (let i = start; i < Math.min(end, b.length); i++) s += String.fromCharCode(b[i]);
  return s;
}

function looksLikeText(b: Uint8Array): boolean {
  if (b.length === 0) return false;
  const sample = b.subarray(0, Math.min(b.length, 64 * 1024));
  let body = sample;
  if (startsWith(sample, [0xef, 0xbb, 0xbf])) body = sample.subarray(3);
  for (let i = 0; i < body.length; i++) {
    const c = body[i];
    if (c < 0x20 && c !== 0x09 && c !== 0x0a && c !== 0x0d && c !== 0x0c) return false;
    if (c === 0x7f) return false;
  }
  // The sample may end in the middle of a multi-byte sequence: drop up to 3 trailing bytes if that makes it valid.
  if (isValidUtf8(body)) return true;
  if (sample.length < b.length) {
    for (let cut = 1; cut <= 3; cut++) if (isValidUtf8(body.subarray(0, body.length - cut))) return true;
  }
  return false;
}

/** Lower-case extension without dot; "txt" for valid UTF-8 text, "bin" when nothing matches. */
export function sniffExtension(bytes: Uint8Array): string {
  const b = bytes;
  if (b.length === 0) return "bin";
  if (startsWith(b, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])) return "png";
  if (startsWith(b, [0xff, 0xd8, 0xff])) return "jpg";
  if (ascii(b, 0, 6) === "GIF87a" || ascii(b, 0, 6) === "GIF89a") return "gif";
  if (ascii(b, 0, 4) === "RIFF") {
    const kind = ascii(b, 8, 12);
    if (kind === "WEBP") return "webp";
    if (kind === "WAVE") return "wav";
    if (kind === "AVI ") return "avi";
  }
  if (startsWith(b, [0x00, 0x00, 0x01, 0x00]) && b.length >= 6 && (b[4] | (b[5] << 8)) > 0 && (b[4] | (b[5] << 8)) <= 256) return "ico";
  if (b[0] === 0x42 && b[1] === 0x4d && b.length >= 18 && b[6] === 0 && b[7] === 0 && b[8] === 0 && b[9] === 0 && [12, 40, 52, 56, 64, 108, 124].includes(b[14])) {
    return "bmp";
  }
  if (ascii(b, 0, 5) === "%PDF-") return "pdf";
  if (startsWith(b, [0x50, 0x4b, 0x03, 0x04]) || startsWith(b, [0x50, 0x4b, 0x05, 0x06])) return "zip";
  if (startsWith(b, [0x1f, 0x8b, 0x08])) return "gz";
  if (startsWith(b, [0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c])) return "7z";
  if (ascii(b, 0, 7) === "Rar!\x1a\x07\x00" || ascii(b, 0, 8) === "Rar!\x1a\x07\x01\x00") return "rar";
  if (ascii(b, 257, 262) === "ustar") return "tar";
  if (ascii(b, 0, 4) === "OggS") return "ogg";
  if (ascii(b, 0, 4) === "fLaC") return "flac";
  if (ascii(b, 4, 8) === "ftyp") {
    const brand = ascii(b, 8, 12);
    if (brand === "qt  ") return "mov";
    if (brand === "M4A " || brand === "M4B ") return "m4a";
    return "mp4";
  }
  if (startsWith(b, [0x1a, 0x45, 0xdf, 0xa3])) {
    return ascii(b, 0, Math.min(b.length, 4096)).includes("webm") ? "webm" : "mkv";
  }
  // UTF-16 text with a BOM is text too (checked before MP3 because FF FE also looks like a frame sync).
  if ((startsWith(b, [0xff, 0xfe]) || startsWith(b, [0xfe, 0xff])) && b.length % 2 === 0 && b.length >= 4) {
    const body = b.subarray(2);
    let controls = 0;
    for (let i = 0; i < Math.min(body.length, 2048); i += 2) {
      const c = startsWith(b, [0xff, 0xfe]) ? body[i] | (body[i + 1] << 8) : (body[i] << 8) | body[i + 1];
      if (c < 0x20 && c !== 0x09 && c !== 0x0a && c !== 0x0d) controls++;
    }
    if (controls === 0) return "txt";
  }
  if (ascii(b, 0, 3) === "ID3") return "mp3";
  if (b[0] === 0xff && (b[1] & 0xe0) === 0xe0 && (b[1] & 0x06) !== 0x00 && (b[1] & 0x18) !== 0x08) return "mp3";
  if (looksLikeText(b)) return "txt";
  return "bin";
}

const MIME: Record<string, string> = {
  png: "image/png",
  jpg: "image/jpeg",
  jpeg: "image/jpeg",
  gif: "image/gif",
  webp: "image/webp",
  avif: "image/avif",
  bmp: "image/bmp",
  ico: "image/x-icon",
  svg: "image/svg+xml",
  pdf: "application/pdf",
  zip: "application/zip",
  gz: "application/gzip",
  tar: "application/x-tar",
  "7z": "application/x-7z-compressed",
  rar: "application/vnd.rar",
  mp3: "audio/mpeg",
  m4a: "audio/mp4",
  wav: "audio/wav",
  ogg: "audio/ogg",
  opus: "audio/ogg",
  flac: "audio/flac",
  aac: "audio/aac",
  mp4: "video/mp4",
  mov: "video/quicktime",
  webm: "video/webm",
  mkv: "video/x-matroska",
  avi: "video/x-msvideo",
  txt: "text/plain;charset=utf-8",
  md: "text/markdown;charset=utf-8",
  csv: "text/csv;charset=utf-8",
  json: "application/json",
  html: "text/html;charset=utf-8",
  htm: "text/html;charset=utf-8",
  xml: "application/xml",
  srt: "text/plain;charset=utf-8",
  vtt: "text/vtt;charset=utf-8",
  docx: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
  xlsx: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
  pptx: "application/vnd.openxmlformats-officedocument.presentationml.presentation",
  bin: "application/octet-stream",
};

/** MIME type for a (lower-case, dot-less) extension; "application/octet-stream" when unknown. */
export function mimeOfExt(ext: string): string {
  return MIME[ext.toLowerCase()] ?? "application/octet-stream";
}
