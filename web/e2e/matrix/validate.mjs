// SPDX-License-Identifier: GPL-3.0-or-later
// Independent checks of what the web app produced (never our own code): FFmpeg/ffprobe decode pictures and media,
// pdf-lib reads PDF structure, pdf.js (Node legacy build) reads PDF text, fflate lists ZIP entries, Node crypto hashes.
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { PDFDocument, PDFName, PDFRawStream } from "pdf-lib";
import { unzipSync } from "fflate";
import { TOOLS } from "./samples.mjs";

let tmpDir = null;
let tmpN = 0;
export function setTmp(dir) {
  tmpDir = dir;
  mkdirSync(dir, { recursive: true });
}
function tmpFile(bytes, ext) {
  const p = join(tmpDir, `v${++tmpN}.${ext || "bin"}`);
  writeFileSync(p, bytes);
  return p;
}

// ---------------------------------------------------------------- magic bytes

export function sniff(buf) {
  const a = (s, e) => buf.subarray(s, e).toString("latin1");
  if (buf.length < 4) return "tiny";
  if (buf[0] === 0x89 && a(1, 4) === "PNG") return "png";
  if (buf[0] === 0xff && buf[1] === 0xd8) return "jpg";
  if (a(0, 4) === "RIFF" && a(8, 12) === "WEBP") return "webp";
  if (a(0, 4) === "RIFF" && a(8, 12) === "WAVE") return "wav";
  if (a(0, 4) === "RIFF" && a(8, 12) === "AVI ") return "avi";
  if (a(4, 8) === "ftyp") {
    const brand = a(8, 12);
    if (/avif|avis/.test(brand)) return "avif";
    if (/heic|heix|mif1/.test(brand)) return "heic";
    if (/M4A /.test(brand)) return "m4a";
    if (/qt {2}/.test(brand)) return "mov";
    return "mp4";
  }
  if (a(0, 2) === "BM") return "bmp";
  if (a(0, 3) === "GIF") return "gif";
  if (a(0, 4) === "II*\0" || a(0, 4) === "MM\0*") return "tiff";
  if (buf[0] === 0 && buf[1] === 0 && buf[2] === 1 && buf[3] === 0) return "ico";
  if (a(0, 5) === "%PDF-") return "pdf";
  if (a(0, 4) === "PK\x03\x04") return "zip";
  if (buf[0] === 0x1f && buf[1] === 0x8b) return "gz";
  if (buf[0] === 0xff && (buf[1] & 0xf6) === 0xf0) return "aac"; // ADTS: sync 0xFFF, layer 00
  if (a(0, 3) === "ID3" || (buf[0] === 0xff && (buf[1] & 0xe0) === 0xe0 && (buf[1] & 0x06) !== 0)) return "mp3";
  if (a(0, 4) === "fLaC") return "flac";
  if (a(0, 4) === "OggS") return buf.includes(Buffer.from("OpusHead")) ? "opus" : "ogg";
  if (a(0, 4) === "FORM" && /AIF[FC]/.test(a(8, 12))) return "aiff";
  if (buf.subarray(0, 4).equals(Buffer.from([0x30, 0x26, 0xb2, 0x75]))) return "asf";
  if (buf.subarray(0, 4).equals(Buffer.from([0x1a, 0x45, 0xdf, 0xa3]))) return buf.includes(Buffer.from("webm")) ? "webm" : "mkv";
  if (a(0, 3) === "FLV") return "flv";
  return "unknown";
}

/** Which sniffed kinds are a correct file for a target extension. */
export const KIND_OK = {
  png: ["png"], jpg: ["jpg"], webp: ["webp"], avif: ["avif"], bmp: ["bmp"], gif: ["gif"], tiff: ["tiff"], ico: ["ico"], pdf: ["pdf"],
  mp3: ["mp3"], m4a: ["m4a", "mp4"], wav: ["wav"], flac: ["flac"], ogg: ["ogg", "opus"], opus: ["opus"], aiff: ["aiff"], wma: ["asf"], aac: ["aac"],
  mp4: ["mp4"], mkv: ["mkv"], webm: ["webm"], mov: ["mov", "mp4"], avi: ["avi"], wmv: ["asf"], m4v: ["mp4"],
  zip: ["zip"],
};

// ---------------------------------------------------------------- ffprobe / pixels

