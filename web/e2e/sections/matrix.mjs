// SPDX-License-Identifier: GPL-3.0-or-later
// Conversion MATRIX: every operation the web app offers for every generated sample, run through the real app in headless
// Edge (real file input, real wheel click, real options panel), every output re-checked independently (validate.mjs).
// Slow (ffmpeg.wasm, OCR, background removal): it only runs when asked: `node e2e/run.mjs <base> --only=matrix`
// (or DROPFLOW_MATRIX=1). Filters: DROPFLOW_MATRIX_SAMPLES=regex on sample names, DROPFLOW_MATRIX_OPS=regex on op ids.
// Writes matrix.json + matrix.md (the table) into --out.
//
// Status per cell: PASS (output checked), FAIL (wrong output, crash, or an error the tool should not give),
// REFUSED (the tool declined with an honest, documented message, e.g. "Already small", "No QR code found", "Password
// protected"). REFUSED is listed explicitly; it is never counted as a pass of the conversion itself.
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { sleep } from "../cdp.mjs";
import { check } from "../helpers.mjs";
import { makeMatrixSamples } from "../matrix/samples.mjs";
import {
  KIND_OK, PRESENTATION_FORMS, arabicDiagnosis, hasPhrase, icoSizes, isBlack, isRed, isTransparent, isWhite, jpegExif, pdfInfo,
  pdfPageTexts, pixels, probe, setTmp, sha256, sniff, squash, wordHit, zipEntries,
} from "../matrix/validate.mjs";

const AUDIO_TARGETS = new Set(["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma", "aac"]);
const VIDEO_TARGETS = new Set(["mp4", "mkv", "webm", "mov", "avi", "wmv", "gif"]);
const ARABIC_RE = /[؀-ۿ]/;
// Error texts in both languages (the matrix can run on /en/app or /ar/app): app.err.<code> from the message files.
const MSG = { en: JSON.parse(readFileSync(new URL("../../messages/en.json", import.meta.url), "utf8")).app.err, ar: JSON.parse(readFileSync(new URL("../../messages/ar.json", import.meta.url), "utf8")).app.err };
const esc = (s) => s.split(/{[^}]+}/)[0].replace(/[.*+?^${}()|[]\]/g, "\const ARABIC_RE = /[؀-ۿ]/;");
const ERR = (code) => new RegExp(`${esc(MSG.en[code])}|${esc(MSG.ar[code])}`);

// ---------------------------------------------------------------- app driving

async function openApp(b, base, locale) {
  await b.goto(`${base}/${locale}/app`, 400);
  await b.waitFor("!!document.querySelector('[data-testid=dropzone]')", 20000, "dropzone");
}

async function wedges(b) {
  return b.ev("[...document.querySelectorAll('[data-wedge]')].map(e => e.getAttribute('data-wedge'))");
}

/** All wedge ids of one wheel (walking its pages), without "more" and "+ Add". */
async function wheelOps(b, kind) {
  const enabled = await b.ev(`(() => { const t = document.querySelector('[data-wheel=${kind}]'); return !!t && !t.disabled; })()`);
  if (!enabled) return [];
  await b.click(`[data-wheel=${kind}]`);
  await sleep(200);
  const all = [];
  for (let page = 0; page < 10; page++) {
    const ids = await wedges(b);
    const fresh = ids.filter((i) => !i.startsWith("more:") && i !== "wheel.add" && !all.includes(i));
    if (page > 0 && fresh.length === 0) break;
    all.push(...fresh);
    if (!ids.some((i) => i.startsWith("more:"))) break;
    await b.click('[data-wedge^="more:"]');
    await sleep(200);
  }
  return all;
}

async function loadFiles(b, paths, { media = false } = {}) {
  await b.setFiles("[data-testid=file-input]", paths);
  await b.waitFor("document.querySelectorAll('[data-testid=file-chip]').length > 0", 15000, "file chips");
  // Wheels depend on async capability probes (image encoders, the ffmpeg core): wait until they stop changing.
  let last = "";
  let stable = 0;
  const t0 = Date.now();
  while (Date.now() - t0 < (media ? 90000 : 4000)) {
    const now = JSON.stringify(await wedges(b));
    const tabs = await b.ev("[...document.querySelectorAll('[data-wheel]')].map(t => t.disabled ? 0 : 1).join('')");
    const sig = now + tabs;
    stable = sig === last ? stable + 1 : 0;
    last = sig;
    if (stable >= (media ? 4 : 3) && (!media || JSON.parse(now).some((w) => w.startsWith("convert:")) || Date.now() - t0 > 60000)) break;
    await sleep(250);
  }
}

async function offeredOps(b) {
  const convert = await wheelOps(b, "convert");
  const tools = await wheelOps(b, "tools");
  return [...convert, ...tools];
}

const resultList = (b) =>
  b.ev("[...document.querySelectorAll('[data-testid=result]')].map(li => ({ name: li.querySelector('p')?.textContent ?? '', href: li.querySelector('a[download]')?.href ?? '', dl: li.querySelector('a[download]')?.getAttribute('download') ?? '' }))");
const errorJobs = (b) => b.ev("[...document.querySelectorAll('[data-testid=jobs] li[data-state=error]')].map(e => e.innerText.replace(/\\s+/g, ' ').trim())");
const busy = (b) => b.ev("document.querySelectorAll('[data-testid=jobs] li[data-state=running], [data-testid=jobs] li[data-state=waiting]').length");

