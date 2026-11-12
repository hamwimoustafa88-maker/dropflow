// SPDX-License-Identifier: GPL-3.0-or-later
// Real input files for the conversion matrix, all GENERATED here (nothing is downloaded):
//  - in headless Edge: canvas JPEG/WebP/text pictures, and PDFs printed by Chromium (Page.printToPDF, real embedded subset
//    fonts with a ToUnicode map, the way "Save as PDF" makes them) in Arabic and English;
//  - in Node: PNGs, EXIF segments, pdf-lib PDFs (scanned/image-only, rotated pages), subtitles in UTF-8 / UTF-16 /
//    windows-1256, ZIP / TAR / GZ archives, text files, a QR code (own encoder, ./qr.mjs);
//  - with the developer's FFmpeg / LibreOffice / qpdf when they are on this PC (audio, video, AVIF/BMP/GIF/TIFF/ICO,
//    a Writer-made Arabic PDF, an encrypted PDF). A missing program skips its samples VISIBLY (listed in `skipped`).
// Every sample carries `meta`: what a correct conversion must preserve (size, page count, words, duration...).
import { execFileSync, spawnSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { gzipSync, deflateSync } from "node:zlib";
import { PDFDocument, StandardFonts, degrees, rgb } from "pdf-lib";
import { strToU8, zipSync } from "fflate";
import { addExifGps } from "../fixtures.mjs";
import { qrMatrix } from "./qr.mjs";

export const AR = {
  hello: "مرحبا بالعالم",
  line: "هذا مستند تجريبي باللغة العربية",
  page1: "الصفحة الأولى",
  local: "الملفات تبقى على جهازك",
};
export const EN = { title: "Quarterly Report 2026", line: "DropFlow converts files on your device" };

// ---------------------------------------------------------------- tools on this PC

function findExe(name, extraDirs = []) {
  const r = spawnSync(process.platform === "win32" ? "where" : "which", [name], { encoding: "utf8" });
  if (r.status === 0) return r.stdout.split(/\r?\n/)[0].trim();
  for (const d of extraDirs) {
    if (!existsSync(d)) continue;
    for (const sub of ["", ...readdirSync(d).filter((x) => x.toLowerCase().startsWith(name))]) {
      for (const p of [join(d, sub, `${name}.exe`), join(d, sub, "bin", `${name}.exe`), join(d, sub, "program", `${name}.exe`)]) if (existsSync(p)) return p;
    }
  }
  return null;
}
export const TOOLS = {
  ffmpeg: findExe("ffmpeg"),
  ffprobe: findExe("ffprobe"),
  soffice: findExe("soffice", ["C:/Program Files/LibreOffice/program", "C:/Program Files"]),
  qpdf: findExe("qpdf", ["C:/Program Files"]),
};

// ---------------------------------------------------------------- small encoders

function crc32(buf) {
  let crc = 0xffffffff;
  for (let i = 0; i < buf.length; i++) {
    let c = (crc ^ buf[i]) & 0xff;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    crc = (crc >>> 8) ^ c;
  }
  return (crc ^ 0xffffffff) >>> 0;
}

/** RGBA PNG from a pixel function (x, y) -> [r, g, b, a]. */
export function pngFrom(w, h, px) {
  const raw = Buffer.alloc((w * 4 + 1) * h);
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const o = y * (w * 4 + 1) + 1 + x * 4;
      const [r, g, b, a] = px(x, y);
      raw[o] = r;
      raw[o + 1] = g;
      raw[o + 2] = b;
      raw[o + 3] = a;
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
  ihdr[9] = 6;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk("IHDR", ihdr), chunk("IDAT", deflateSync(raw)), chunk("IEND", Buffer.alloc(0))]);
}

/** Inserts an Exif APP1 with only an Orientation tag right after SOI. */
export function addExifOrientation(jpeg, orientation) {
  const ifd = Buffer.alloc(2 + 12 + 4);
  ifd.writeUInt16LE(1, 0);
  ifd.writeUInt16LE(0x0112, 2);
  ifd.writeUInt16LE(3, 4);
  ifd.writeUInt32LE(1, 6);
  ifd.writeUInt16LE(orientation, 10);
  const tiff = Buffer.concat([Buffer.from("II*\0"), Buffer.from([8, 0, 0, 0]), ifd]);
  const payload = Buffer.concat([Buffer.from("Exif\0\0"), tiff]);
  const len = payload.length + 2;
  return Buffer.concat([jpeg.subarray(0, 2), Buffer.from([0xff, 0xe1, len >> 8, len & 0xff]), payload, jpeg.subarray(2)]);
}

