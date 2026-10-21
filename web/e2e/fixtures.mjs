// Real input files for the e2e, written by the test itself (no binary fixtures in the repo).
import { deflateSync } from "node:zlib";
import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { PDFDocument, StandardFonts, rgb } from "pdf-lib";
import { zipSync, strToU8 } from "fflate";

function crc32(buf) {
  let c;
  let crc = 0xffffffff;
  for (let i = 0; i < buf.length; i++) {
    c = (crc ^ buf[i]) & 0xff;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    crc = (crc >>> 8) ^ c;
  }
  return (crc ^ 0xffffffff) >>> 0;
}

/** A valid RGBA PNG with a diagonal gradient and a solid red 8x8 block in the top-left corner. */
export function makePng(w = 96, h = 64) {
  const raw = Buffer.alloc((w * 4 + 1) * h);
  for (let y = 0; y < h; y++) {
    raw[y * (w * 4 + 1)] = 0; // filter: none
    for (let x = 0; x < w; x++) {
      const o = y * (w * 4 + 1) + 1 + x * 4;
      const red = x < 8 && y < 8;
      raw[o] = red ? 255 : Math.round((x / w) * 255);
      raw[o + 1] = red ? 0 : Math.round((y / h) * 255);
      raw[o + 2] = red ? 0 : 128;
      raw[o + 3] = 255;
    }
  }
  const chunk = (type, data) => {
    const t = Buffer.from(type);
    const len = Buffer.alloc(4);
    len.writeUInt32BE(data.length);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(Buffer.concat([t, data])));
    return Buffer.concat([len, t, data, crc]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0);
  ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8;
  ihdr[9] = 6; // RGBA
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk("IHDR", ihdr), chunk("IDAT", deflateSync(raw)), chunk("IEND", Buffer.alloc(0))]);
}

/** Inserts an Exif APP1 segment (Make/Model + GPS 25°N 55°E style coordinates) right after SOI of a JPEG. */
export function addExifGps(jpeg) {
  const le = (n, bytes) => {
    const b = Buffer.alloc(bytes);
    if (bytes === 2) b.writeUInt16LE(n);
    else b.writeUInt32LE(n);
    return b;
  };
  const entry = (tag, type, count, value) => Buffer.concat([le(tag, 2), le(type, 2), le(count, 4), value]);
  const ascii = (s) => Buffer.from(s + "\0");
  // Layout: header(8) | IFD0 | data...
  // IFD0: Make(0x010F), Model(0x0110), GPS IFD pointer (0x8825)
  const make = ascii("DropFlowCam");
  const model = ascii("Test-1");
  const ifd0Count = 3;
  const ifd0Size = 2 + ifd0Count * 12 + 4;
  const makeOff = 8 + ifd0Size;
  const modelOff = makeOff + make.length;
  const gpsIfdOff = modelOff + model.length;
  const ifd0 = Buffer.concat([
    le(ifd0Count, 2),
    entry(0x010f, 2, make.length, le(makeOff, 4)),
    entry(0x0110, 2, model.length, le(modelOff, 4)),
    entry(0x8825, 4, 1, le(gpsIfdOff, 4)),
    le(0, 4),
  ]);
  // GPS IFD: LatRef, Lat, LonRef, Lon
  const gpsCount = 4;
  const gpsSize = 2 + gpsCount * 12 + 4;
  const latOff = gpsIfdOff + gpsSize;
  const lonOff = latOff + 24;
  const rat = (a, b) => Buffer.concat([le(a, 4), le(b, 4)]);
  const gps = Buffer.concat([
    le(gpsCount, 2),
    entry(1, 2, 2, Buffer.from("N\0\0\0")),
    entry(2, 5, 3, le(latOff, 4)),
    entry(3, 2, 2, Buffer.from("E\0\0\0")),
    entry(4, 5, 3, le(lonOff, 4)),
    le(0, 4),
  ]);
  const lat = Buffer.concat([rat(25, 1), rat(12, 1), rat(0, 1)]);
  const lon = Buffer.concat([rat(55, 1), rat(16, 1), rat(30, 1)]);
  const tiff = Buffer.concat([Buffer.from("II*\0"), le(8, 4), ifd0, make, model, gps, lat, lon]);
  const payload = Buffer.concat([Buffer.from("Exif\0\0"), tiff]);
  const seg = Buffer.concat([Buffer.from([0xff, 0xe1]), Buffer.from([(payload.length + 2) >> 8, (payload.length + 2) & 0xff]), payload]);
  return Buffer.concat([jpeg.subarray(0, 2), seg, jpeg.subarray(2)]);
}

export async function makePdf(pages = 3, label = "Page") {
  const doc = await PDFDocument.create();
  const font = await doc.embedFont(StandardFonts.Helvetica);
  for (let i = 1; i <= pages; i++) {
    const p = doc.addPage([300, 200]);
    p.drawText(`${label} ${i} of ${pages}`, { x: 30, y: 100, size: 20, font, color: rgb(0, 0, 0) });
  }
  return Buffer.from(await doc.save());
}

/** One page with thousands of coloured rectangles: a large, uncompressed content stream that JPEG-rebuild shrinks a lot. */
export async function makeBigPdf() {
  const doc = await PDFDocument.create();
  const page = doc.addPage([595, 842]);
  let seed = 7;
  const rnd = () => ((seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff);
  for (let i = 0; i < 6000; i++) {
    page.drawRectangle({ x: rnd() * 560, y: rnd() * 800, width: 8 + rnd() * 30, height: 8 + rnd() * 30, color: rgb(rnd(), rnd(), rnd()), opacity: 0.6 });
  }
  return Buffer.from(await doc.save({ useObjectStreams: false }));
}

/** 16-bit mono PCM WAV with a 440 Hz sine. */
export function makeWav(seconds = 1, rate = 22050) {
  const n = Math.round(seconds * rate);
  const buf = Buffer.alloc(44 + n * 2);
  buf.write("RIFF", 0);
  buf.writeUInt32LE(36 + n * 2, 4);
  buf.write("WAVEfmt ", 8);
  buf.writeUInt32LE(16, 16);
  buf.writeUInt16LE(1, 20);
  buf.writeUInt16LE(1, 22);
  buf.writeUInt32LE(rate, 24);
  buf.writeUInt32LE(rate * 2, 28);
  buf.writeUInt16LE(2, 32);
  buf.writeUInt16LE(16, 34);
  buf.write("data", 36);
  buf.writeUInt32LE(n * 2, 40);
  for (let i = 0; i < n; i++) buf.writeInt16LE(Math.round(Math.sin((2 * Math.PI * 440 * i) / rate) * 12000), 44 + i * 2);
  return buf;
}

export const ARABIC_SRT = "﻿1\r\n00:00:01,000 --> 00:00:02,500\r\nمرحباً بالعالم\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nDropFlow سريع وخاص\r\n";

export function writeAll(dir) {
  mkdirSync(dir, { recursive: true });
  const p = (name) => join(dir, name);
  return { p };
}

export function makeZip() {
  return Buffer.from(zipSync({ "docs/hello.txt": strToU8("hello zip"), "تقرير.txt": strToU8("مرحبا"), "a.bin": new Uint8Array([1, 2, 3, 4]) }));
}