async function fetchBlob(b, href) {
  const b64 = await b.ev(`(async () => { const u8 = new Uint8Array(await (await fetch(${JSON.stringify(href)})).arrayBuffer()); let s = ''; for (let i = 0; i < u8.length; i += 0x8000) s += String.fromCharCode(...u8.subarray(i, i + 0x8000)); return btoa(s); })()`);
  return Buffer.from(b64, "base64");
}

/** Sets an option control inside the open panel (by its key); choices by VALUE index (labels are translated). */
async function setOpt(b, key, value, choices) {
  await b.ev(`(() => {
    const el = document.querySelector('[data-testid=options-panel] [id$="-${key}"]');
    if (!el) throw new Error('no option ${key}');
    const v = ${JSON.stringify(value)};
    if (el.getAttribute('role') === 'radiogroup') {
      const i = ${JSON.stringify(choices ?? [])}.indexOf(String(v));
      const btn = el.querySelectorAll('button')[i];
      if (!btn) throw new Error('no choice ' + v);
      btn.click();
    } else if (el.type === 'checkbox') { if (el.checked !== !!v) el.click(); }
    else {
      const proto = el.tagName === 'SELECT' ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
      Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, String(v));
      el.dispatchEvent(new Event(el.tagName === 'SELECT' ? 'change' : 'input', { bubbles: true }));
    }
  })()`);
  await sleep(60);
}

/**
 * Options the matrix sets (everything else stays at its default). Only where a default cannot run at all (required text,
 * "nothing to change") or where a known value makes the output checkable.
 */
function overridesFor(op, sample) {
  switch (op) {
    case "img.resize":
      return { width: 50 };
    case "img.edit":
      return { rotate: ["90", ["0", "90", "180", "270"]] };
    case "img.redact":
      return { regions: "0,0,20,20" };
    case "pdf.organize": {
      const n = sample.meta.pages ?? 1;
      return { order: Array.from({ length: n }, (_, i) => n - i).join(", ") };
    }
    case "pdf.metadata":
      return { title: "تقرير تجريبي DropFlow" };
    case "pdf.watermark":
      return { text: "سري CONFIDENTIAL" };
    case "aud.trim":
    case "vid.trim":
      return { start: "0.5", end: "1.5" };
    case "vid.snapshot":
      return { time: "1" };
    default:
      return {};
  }
}

/** Runs one operation on the loaded files; returns { outputs:[{name,bytes}], error, ms }. */
async function runOp(b, op, sample, timeoutMs) {
  const before = (await resultList(b)).length;
  const errBefore = (await errorJobs(b)).length;
  const t0 = Date.now();
  const kind = op.startsWith("convert:") ? "convert" : "tools";
  await b.click(`[data-wheel=${kind}]`);
  await sleep(150);
  for (let i = 0; i < 10; i++) {
    if (await b.ev(`!!document.querySelector('[data-wedge=${JSON.stringify(op)}]')`)) break;
    if (!(await b.ev("!!document.querySelector('[data-wedge^=\"more:\"]')"))) break;
    await b.click('[data-wedge^="more:"]');
    await sleep(180);
  }
  if (!(await b.ev(`!!document.querySelector('[data-wedge=${JSON.stringify(op)}]')`))) return { outputs: [], error: "wedge not found", ms: 0 };
  await b.click(`[data-wedge=${JSON.stringify(op)}]`);
  await sleep(350);
  const set = overridesFor(op, sample);
  if (await b.ev("!!document.querySelector('[data-testid=options-panel]')")) {
    for (const [k, v] of Object.entries(set)) {
      if (Array.isArray(v)) await setOpt(b, k, v[0], v[1]);
      else await setOpt(b, k, v);
    }
    const disabled = await b.ev("document.querySelector('[data-testid=options-panel] button[type=submit]').disabled");
    if (disabled) {
      await b.ev("[...document.querySelectorAll('[data-testid=options-panel] button[type=button]')].at(-1)?.click()");
      return { outputs: [], error: "Run button disabled with the matrix options", ms: 0 };
    }
    await b.click("[data-testid=options-panel] button[type=submit]");
  }
  // wait for a new result or a new error, with no job still running
  for (;;) {
    await sleep(250);
    const res = await resultList(b);
    const errs = await errorJobs(b);
    const running = await busy(b);
    if (!running && (res.length > before || errs.length > errBefore)) {
      await sleep(300); // multi-output jobs add their results together; give the list a moment
      const final = await resultList(b);
      const ms = Date.now() - t0;
      if (errs.length > errBefore) return { outputs: [], error: errs.slice(errBefore).join(" | "), ms };
      const outputs = [];
      for (const r of final.slice(before)) outputs.push({ name: r.dl || r.name, bytes: await fetchBlob(b, r.href) });
      return { outputs, error: null, ms };
    }
    if (Date.now() - t0 > timeoutMs) return { outputs: [], error: `TIMEOUT after ${timeoutMs / 1000}s`, ms: Date.now() - t0, timeout: true };
  }
}

// ---------------------------------------------------------------- expectations