/** windows-1256 encoder built from the platform's own decoder (Node has full ICU). */
function encode1256(text) {
  const dec = new TextDecoder("windows-1256");
  const map = new Map();
  for (let i = 0; i < 256; i++) map.set(dec.decode(Uint8Array.of(i)), i);
  return Buffer.from([...text].map((ch) => {
    if (!map.has(ch)) throw new Error(`not in windows-1256: ${ch}`);
    return map.get(ch);
  }));
}

/** POSIX ustar archive (names in UTF-8, as GNU tar writes them in a UTF-8 locale). */
function makeTar(entries) {
  const blocks = [];
  for (const { name, data, dir } of entries) {
    const h = Buffer.alloc(512);
    const nb = Buffer.from(name, "utf8");
    if (nb.length > 100) {
      // split at a "/" into prefix (155) + name (100)
      const cut = name.lastIndexOf("/", 154);
      Buffer.from(name.slice(0, cut), "utf8").copy(h, 345);
      Buffer.from(name.slice(cut + 1), "utf8").copy(h, 0);
    } else nb.copy(h, 0);
    h.write(dir ? "0000755\0" : "0000644\0", 100);
    h.write("0000000\0", 108);
    h.write("0000000\0", 116);
    h.write((dir ? 0 : data.length).toString(8).padStart(11, "0") + "\0", 124);
    h.write(Math.floor(Date.UTC(2026, 0, 2) / 1000).toString(8).padStart(11, "0") + "\0", 136);
    h.write("        ", 148);
    h.write(dir ? "5" : "0", 156);
    h.write("ustar\0", 257);
    h.write("00", 263);
    let sum = 0;
    for (const x of h) sum += x;
    h.write(sum.toString(8).padStart(6, "0") + "\0 ", 148);
    blocks.push(h);
    if (!dir) {
      blocks.push(Buffer.from(data));
      const pad = (512 - (data.length % 512)) % 512;
      if (pad) blocks.push(Buffer.alloc(pad));
    }
  }
  blocks.push(Buffer.alloc(1024));
  return Buffer.concat(blocks);
}

/** gzip with an FNAME field (what `gzip file` writes). */
function gzipNamed(data, name) {
  const gz = gzipSync(data);
  // header: 1f 8b 08 FLG MTIME(4) XFL OS ; set FNAME flag and insert the zero-terminated name after the 10-byte header
  const head = Buffer.from(gz.subarray(0, 10));
  head[3] |= 0x08;
  return Buffer.concat([head, Buffer.from(name + "\0", "latin1"), gz.subarray(10)]);
}

// ---------------------------------------------------------------- browser helpers

async function browserBytes(b, js) {
  const b64 = await b.ev(`(async () => { const blob = await (${js})(); const u8 = new Uint8Array(await blob.arrayBuffer()); let s = ''; for (let i = 0; i < u8.length; i += 0x8000) s += String.fromCharCode(...u8.subarray(i, i + 0x8000)); return btoa(s); })()`);
  return Buffer.from(b64, "base64");
}

/** Canvas picture of text lines (black on white), drawn by the browser (it shapes Arabic correctly). */
function textPictureJs(lines, { w = 1100, h = 360, font = "bold 64px Arial", rtl = false, type = "image/png" } = {}) {
  return `async () => { const c = new OffscreenCanvas(${w}, ${h}); const x = c.getContext('2d'); x.fillStyle = '#fff'; x.fillRect(0, 0, ${w}, ${h});
    x.fillStyle = '#000'; x.font = ${JSON.stringify(font)}; x.direction = ${JSON.stringify(rtl ? "rtl" : "ltr")}; x.textAlign = ${JSON.stringify(rtl ? "right" : "left")}; x.textBaseline = 'middle';
    const lines = ${JSON.stringify(lines)}; lines.forEach((l, i) => x.fillText(l, ${rtl ? w - 50 : 50}, ${h} * (i + 1) / (lines.length + 1)));
    return c.convertToBlob({ type: ${JSON.stringify(type)}, quality: 0.92 }); }`;
}