export function probe(bytes, ext) {
  if (!TOOLS.ffprobe) return null;
  const p = tmpFile(bytes, ext);
  try {
    const out = execFileSync(TOOLS.ffprobe, ["-v", "error", "-show_streams", "-show_format", "-of", "json", p], { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
    const j = JSON.parse(out);
    const v = j.streams?.find((s) => s.codec_type === "video");
    const au = j.streams?.find((s) => s.codec_type === "audio");
    return {
      format: j.format?.format_name ?? "",
      duration: Number(j.format?.duration ?? v?.duration ?? au?.duration ?? NaN),
      video: v ? { codec: v.codec_name, w: v.width, h: v.height, frames: Number(v.nb_frames ?? NaN) } : null,
      audio: au ? { codec: au.codec_name, channels: au.channels, rate: Number(au.sample_rate) } : null,
      tags: j.format?.tags ?? {},
    };
  } catch (e) {
    return { error: String(e.stderr ?? e.message).slice(0, 200) };
  }
}

/** First frame as straight RGBA, decoded by FFmpeg (no autorotation: outputs must not rely on EXIF orientation). */
export function pixels(bytes, ext) {
  if (!TOOLS.ffmpeg) return null;
  const pr = probe(bytes, ext);
  if (!pr?.video) return null;
  const p = tmpFile(bytes, ext);
  const raw = execFileSync(TOOLS.ffmpeg, ["-v", "error", "-noautorotate", "-i", p, "-map", "0:v:0", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1"], { maxBuffer: 1 << 30, stdio: ["ignore", "pipe", "pipe"] });
  const { w, h } = pr.video;
  return {
    w,
    h,
    at(x, y) {
      const o = (Math.min(h - 1, Math.max(0, y)) * w + Math.min(w - 1, Math.max(0, x))) * 4;
      return [raw[o], raw[o + 1], raw[o + 2], raw[o + 3]];
    },
    count(pred) {
      let n = 0;
      for (let i = 0; i < w * h * 4; i += 4) if (pred(raw[i], raw[i + 1], raw[i + 2], raw[i + 3])) n++;
      return n;
    },
  };
}
export const isRed = ([r, g, b]) => r > 200 && g < 70 && b < 70;
export const isWhite = ([r, g, b, a]) => r > 235 && g > 235 && b > 235 && a > 235;
export const isBlack = ([r, g, b, a]) => r < 30 && g < 30 && b < 30 && a > 200;
export const isTransparent = (p) => p[3] < 20;

// ---------------------------------------------------------------- EXIF (JPEG APP1)

/** { orientation, tags:Set } of IFD0 of the first Exif APP1, or null. */
export function jpegExif(buf) {
  let p = 2;
  while (p + 4 < buf.length && buf[p] === 0xff) {
    const marker = buf[p + 1];
    const len = buf.readUInt16BE(p + 2);
    if (marker === 0xe1 && buf.subarray(p + 4, p + 10).toString("latin1") === "Exif\0\0") {
      const t = p + 10;
      const le = buf.subarray(t, t + 2).toString("latin1") === "II";
      const u16 = (o) => (le ? buf.readUInt16LE(t + o) : buf.readUInt16BE(t + o));
      const u32 = (o) => (le ? buf.readUInt32LE(t + o) : buf.readUInt32BE(t + o));
      const ifd = u32(4);
      const n = u16(ifd);
      const tags = new Set();
      let orientation = 1;
      for (let i = 0; i < n; i++) {
        const e = ifd + 2 + i * 12;
        const tag = u16(e);
        tags.add(tag);
        if (tag === 0x0112) orientation = u16(e + 8);
      }
      return { orientation, tags };
    }
    if (marker === 0xda) break;
    p += 2 + len;
  }
  return null;
}

/** Sizes of the entries of an ICO file. */
export function icoSizes(buf) {
  const n = buf.readUInt16LE(4);
  const out = [];
  for (let i = 0; i < n; i++) {
    const e = 6 + i * 16;
    out.push({ w: buf[e] || 256, h: buf[e + 1] || 256 });
  }
  return out;
}

// ---------------------------------------------------------------- PDF

let pdfjsP = null;
async function pdfjs() {
  pdfjsP ??= import("pdfjs-dist/legacy/build/pdf.mjs");
  return pdfjsP;
}

/** Text of every page with pdf.js (lines joined by spaces, NFC). */
export async function pdfPageTexts(bytes) {
  const lib = await pdfjs();
  const task = lib.getDocument({ data: new Uint8Array(bytes), isEvalSupported: false, useSystemFonts: false, disableFontFace: true, verbosity: 0 });
  const doc = await task.promise;
  const out = [];
  for (let i = 1; i <= doc.numPages; i++) {
    const page = await doc.getPage(i);
    const tc = await page.getTextContent();
    out.push(tc.items.map((it) => it.str ?? "").join(" ").normalize("NFC"));
  }
  await task.destroy();
  return out;
}

export async function pdfInfo(bytes) {
  const doc = await PDFDocument.load(bytes, { ignoreEncryption: true, updateMetadata: false });
  const pages = doc.getPages().map((p) => ({ w: p.getWidth(), h: p.getHeight(), rot: ((p.getRotation().angle % 360) + 360) % 360 }));
  let images = 0;
  for (const [, obj] of doc.context.enumerateIndirectObjects()) {
    if (obj instanceof PDFRawStream && obj.dict.get(PDFName.of("Subtype"))?.toString() === "/Image") images++;
  }
  return { count: pages.length, pages, images, title: doc.getTitle(), author: doc.getAuthor() };
}

// ---------------------------------------------------------------- text helpers

export const squash = (s) => s.normalize("NFC").replace(/[‎‏؜‪-‮⁦-⁩]/g, "").replace(/\s+/g, " ").trim();
export const PRESENTATION_FORMS = /[ﭐ-﷿ﹰ-﻿]/;
export function hasPhrase(text, phrase) {
  return squash(text).includes(squash(phrase));
}
/** Diagnoses a missing Arabic phrase: reversed? presentation forms? */
export function arabicDiagnosis(text, phrase) {
  const t = squash(text);
  if (t.includes([...squash(phrase)].reverse().join(""))) return "REVERSED (visual order)";
  if (PRESENTATION_FORMS.test(t) && t.normalize("NFKC").includes(squash(phrase))) return "presentation forms (only matches after NFKC)";
  if (/�|□/.test(t)) return "replacement characters / boxes";
  return "missing";
}
/** Share of `words` that occur in the (OCR) text. */
export function wordHit(text, words) {
  const t = squash(text).toLowerCase();
  const found = words.filter((w) => t.includes(w.toLowerCase()));
  return { share: found.length / words.length, found };
}

export const sha256 = (b) => createHash("sha256").update(b).digest("hex");
export function zipEntries(bytes) {
  return unzipSync(new Uint8Array(bytes));
}