/** Honest refusals that are documented behaviour for this sample+op (REFUSED, not FAIL). */
function expectedRefusal(op, sample, error) {
  const e = error ?? "";
  if (sample.meta.encrypted && ERR("encrypted").test(e)) return "encrypted PDF: the browser cannot open it (unlock is Windows-only)";
  if (ERR("notSmaller").test(e) && (op === "img.compress" || op === "pdf.compress" || (op === "vid.compress" && !sample.meta.compressible))) return "already as small as this re-encode can make it";
  if (op === "img.qr" && !sample.meta.qr && ERR("noQrFound").test(e)) return "no QR code in this picture";
  if ((op === "img.ocr" || op === "convert:txt") && sample.kind === "image" && !sample.meta.words && (ERR("noTextFound").test(e) || ERR("noText").test(e))) return "no text in this picture";
  if (ERR("noAudio").test(e) && sample.meta.audio === false) return "the video has no audio track";
  return null;
}

function near(a, b, tol) {
  return Math.abs(a - b) <= tol;
}

/** Validates the outputs of op on sample. Returns { ok, detail }. Throws nothing. */
async function judge(op, sample, outputs, ctx) {
  const m = sample.meta;
  const fail = (d) => ({ ok: false, detail: d });
  const pass = (d) => ({ ok: true, detail: d });
  if (outputs.length === 0) return fail("no output");
  const o = outputs[0];
  const outExt = (o.name.match(/\.([^.]+)$/)?.[1] ?? "").toLowerCase();
  const kind = sniff(o.bytes);

  // ---------- general tools (any input)
  if (op === "gen.hash") {
    const text = outputs.map((x) => x.bytes.toString("utf8")).join("\n");
    const want = ctx.inputs.map((p) => sha256(p.bytes));
    if (!want.every((h) => text.toLowerCase().includes(h))) return fail(`SHA-256 mismatch: ${text.slice(0, 120)}`);
    return pass("SHA-256 matches Node crypto");
  }
  if (op === "gen.base64") {
    const got = outputs.map((x) => x.bytes.toString("utf8").replace(/\s+/g, ""));
    const ok = ctx.inputs.every((p) => got.some((g) => g.includes(p.bytes.toString("base64"))));
    if (!ok) return fail(`base64 differs (${got[0]?.slice(0, 40)}…)`);
    return pass("Base64 matches Node");
  }
  if (op === "arc.zip") {
    if (kind !== "zip") return fail(`output ${kind}`);
    const entries = zipEntries(o.bytes);
    for (const p of ctx.inputs) {
      const e = Object.entries(entries).find(([n]) => n.normalize("NFC") === p.name.normalize("NFC"));
      if (!e) return fail(`entry ${p.name} missing; got ${Object.keys(entries).join(", ")}`);
      if (!Buffer.from(e[1]).equals(p.bytes)) return fail(`entry ${p.name} bytes differ`);
    }
    return pass(`${Object.keys(entries).length} entries, names + bytes match`);
  }
  if (op === "gen.rename") {
    if (outputs.length !== ctx.inputs.length) return fail(`${outputs.length} files for ${ctx.inputs.length}`);
    for (const p of ctx.inputs) if (!outputs.some((x) => x.bytes.equals(p.bytes))) return fail(`copy of ${p.name} missing`);
    return pass(`${outputs.length} renamed copies: ${outputs.map((x) => x.name).join(", ")}`);
  }
  // ---------- image inputs
  if (sample.kind === "image" && op.startsWith("convert:") && op !== "convert:txt") {
    const t = op.slice(8);
    if (t === "pdf") {
      const info = await pdfInfo(o.bytes);
      if (info.count !== 1 || info.images < 1) return fail(`pdf pages=${info.count} images=${info.images}`);
      if (m.w && !near(info.pages[0].w / info.pages[0].h, m.w / m.h, 0.03)) return fail(`page ${info.pages[0].w}x${info.pages[0].h} has the wrong shape for ${m.w}x${m.h}`);
      return pass(`1 page ${Math.round(info.pages[0].w)}x${Math.round(info.pages[0].h)}pt with the picture`);
    }
    if (!KIND_OK[t]?.includes(kind)) return fail(`output is ${kind}, expected ${t}`);
    if (outExt !== t) return fail(`name ${o.name} has the wrong extension`);
    if (t === "ico") {
      const sizes = icoSizes(o.bytes);
      if (!sizes.length || sizes.some((s) => s.w !== s.h)) return fail(`ico entries ${JSON.stringify(sizes)}`);
      const px = pixels(o.bytes, "ico");
      if (m.alpha && px && !isTransparent(px.at(1, Math.floor(px.h / 2)))) return fail(`ico lost the transparency (${px.at(1, Math.floor(px.h / 2))})`);
      return pass(`ico sizes ${sizes.map((s) => s.w).join(",")}`);
    }
    const px = pixels(o.bytes, t);
    if (!px) return fail("FFmpeg cannot decode the output");
    if (m.w && (px.w !== m.w || px.h !== m.h)) return fail(`${px.w}x${px.h}, expected ${m.w}x${m.h}${sample.meta.note ? ` (${sample.meta.note})` : ""}`);
    if (t === "jpg") {
      const ex = jpegExif(o.bytes);
      if (ex && ex.orientation !== 1) return fail(`JPEG still carries EXIF Orientation ${ex.orientation} after the pixels were turned (double rotation in viewers)`);
    }
    const notes = [`${px.w}x${px.h}`];
    if (m.redTopLeft && !isRed(px.at(4, 4))) return fail(`red mark not at top-left (${px.at(4, 4)})`);
    if (m.redTopRight && !isRed(px.at(px.w - 5, 4))) return fail(`orientation not applied: red mark not at top-right (${px.at(px.w - 5, 4)}, top-left ${px.at(4, 4)})`);
    if (m.redBottomRight && !isRed(px.at(px.w - 5, px.h - 5))) return fail(`orientation not applied: red mark not at bottom-right (${px.at(px.w - 5, px.h - 5)})`);
    if (m.alpha) {
      const p = px.at(5, Math.floor(px.h / 2));
      if (t === "jpg" || t === "bmp") {
        if (!isWhite(p)) return fail(`transparent area became ${p} in ${t} (expected white, like the JPEG flatten)`);
        notes.push("transparent -> white");
      } else {
        if (!isTransparent(p)) return fail(`transparency lost in ${t}: ${p}`);
        notes.push("alpha kept");
      }
    }
    return pass(notes.join(", "));
  }

  if (sample.kind === "image") {
    switch (op) {
      case "convert:txt":
      case "img.ocr": {
        const text = o.bytes.toString("utf8");
        if (m.words) {
          const h = wordHit(text, m.words);
          if (h.share < 0.6) return fail(`OCR found ${h.found.length}/${m.words.length} words: ${JSON.stringify(squash(text).slice(0, 80))}`);
          return pass(`OCR ${h.found.length}/${m.words.length} words (${JSON.stringify(squash(text).slice(0, 50))})`);
        }
        return pass(`text output ${JSON.stringify(squash(text).slice(0, 40))}`);
      }
      case "img.compress": {
        const px = pixels(o.bytes, outExt);
        if (!px || (m.w && (px.w !== m.w || px.h !== m.h))) return fail(`decoded ${px?.w}x${px?.h}`);
        if (o.bytes.length >= sample.size) return fail(`not smaller: ${sample.size} -> ${o.bytes.length}`);
        return pass(`${sample.size} -> ${o.bytes.length} B, ${px.w}x${px.h}`);
      }
      case "img.resize": {
        const px = pixels(o.bytes, outExt);
        const eh = Math.round((50 * m.h) / m.w);
        if (!px || px.w !== 50 || !near(px.h, eh, 1)) return fail(`${px?.w}x${px?.h}, expected 50x${eh}`);
        if (m.redTopRight && !isRed(px.at(px.w - 2, 1))) return fail("orientation lost on resize");
        return pass(`50x${px.h}`);
      }
      case "img.crop": {
        const px = pixels(o.bytes, outExt);
        const s = Math.min(m.w, m.h);
        if (!px || px.w !== s || px.h !== s) return fail(`${px?.w}x${px?.h}, expected ${s}x${s}`);
        return pass(`${s}x${s}`);
      }
      case "img.edit": {
        const px = pixels(o.bytes, outExt);
        if (!px || px.w !== m.h || px.h !== m.w) return fail(`${px?.w}x${px?.h}, expected ${m.h}x${m.w} after 90°`);
        if (m.redTopLeft && !isRed(px.at(px.w - 4, 4))) return fail(`after turning right the red mark should be top-right (${px.at(px.w - 4, 4)})`);
        return pass(`${px.w}x${px.h} (turned 90°)`);
      }
      case "img.background": {
        const px = pixels(o.bytes, outExt);
        if (!px || px.w < m.w || px.h < m.h) return fail(`${px?.w}x${px?.h} smaller than ${m.w}x${m.h}`);
        if (!isWhite(px.at(1, 1))) return fail(`corner is not the white background (${px.at(1, 1)})`);
        if (m.alpha && !isWhite(px.at(Math.floor(px.w * 0.2), Math.floor(px.h / 2)))) return fail(`transparent part not filled (${px.at(Math.floor(px.w * 0.2), Math.floor(px.h / 2))})`);
        return pass(`${px.w}x${px.h}, white`);
      }
      case "img.redact": {
        const px = pixels(o.bytes, outExt);
        if (!px || (m.w && (px.w !== m.w || px.h !== m.h))) return fail(`${px?.w}x${px?.h}`);
        if (!isBlack(px.at(5, 5)) || !isBlack(px.at(15, 15))) return fail(`area 0,0,20,20 is not black (${px.at(5, 5)})`);
        return pass("area blacked out");
      }
      case "img.icons": {
        const names = outputs.map((x) => x.name);
        const ico = outputs.find((x) => sniff(x.bytes) === "ico");
        const zip = outputs.find((x) => sniff(x.bytes) === "zip");
        if (zip) {
          const entries = Object.keys(zipEntries(zip.bytes));
          if (!entries.some((n) => n.endsWith(".ico")) || !entries.some((n) => n.endsWith(".png"))) return fail(`zip entries ${entries.join(",")}`);
          return pass(`zip: ${entries.length} files (${entries.slice(0, 5).join(", ")}…)`);
        }
        if (!ico) return fail(`no .ico among ${names.join(",")}`);
        return pass(`${outputs.length} files: ${names.slice(0, 6).join(", ")}`);
      }
      case "img.qr": {
        const text = o.bytes.toString("utf8");
        if (!m.qr) return fail(`a QR was "found" in a picture without one: ${JSON.stringify(text.slice(0, 60))}`);
        if (!text.includes(m.qr)) return fail(`decoded ${JSON.stringify(text.slice(0, 80))}, expected ${JSON.stringify(m.qr)}`);
        return pass(`decoded ${JSON.stringify(m.qr)}`);
      }
      case "img.remove-bg": {
        const px = pixels(o.bytes, "png");
        if (kind !== "png" || !px) return fail(`output ${kind}`);
        if (m.w && (px.w !== m.w || px.h !== m.h)) return fail(`${px.w}x${px.h}, expected ${m.w}x${m.h}`);
        const clear = px.count((r, g, bb, a) => a < 20);
        return pass(`png ${px.w}x${px.h}, ${Math.round((100 * clear) / (px.w * px.h))}% transparent`);
      }
      case "img.strip-gps":
      case "img.metadata": {
        if (op === "img.metadata") {
          const text = o.bytes.toString("utf8");
          if (!text.trim()) return fail("empty report");
          if (m.gps && !/GPS|25°|25\.2/i.test(text)) return fail(`report does not show the GPS position: ${JSON.stringify(text.slice(0, 120))}`);
          if (m.gps && !/DropFlowCam/.test(text)) return fail("report does not show the camera");
          return pass(`report ${text.split("\n").length} lines`);
        }
        const px = pixels(o.bytes, outExt);
        if (!px || (m.w && (px.w !== m.w || px.h !== m.h))) return fail(`${px?.w}x${px?.h}`);
        const ex = kind === "jpg" ? jpegExif(o.bytes) : null;
        if (ex?.tags.has(0x8825)) return fail("GPS IFD still present");
        if (m.redTopRight && !isRed(px.at(px.w - 5, 4)) && !(ex && ex.orientation === 6)) return fail("orientation lost when stripping");
        return pass(`${px.w}x${px.h}, no GPS${ex ? `, orientation tag ${ex.orientation}` : ""}`);
      }
    }
  }

  // ---------- PDF inputs
  if (sample.kind === "pdf") {
    const src = ctx.srcInfo;
    const imagesOp = op === "convert:png" || op === "convert:jpg" || op === "pdf.to-images";
    if (imagesOp) {
      const fmt = op === "convert:jpg" ? "jpg" : "png";
      if (outputs.length !== src.count) return fail(`${outputs.length} images for ${src.count} pages`);
      for (let i = 0; i < outputs.length; i++) {
        const px = pixels(outputs[i].bytes, fmt);
        if (!px || sniff(outputs[i].bytes) !== fmt) return fail(`page ${i + 1}: not a ${fmt}`);
        const pg = src.pages[i];
        const [ew, eh] = pg.rot % 180 ? [pg.h * 2, pg.w * 2] : [pg.w * 2, pg.h * 2];
        if (!near(px.w, ew, 3) || !near(px.h, eh, 3)) return fail(`page ${i + 1}: ${px.w}x${px.h}, expected ${Math.round(ew)}x${Math.round(eh)}`);
        const ink = px.count((r, g, bb) => r < 128 && g < 128 && bb < 128);
        if (ink < 200) return fail(`page ${i + 1} looks blank (${ink} dark pixels)`);
      }
      return pass(`${outputs.length} ${fmt} pages, sizes match, ink on every page`);
    }
    const textOp = op === "convert:txt" || op === "convert:md" || op === "pdf.extract-text";
    if (textOp) {
      const text = o.bytes.toString("utf8");
      if (m.scanned) {
        const h = wordHit(text, m.words);
        if (h.share < 0.6) return fail(`OCR of scanned pages found ${h.found.length}/${m.words.length}: ${JSON.stringify(squash(text).slice(0, 80))}`);
        return pass(`OCR ${h.found.length}/${m.words.length} words`);
      }
      const missing = (m.phrases ?? []).filter((p) => !hasPhrase(text, p));
      if (missing.length) return fail(`missing ${missing.map((p) => `"${p}"${ARABIC_RE.test(p) ? ` [${arabicDiagnosis(text, p)}]` : ""}`).join(", ")} in ${JSON.stringify(squash(text).slice(0, 120))}`);
      if (m.arabic && PRESENTATION_FORMS.test(text)) return fail("text contains Arabic presentation forms (search/copy would break)");
      if (m.pageOrder) {
        const idx = m.pageOrder.map((p) => squash(text).indexOf(squash(p)));
        if (idx.some((v, i) => i > 0 && v < idx[i - 1])) return fail(`pages out of order: ${idx}`);
      }
      return pass(`${(m.phrases ?? []).length} phrases found in logical order${m.arabic ? " (Arabic)" : ""}`);
    }
    if (kind !== "pdf") return fail(`output is ${kind}`);
    const info = await pdfInfo(o.bytes);
    const texts = op === "pdf.compress" ? [] : await pdfPageTexts(o.bytes);
    // "text layer kept": every glyph of input page i is still on output page i (same extractor on both sides, multiset of
    // non-space characters, so an added page number or a re-ordered content stream does not matter).
    const bag = (t) => {
      const mm = new Map();
      for (const ch of t.replace(/s+/g, "")) mm.set(ch, (mm.get(ch) ?? 0) + 1);
      return mm;
    };
    const within = (a, b) => [...bag(a)].every(([ch, n]) => (bag(b).get(ch) ?? 0) >= n);
    const keepsText = () => ctx.srcTexts.every((t, i) => within(t, texts[i] ?? ""));
    switch (op) {
      case "pdf.split": {
        if (outputs.length !== src.count) return fail(`${outputs.length} parts for ${src.count} pages`);
        for (const x of outputs) if ((await pdfInfo(x.bytes)).count !== 1) return fail("a part does not have exactly one page");
        return pass(`${outputs.length} one-page PDFs`);
      }
      case "pdf.rotate": {
        if (info.count !== src.count) return fail(`page count ${info.count}`);
        const bad = info.pages.findIndex((p, i) => p.rot !== (src.pages[i].rot + 90) % 360);
        if (bad >= 0) return fail(`page ${bad + 1} rotation ${info.pages[bad].rot}, expected ${(src.pages[bad].rot + 90) % 360}`);
        if (!keepsText()) return fail("text layer lost");
        return pass("every page +90°, text kept");
      }
      case "pdf.organize": {
        if (info.count !== src.count) return fail(`page count ${info.count}`);
        const n = src.count;
        const bad = texts.findIndex((t, i) => !within(ctx.srcTexts[n - 1 - i], t) || !within(t, ctx.srcTexts[n - 1 - i]));
        if (bad >= 0) return fail(`output page ${bad + 1} is not input page ${n - bad}: ${texts.map((t) => t.slice(0, 20)).join(" | ")}`);
        return pass(`${info.count} pages reversed`);
      }
      case "pdf.metadata":
        if (info.title !== "تقرير تجريبي DropFlow") return fail(`title ${JSON.stringify(info.title)}`);
        if (info.count !== src.count) return fail("pages changed");
        return pass("Arabic title written, pages kept");
      case "pdf.watermark":
        if (info.count !== src.count || info.images < 1) return fail(`pages ${info.count}, images ${info.images}`);
        if (!keepsText()) return fail("text layer lost under the watermark");
        return pass(`watermark image on ${info.count} pages, text kept`);
      case "pdf.page-numbers": {
        if (info.count !== src.count) return fail("pages changed");
        const bad = texts.findIndex((t, i) => !new RegExp(`(^|\\s)${i + 1}(\\s|$)`).test(t));
        if (bad >= 0) return fail(`page ${bad + 1} has no number ${bad + 1}: ${JSON.stringify(texts[bad].slice(-40))}`);
        if (!keepsText()) return fail("text layer lost");
        return pass(`numbers 1..${info.count}`);
      }
      case "pdf.compress":
        if (info.count !== src.count) return fail(`pages ${info.count}`);
        if (o.bytes.length >= sample.size) return fail(`not smaller ${sample.size} -> ${o.bytes.length}`);
        return pass(`${sample.size} -> ${o.bytes.length} B`);
    }
  }

  // ---------- audio / video inputs
  if (sample.kind === "audio" || sample.kind === "video") {
    const t = op.startsWith("convert:") ? op.slice(8) : null;
    const pr = probe(o.bytes, outExt);
    if (!pr || pr.error) return fail(`ffprobe: ${pr?.error ?? "unavailable"}`);
    const dur = pr.duration;
    const d = (x) => `${Number.isFinite(x) ? x.toFixed(2) : x}s`;
    if (t && AUDIO_TARGETS.has(t)) {
      if (!KIND_OK[t].includes(kind)) return fail(`file is ${kind}, expected ${t}`);
      if (!pr.audio) return fail("no audio stream");
      if (pr.video && !/mjpeg|png/.test(pr.video.codec)) return fail(`unexpected video stream ${pr.video.codec}`);
      if (!near(dur, m.duration, 0.35)) return fail(`duration ${d(dur)}, expected ~${m.duration}s`);
      return pass(`${pr.format} ${pr.audio.codec} ${pr.audio.channels}ch ${d(dur)}`);
    }
    if (t && VIDEO_TARGETS.has(t)) {
      if (t === "gif" ? kind !== "gif" : !KIND_OK[t].includes(kind)) return fail(`file is ${kind}, expected ${t}`);
      if (!pr.video) return fail("no video stream");
      if (m.w && t !== "gif" && (pr.video.w !== m.w || pr.video.h !== m.h)) return fail(`${pr.video.w}x${pr.video.h}, expected ${m.w}x${m.h}`);
      if (t !== "gif" && m.audio && !pr.audio) return fail("audio track dropped");
      if (t !== "gif" && !near(dur, m.duration, 0.4)) return fail(`duration ${d(dur)}`);
      return pass(`${pr.format} ${pr.video.codec}${pr.audio ? "+" + pr.audio.codec : ""} ${pr.video.w}x${pr.video.h} ${d(dur)}`);
    }
    switch (op) {
      case "aud.normalize":
        if (!pr.audio || !near(dur, m.duration, 0.35)) return fail(`audio ${!!pr.audio} ${d(dur)}`);
        return pass(`${pr.audio.codec} ${d(dur)}`);
      case "aud.trim":
      case "vid.trim":
        if (!near(dur, 1, 0.35)) return fail(`trim 0.5-1.5 gave ${d(dur)}`);
        if (op === "vid.trim" && (!pr.video || (m.audio && !pr.audio))) return fail("stream missing after trim");
        return pass(`${d(dur)}`);
      case "vid.mute":
        if (!pr.video || pr.audio) return fail(`video ${!!pr.video}, audio ${!!pr.audio}`);
        return pass(`${pr.video.codec}, no audio, ${d(dur)}`);
      case "vid.snapshot":
        if (kind !== "png" || pr.video?.w !== m.w || pr.video?.h !== m.h) return fail(`${kind} ${pr.video?.w}x${pr.video?.h}`);
        return pass(`png ${m.w}x${m.h}`);
      case "vid.speed":
        if (!pr.video || !near(dur, m.duration / 2, 0.35)) return fail(`2x speed gave ${d(dur)}`);
        if (m.audio && !pr.audio) return fail("audio dropped");
        return pass(`${d(dur)} at 2x`);
      case "vid.compress":
        if (!pr.video || !near(dur, m.duration, 0.4)) return fail(`${d(dur)}`);
        return pass(`${pr.video.codec} ${o.bytes.length} B (input ${sample.size} B)`);
    }
  }

  // ---------- subtitles
  if (sample.kind === "subtitle" && op.startsWith("convert:")) {
    const t = op.slice(8);
    const text = o.bytes.toString("utf8").replace(/^﻿/, "");
    const missing = m.words.filter((w) => !squash(text).includes(squash(w)));
    if (missing.length) return fail(`missing ${JSON.stringify(missing)} in ${JSON.stringify(text.slice(0, 100))}`);
    if (t === "srt" && !/00:00:01,000 --> 00:00:02,500/.test(text)) return fail("SRT timing not kept");
    if (t === "vtt" && (!/^WEBVTT/.test(text) || !/00:00:01\.000 --> 00:00:02\.500/.test(text))) return fail("VTT header/timing wrong");
    if (t === "txt" && /-->|<b>|\{\\/.test(text)) return fail("TXT still has timings or tags");
    if (/<b>|\{\\b1\}/.test(text) && t === "srt" && sample.name.endsWith(".ass")) return fail("ASS override tags leaked into SRT");
    return pass(`${m.cues} cues, Arabic kept${t !== "txt" ? ", timings kept" : ""}`);
  }

  // ---------- archives
  if (op === "arc.extract") {
    const want = Object.entries(m.entries);
    for (const [name, content] of want) {
      const buf = Buffer.isBuffer(content) ? content : Buffer.from(content, "utf8");
      const base = name.split("/").pop();
      const hit = outputs.find((x) => x.bytes.equals(buf) && (x.name === name || x.name.endsWith(base)));
      if (!hit) return fail(`entry ${name} missing or renamed wrongly; got ${outputs.map((x) => x.name).join(", ")}`);
    }
    return pass(`${outputs.length} entries with the right names and bytes`);
  }

  if (op === "pdf.merge") {
    const info = await pdfInfo(o.bytes);
    const want = ctx.inputs.reduce((s, p) => s + p.pages, 0);
    if (info.count !== want) return fail(`${info.count} pages, expected ${want}`);
    // every input page's text layer must be on the matching merged page (same extractor on both sides)
    const outTexts = await pdfPageTexts(o.bytes);
    const inTexts = [];
    for (const p of ctx.inputs) inTexts.push(...(await pdfPageTexts(p.bytes)));
    const strip = (t) => t.replace(/s+/g, "");
    const lost = inTexts.findIndex((t, i) => strip(outTexts[i] ?? "") !== strip(t));
    if (lost >= 0) return fail(`merged page ${lost + 1} text differs from its source page`);
    return pass(`${info.count} pages in order, text kept`);
  }
  if (op === "img.to-pdf") {
    const info = await pdfInfo(o.bytes);
    if (info.count !== ctx.inputs.length) return fail(`${info.count} pages for ${ctx.inputs.length} pictures`);
    const bad = info.pages.findIndex((pg, i) => ctx.inputs[i].meta?.w && !near(pg.w / pg.h, ctx.inputs[i].meta.w / ctx.inputs[i].meta.h, 0.03));
    if (bad >= 0) return fail(`page ${bad + 1} shape ${info.pages[bad].w}x${info.pages[bad].h} != picture ${ctx.inputs[bad].meta.w}x${ctx.inputs[bad].meta.h}`);
    return pass(`${info.count} pages, each the picture's shape (EXIF orientation applied)`);
  }
  if (op === "img.collage") {
    const px = pixels(o.bytes, outExt);
    if (!px || px.w < 100 || px.h < 100) return fail(`collage ${px?.w}x${px?.h}`);
    return pass(`${kind} ${px.w}x${px.h}`);
  }
  return fail(`no validator for ${op} on ${sample.kind}`);
}

// ---------------------------------------------------------------- main

function timeoutFor(op, sample) {
  if (sample.kind === "audio" || sample.kind === "video") return 240000;
  if (/ocr|remove-bg|convert:txt/.test(op) || sample.meta.scanned) return 180000;
  if (sample.name === "big.jpg") return 120000;
  return 60000;
}

export async function matrix({ b, base, out }) {
  const t0 = Date.now();
  const dir = join(out, "matrix");
  mkdirSync(dir, { recursive: true });
  setTmp(join(dir, "tmp"));
  console.log("\n== matrix: generating samples");
  const { samples, skipped } = await makeMatrixSamples(b, join(dir, "samples"));
  const { statSync } = await import("node:fs");
  for (const s of samples) s.size = statSync(s.path).size;
  console.log(`   ${samples.length} samples; skipped: ${skipped.join("; ") || "none"}`);
  const sampleFilter = process.env.DROPFLOW_MATRIX_SAMPLES ? new RegExp(process.env.DROPFLOW_MATRIX_SAMPLES) : null;
  const opFilter = process.env.DROPFLOW_MATRIX_OPS ? new RegExp(process.env.DROPFLOW_MATRIX_OPS) : null;
  const locale = process.env.DROPFLOW_MATRIX_LOCALE ?? "en";
  const rows = [];
  const record = (row) => {
    rows.push(row);
    const tag = row.status === "PASS" ? "PASS" : row.status === "REFUSED" ? "REFU" : "FAIL";
    console.log(`${tag}  ${row.sample} × ${row.op}  - ${String(row.detail).slice(0, 220)} (${Math.round(row.ms / 100) / 10}s)`);
  };

  // single-file cells
  const runSet = async (label, set, extraFilter) => {
    const media = set.some((s) => s.kind === "audio" || s.kind === "video");
    const inputs = set.map((s) => ({ name: s.name, bytes: readFileSync(s.path), meta: s.meta, pages: s.meta.pages, phrases: s.meta.phrases }));
    const isPdf = set[0].kind === "pdf" && !set[0].meta.encrypted;
    const ctx = { inputs, srcInfo: isPdf ? await pdfInfo(inputs[0].bytes) : null, srcTexts: isPdf ? await pdfPageTexts(inputs[0].bytes) : [] };
    await openApp(b, base, locale);
    await loadFiles(b, set.map((s) => s.path), { media });
    let ops = (await offeredOps(b)).filter((op) => !opFilter || opFilter.test(op)).filter((op) => !extraFilter || extraFilter(op));
    rows.push({ sample: label, op: "(offered)", status: "INFO", detail: ops.join(" "), ms: 0 });
    console.log(`   ${label}: offered ${ops.join(" ")}`);
    for (const op of ops) {
      let r;
      try {
        r = await runOp(b, op, set[0], timeoutFor(op, set[0]));
      } catch (e) {
        r = { outputs: [], error: `harness: ${e.message}`, ms: 0, timeout: true };
      }
      if (r.error) {
        const why = expectedRefusal(op, set[0], r.error);
        record({ sample: label, op, status: why ? "REFUSED" : "FAIL", detail: why ? `${r.error} — ${why}` : r.error, ms: r.ms });
      } else {
        let v;
        try {
          v = await judge(op, { ...set[0], size: set[0].size }, r.outputs, ctx);
        } catch (e) {
          v = { ok: false, detail: `validator crashed: ${e.message}` };
        }
        record({ sample: label, op, status: v.ok ? "PASS" : "FAIL", detail: v.detail, ms: r.ms, outputs: r.outputs.map((x) => `${x.name} (${x.bytes.length} B)`) });
        if (!v.ok) for (const x of r.outputs.slice(0, 3)) writeFileSync(join(dir, `FAIL ${label.replace(/[\\/:*?"<>|]/g, "_")} ${op.replace(/[:]/g, "_")} ${x.name}`), x.bytes);
      }
      if (r.timeout) {
        // a hung job: start over with a fresh page for the rest of this sample
        await openApp(b, base, locale);
        await loadFiles(b, set.map((s) => s.path), { media });
      }
    }
  };

  for (const s of samples) {
    if (sampleFilter && !sampleFilter.test(s.name)) continue;
    try {
      await runSet(s.name, [s]);
    } catch (e) {
      record({ sample: s.name, op: "(load)", status: "FAIL", detail: `harness: ${e.message}`, ms: 0 });
    }
  }

  // multi-file selections (tools that need two or more files)
  const by = (n) => samples.find((x) => x.name === n);
  const multi = [
    ["3 pictures (png + exif-rotated jpg + webp)", ["alpha.png", "exif-rotate-90.jpg", "alpha.webp"], (op) => ["img.to-pdf", "img.collage", "gen.rename", "arc.zip"].includes(op)],
    ["3 PDFs (Arabic + English + 4-page mixed)", ["arabic-chromium.pdf", "english-chromium.pdf", "multipage-mixed.pdf"], (op) => ["pdf.merge", "gen.rename", "arc.zip", "gen.hash"].includes(op)],
    ["mixed (pdf + mp3 + srt + Arabic-named png)", ["english-chromium.pdf", "tone.mp3", "arabic-utf8-bom.srt", "صورة عربية.png"], () => true],
  ];
  if (!sampleFilter || sampleFilter.test("multi")) {
    for (const [label, names, f] of multi) {
      const set = names.map(by).filter(Boolean);
      if (set.length !== names.length) continue;
      try {
        await runSet(label, set, f);
      } catch (e) {
        record({ sample: label, op: "(load)", status: "FAIL", detail: `harness: ${e.message}`, ms: 0 });
      }
    }
  }

  // ---- report
  const cells = rows.filter((r) => r.status !== "INFO");
  const n = (st) => cells.filter((r) => r.status === st).length;
  const md = [
    `# DropFlow Web conversion matrix (${new Date().toISOString().slice(0, 16)}, ${locale}, ${Math.round((Date.now() - t0) / 1000)} s)`,
    "",
    `${cells.length} cells: ${n("PASS")} PASS, ${n("FAIL")} FAIL, ${n("REFUSED")} REFUSED (honest documented refusal). Samples skipped: ${skipped.join("; ") || "none"}.`,
    "",
    "| Sample | Operation | Result | What was checked |",
    "|---|---|---|---|",
    ...rows.map((r) => `| ${r.sample} | ${r.op} | ${r.status} | ${String(r.detail).replace(/\|/g, "\\|").replace(/\n/g, " ").slice(0, 300)} |`),
  ].join("\n");
  writeFileSync(join(out, "matrix.md"), md);
  writeFileSync(join(out, "matrix.json"), JSON.stringify({ skipped, rows }, null, 2));
  console.log(`\nmatrix: ${cells.length} cells, ${n("PASS")} pass, ${n("FAIL")} fail, ${n("REFUSED")} refused -> ${join(out, "matrix.md")}`);
  for (const r of cells) check(`matrix: ${r.sample} × ${r.op}`, r.status !== "FAIL", r.status === "FAIL" ? r.detail : "");
  check("matrix: no sample skipped for a missing program", skipped.length === 0, skipped.join("; "));
}