/** Gradient photo with a red 16x16 block at the top-left (lets a test tell orientation apart). */
function photoJs(w, h, type, quality = 0.9, alpha = false) {
  return `async () => { const c = new OffscreenCanvas(${w}, ${h}); const x = c.getContext('2d');
    const g = x.createLinearGradient(0, 0, ${w}, ${h}); g.addColorStop(0, 'rgb(40,160,90)'); g.addColorStop(1, 'rgb(30,60,200)');
    x.fillStyle = g; x.fillRect(0, 0, ${w}, ${h});
    ${alpha ? `x.clearRect(0, 0, ${Math.floor(w / 2)}, ${h});` : ""}
    for (let i = 0; i < ${Math.round((w * h) / 4000)}; i++) { x.fillStyle = 'hsl(' + (i * 47 % 360) + ',70%,50%)'; x.fillRect((i * 7919) % ${w}, (i * 104729) % ${h}, 9, 9); }
    x.fillStyle = 'rgb(255,0,0)'; x.fillRect(0, 0, 16, 16);
    return c.convertToBlob({ type: ${JSON.stringify(type)}, quality: ${quality} }); }`;
}

async function printPdf(b, html) {
  await b.goto("data:text/html;charset=utf-8," + encodeURIComponent(html), 900);
  await b.ev("document.fonts.ready.then(() => true)");
  const r = await b.send("Page.printToPDF", { printBackground: true, preferCSSPageSize: true, displayHeaderFooter: false });
  if (!r.result?.data) throw new Error("printToPDF failed: " + JSON.stringify(r.error ?? {}));
  return Buffer.from(r.result.data, "base64");
}

const PAGE_CSS = "@page { size: A4; margin: 18mm } body { font-family: Arial, sans-serif; font-size: 16pt } h1 { font-size: 28pt } .pb { break-after: page }";

// ---------------------------------------------------------------- the samples

export async function makeMatrixSamples(b, dir) {
  rmSync(dir, { recursive: true, force: true });
  mkdirSync(dir, { recursive: true });
  const samples = [];
  const skipped = [];
  const add = (name, data, kind, meta = {}) => {
    const p = join(dir, name);
    writeFileSync(p, data);
    samples.push({ id: name, name, path: p, kind, meta });
    return p;
  };
  const ff = (args) => execFileSync(TOOLS.ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", ...args], { stdio: ["ignore", "ignore", "pipe"] });
  const ffSample = (name, args, kind, meta) => {
    if (!TOOLS.ffmpeg) return skipped.push(`${name} (no ffmpeg on this PC)`);
    const p = join(dir, name);
    try {
      ff([...args, p]);
      samples.push({ id: name, name, path: p, kind, meta });
    } catch (e) {
      skipped.push(`${name} (ffmpeg failed: ${String(e.stderr ?? e).slice(0, 120)})`);
    }
  };

  await b.goto("about:blank", 300);

  // ---- images
  add("alpha.png", pngFrom(120, 80, (x, y) => (x < 60 ? [0, 0, 0, 0] : x >= 112 && y < 8 ? [255, 0, 0, 255] : [0, 0, 255, 255])), "image", { w: 120, h: 80, alpha: true });
  const jpg = await browserBytes(b, photoJs(160, 100, "image/jpeg"));
  add("photo.jpg", jpg, "image", { w: 160, h: 100, redTopLeft: true });
  add("exif-rotate-90.jpg", addExifOrientation(jpg, 6), "image", { w: 100, h: 160, redTopRight: true, note: "pixels 160x100, EXIF Orientation 6" });
  add("exif-rotate-180.jpg", addExifOrientation(jpg, 3), "image", { w: 160, h: 100, redBottomRight: true, note: "EXIF Orientation 3" });
  add("geo.jpg", addExifGps(jpg), "image", { w: 160, h: 100, gps: true });
  add("big.jpg", await browserBytes(b, photoJs(3000, 2000, "image/jpeg", 0.95)), "image", { w: 3000, h: 2000 });
  add("small-16.png", pngFrom(16, 16, (x, y) => [x * 16, y * 16, 128, 255]), "image", { w: 16, h: 16 });
  add("صورة عربية.png", pngFrom(96, 64, (x, y) => [x * 2, y * 3, 90, 255]), "image", { w: 96, h: 64 });
  add("alpha.webp", await browserBytes(b, photoJs(128, 96, "image/webp", 0.9, true)), "image", { w: 128, h: 96, alpha: true });
  ffSample("sample.avif", ["-f", "lavfi", "-i", "testsrc2=size=64x48:rate=1", "-frames:v", "1", "-c:v", "libaom-av1", "-still-picture", "1"], "image", { w: 64, h: 48 });
  ffSample("sample.bmp", ["-f", "lavfi", "-i", "testsrc2=size=50x30:rate=1", "-frames:v", "1", "-pix_fmt", "bgr24"], "image", { w: 50, h: 30 });
  ffSample("anim.gif", ["-f", "lavfi", "-i", "testsrc2=size=40x40:rate=3", "-t", "1"], "image", { w: 40, h: 40, animated: true });
  ffSample("sample.tiff", ["-f", "lavfi", "-i", "testsrc2=size=70x50:rate=1", "-frames:v", "1", "-pix_fmt", "rgb24"], "image", { w: 70, h: 50 });
  ffSample("icon.ico", ["-f", "lavfi", "-i", "testsrc2=size=32x32:rate=1", "-frames:v", "1"], "image", { w: 32, h: 32 });
  add("sized.svg", Buffer.from(`<svg xmlns="http://www.w3.org/2000/svg" width="200" height="100"><rect width="200" height="100" fill="#3a7"/><circle cx="50" cy="50" r="30" fill="#f00"/><text x="190" y="60" font-family="Arial" font-size="20" text-anchor="end" direction="rtl">مرحبا</text></svg>`), "image", { w: 200, h: 100 });
  add("viewbox-only.svg", Buffer.from(`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 300 150"><rect width="300" height="150" fill="#28c"/><rect x="10" y="10" width="40" height="40" fill="#fff"/></svg>`), "image", { w: 1024, h: 512, note: "viewBox only: rasterised with the longest side 1024 (svgSize, by design)" });
  const repoHeic = new URL("../../../tests/samples/heic/RGB_8__128x128.heic", import.meta.url);
  if (existsSync(repoHeic)) {
    copyFileSync(repoHeic, join(dir, "sample.heic"));
    samples.push({ id: "sample.heic", name: "sample.heic", path: join(dir, "sample.heic"), kind: "image", meta: { w: 128, h: 128 } });
  } else skipped.push("sample.heic (repo fixture tests/samples/heic missing)");
  const ocrEn = await browserBytes(b, textPictureJs(["DropFlow 2026", "Local files only"]));
  add("ocr-english.png", ocrEn, "image", { w: 1100, h: 360, words: ["DropFlow", "2026", "Local", "files", "only"] });
  const ocrAr = await browserBytes(b, textPictureJs([AR.hello, "ملفات محلية فقط"], { font: "bold 72px Arial", rtl: true }));
  add("ocr-arabic.png", ocrAr, "image", { w: 1100, h: 360, words: ["مرحبا", "بالعالم", "ملفات", "محلية", "فقط"], arabic: true });
  const qrPng = (text, scale = 8) => {
    const m = qrMatrix(text);
    const q = 4;
    const n = (m.length + 2 * q) * scale;
    return pngFrom(n, n, (x, y) => {
      const mx = Math.floor(x / scale) - q;
      const my = Math.floor(y / scale) - q;
      const dark = mx >= 0 && my >= 0 && mx < m.length && my < m.length && m[my][mx];
      return dark ? [0, 0, 0, 255] : [255, 255, 255, 255];
    });
  };
  const qrSide = (t) => (qrMatrix(t).length + 8) * 8;
  add("qr-link.png", qrPng("https://dropflow.scouthub.dev"), "image", { qr: "https://dropflow.scouthub.dev", w: qrSide("https://dropflow.scouthub.dev"), h: qrSide("https://dropflow.scouthub.dev") });
  add("qr-arabic.png", qrPng("مرحبا DropFlow"), "image", { qr: "مرحبا DropFlow", w: qrSide("مرحبا DropFlow"), h: qrSide("مرحبا DropFlow") });

  // ---- PDFs
  add("arabic-chromium.pdf", await printPdf(b, `<!doctype html><html lang="ar" dir="rtl"><meta charset="utf-8"><style>${PAGE_CSS}</style><body><h1>${AR.hello}</h1><p>${AR.line}</p><p>${AR.page1} 1</p></body></html>`), "pdf", { pages: 1, phrases: [AR.hello, AR.line, AR.page1], arabic: true, producer: "Chromium" });
  add("english-chromium.pdf", await printPdf(b, `<!doctype html><html lang="en"><meta charset="utf-8"><style>${PAGE_CSS}</style><body><h1>${EN.title}</h1><p>${EN.line}.</p><ul><li>First point</li><li>Second point</li></ul></body></html>`), "pdf", { pages: 1, phrases: [EN.title, EN.line, "First point"], producer: "Chromium" });
  add("multipage-mixed.pdf", await printPdf(b, `<!doctype html><html><meta charset="utf-8"><style>${PAGE_CSS}</style><body>
    <section class="pb"><h1>Chapter one</h1><p>${EN.line}.</p></section>
    <section class="pb" dir="rtl" lang="ar"><h1>${AR.hello}</h1><p>${AR.local}</p></section>
    <section class="pb"><h1>Chapter three</h1><p>Mixed: DropFlow ${AR.hello} 2026</p></section>
    <section dir="rtl" lang="ar"><h1>${AR.page1}</h1><ol><li>البند الأول</li><li>البند الثاني</li></ol></section></body></html>`), "pdf", { pages: 4, phrases: ["Chapter one", AR.hello, AR.local, "Chapter three", AR.page1, "البند الأول"], arabic: true, pageOrder: ["Chapter one", AR.local, "Chapter three", "البند الثاني"], producer: "Chromium" });
  {
    const doc = await PDFDocument.create();
    for (const png of [ocrEn, ocrAr]) {
      const img = await doc.embedPng(png);
      const p = doc.addPage([595, 842]);
      p.drawImage(img, { x: 20, y: 500, width: 555, height: (555 * 360) / 1100 });
    }
    add("scanned-image-only.pdf", Buffer.from(await doc.save()), "pdf", { pages: 2, scanned: true, words: ["DropFlow", "2026", "Local", "مرحبا", "بالعالم"] });
  }
  {
    const doc = await PDFDocument.create();
    const font = await doc.embedFont(StandardFonts.Helvetica);
    const a = doc.addPage([842, 595]);
    a.drawText("Landscape page one", { x: 40, y: 300, size: 30, font, color: rgb(0, 0, 0) });
    const p2 = doc.addPage([595, 842]);
    p2.drawText("Rotated page two", { x: 40, y: 400, size: 30, font });
    p2.setRotation(degrees(90));
    const p3 = doc.addPage([612, 792]);
    p3.drawText("Letter page three", { x: 40, y: 400, size: 30, font });
    add("landscape-rotated.pdf", Buffer.from(await doc.save()), "pdf", { pages: 3, phrases: ["Landscape page one", "Rotated page two", "Letter page three"], pageOrder: ["Landscape page one", "Rotated page two", "Letter page three"], producer: "pdf-lib" });
  }
  if (TOOLS.soffice) {
    const work = join(dir, "..", "lo-work");
    rmSync(work, { recursive: true, force: true });
    mkdirSync(work, { recursive: true });
    const html = join(work, "arabic-writer.html");
    writeFileSync(html, `<!doctype html><html lang="ar" dir="rtl"><head><meta charset="utf-8"></head><body style="font-family: Arial"><h1>${AR.hello}</h1><p>${AR.line}</p><p>${AR.local}</p></body></html>`);
    const r = spawnSync(TOOLS.soffice, ["--headless", `-env:UserInstallation=file:///${work.replace(/\\/g, "/")}/profile`, "--convert-to", "pdf:writer_pdf_Export", "--outdir", work, html], { encoding: "utf8", timeout: 120000 });
    const out = join(work, "arabic-writer.pdf");
    if (existsSync(out)) add("arabic-libreoffice.pdf", readFileSync(out), "pdf", { pages: 1, phrases: [AR.hello, AR.line, AR.local], arabic: true, producer: "LibreOffice Writer" });
    else skipped.push(`arabic-libreoffice.pdf (soffice failed: ${(r.stderr || r.error || "").toString().slice(0, 120)})`);
  } else skipped.push("arabic-libreoffice.pdf (no LibreOffice on this PC)");
  if (TOOLS.qpdf) {
    const plain = join(dir, "..", "plain-for-qpdf.pdf");
    writeFileSync(plain, readFileSync(samples.find((s) => s.id === "english-chromium.pdf").path));
    const out = join(dir, "encrypted.pdf");
    const r = spawnSync(TOOLS.qpdf, ["--encrypt", "user123", "owner123", "256", "--", plain, out], { encoding: "utf8" });
    if (existsSync(out)) samples.push({ id: "encrypted.pdf", name: "encrypted.pdf", path: out, kind: "pdf", meta: { pages: 1, encrypted: true } });
    else skipped.push(`encrypted.pdf (qpdf failed: ${r.stderr})`);
  } else skipped.push("encrypted.pdf (no qpdf on this PC)");

  // ---- audio (2 s stereo tone, Arabic title tag)
  const tone = ["-f", "lavfi", "-i", "sine=frequency=440:duration=2:sample_rate=44100", "-f", "lavfi", "-i", "sine=frequency=660:duration=2:sample_rate=44100", "-filter_complex", "[0][1]amerge=inputs=2", "-metadata", "title=نغمة تجريبية"];
  for (const [ext, codec] of [["mp3", ["-c:a", "libmp3lame"]], ["m4a", ["-c:a", "aac"]], ["wav", ["-c:a", "pcm_s16le"]], ["flac", ["-c:a", "flac"]], ["ogg", ["-c:a", "libvorbis"]], ["opus", ["-c:a", "libopus"]], ["aiff", ["-c:a", "pcm_s16be"]], ["wma", ["-c:a", "wmav2"]], ["aac", ["-c:a", "aac", "-f", "adts"]]]) {
    ffSample(`tone.${ext}`, [...tone, ...codec], "audio", { duration: 2, channels: 2 });
  }
  // ---- video (2 s 160x120 test pattern + tone)
  const vid = ["-f", "lavfi", "-i", "testsrc2=size=160x120:rate=15:duration=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2:sample_rate=44100", "-shortest"];
  for (const [name, codec] of [
    ["clip.mp4", ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac"]],
    ["clip.mov", ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac"]],
    ["clip.mkv", ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "libopus"]],
    ["clip.webm", ["-c:v", "libvpx-vp9", "-b:v", "200k", "-c:a", "libopus"]],
    ["clip.avi", ["-c:v", "mpeg4", "-c:a", "libmp3lame"]],
    ["clip.wmv", ["-c:v", "wmv2", "-c:a", "wmav2"]],
    ["clip.flv", ["-c:v", "flv", "-c:a", "libmp3lame", "-ar", "44100"]],
    ["clip.m4v", ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-f", "ipod"]],
  ]) ffSample(name, [...vid, ...codec], "video", { duration: 2, w: 160, h: 120, audio: true });
  // a high-bitrate clip, so "Compress" has something to compress (the 160x120 clips above are already tiny)
  ffSample("camera-hq.mp4", ["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=3", "-f", "lavfi", "-i", "sine=frequency=500:duration=3:sample_rate=48000", "-shortest", "-c:v", "libx264", "-preset", "ultrafast", "-qp", "8", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "256k", "-ac", "2"], "video", { duration: 3, w: 640, h: 360, audio: true, compressible: true });
  ffSample("silent-portrait.mp4", ["-f", "lavfi", "-i", "testsrc2=size=120x160:rate=15:duration=2", "-c:v", "libx264", "-pix_fmt", "yuv420p"], "video", { duration: 2, w: 120, h: 160, audio: false });

  // ---- subtitles
  const cues = [["00:00:01,000", "00:00:02,500", AR.hello], ["00:00:03,000", "00:00:04,200", "DropFlow سريع وخاص"], ["00:00:05,000", "00:00:06,000", "سطر أول\nسطر ثان"]];
  const srt = cues.map(([a, z, t], i) => `${i + 1}\r\n${a} --> ${z}\r\n${t.replace(/\n/g, "\r\n")}\r\n`).join("\r\n");
  const subMeta = { cues: 3, words: [AR.hello, "DropFlow سريع وخاص", "سطر أول", "سطر ثان"], times: ["00:00:01", "00:00:03", "00:00:05"] };
  add("arabic-utf8-bom.srt", Buffer.from("﻿" + srt, "utf8"), "subtitle", subMeta);
  add("arabic-windows1256.srt", encode1256(srt), "subtitle", { ...subMeta, encoding: "windows-1256" });
  add("arabic-utf16.srt", Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from(srt, "utf16le")]), "subtitle", { ...subMeta, encoding: "utf-16le" });
  const vtt = "WEBVTT\n\nNOTE made by the DropFlow matrix\n\n" + cues.map(([a, z, t], i) => `cue-${i + 1}\n${a.replace(",", ".")} --> ${z.replace(",", ".")} align:start position:10%\n<b>${t}</b>`).join("\n\n") + "\n";
  add("arabic.vtt", Buffer.from(vtt, "utf8"), "subtitle", subMeta);
  const ass = `[Script Info]\nScriptType: v4.00+\nPlayResX: 640\nPlayResY: 360\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,Arial,28,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n` +
    cues.map(([a, z, t]) => `Dialogue: 0,${a.slice(1, 8)}.${a.slice(9, 11)},${z.slice(1, 8)}.${z.slice(9, 11)},Default,,0,0,0,,{\\b1}${t.replace(/\n/g, "\\N")}{\\b0}`).join("\n") + "\n";
  add("arabic.ass", Buffer.from(ass, "utf8"), "subtitle", subMeta);

  // ---- archives
  const zipEntries = { "docs/hello.txt": strToU8("hello zip"), "تقرير.txt": strToU8("مرحبا"), "a.bin": new Uint8Array([1, 2, 3, 4]) };
  add("pack.zip", Buffer.from(zipSync(zipEntries)), "archive", { entries: { "docs/hello.txt": "hello zip", "تقرير.txt": "مرحبا", "a.bin": Buffer.from([1, 2, 3, 4]) } });
  const longName = "folder-with-a-rather-long-name-for-the-ustar-prefix-field/and-another-sub-folder-that-is-long/file.txt";
  const tar = makeTar([{ name: "docs/", dir: true }, { name: "docs/readme.txt", data: Buffer.from("tar readme") }, { name: "ملاحظة.txt", data: Buffer.from("ملاحظة عربية") }, { name: longName, data: Buffer.from("deep") }]);
  const tarEntries = { "docs/readme.txt": "tar readme", "ملاحظة.txt": "ملاحظة عربية", [longName]: "deep" };
  add("pack.tar", tar, "archive", { entries: tarEntries });
  add("pack.tgz", gzipSync(tar), "archive", { entries: tarEntries });
  add("pack.tar.gz", gzipNamed(tar, "pack.tar"), "archive", { entries: tarEntries });
  add("note.txt.gz", gzipNamed(Buffer.from("gzip single file\n"), "note.txt"), "archive", { entries: { "note.txt": "gzip single file\n" } });

  // ---- documents (the browser offers only the general tools for these; Office conversions are Windows-only)
  add("note-arabic.txt", Buffer.from(`${AR.hello}\n${AR.line}\n`, "utf8"), "document", {});
  add("readme.md", Buffer.from(`# Title\n\n- one\n- ${AR.hello}\n`, "utf8"), "document", {});
  add("page.html", Buffer.from(`<!doctype html><title>t</title><p dir="rtl">${AR.hello}</p>`, "utf8"), "document", {});
  add("data.csv", Buffer.from(`name,city\nعلي,بيروت\nSam,Paris\n`, "utf8"), "spreadsheet", {});
  add("data.json", Buffer.from(JSON.stringify({ a: 1, b: AR.hello }), "utf8"), "spreadsheet", {});

  return { samples, skipped };
}
