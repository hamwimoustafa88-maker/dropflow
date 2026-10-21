// Shared e2e helpers: pass/fail bookkeeping, app driving (files, wheel, options, results, downloads).
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync } from "node:fs";
import { join } from "node:path";
import { PDFArray, PDFDocument, decodePDFRawStream } from "pdf-lib";
import { sleep } from "./cdp.mjs";

export const results = [];
export function check(name, ok, detail = "") {
  results.push({ name, ok: !!ok, detail });
  console.log(`${ok ? "PASS" : "FAIL"}  ${name}${detail ? "  - " + String(detail).slice(0, 240) : ""}`);
  return !!ok;
}
export async function section(name, fn) {
  console.log(`\n== ${name}`);
  try {
    await fn();
  } catch (e) {
    check(`${name}: no crash`, false, e instanceof Error ? e.message : String(e));
  }
}

export const q = (s) => JSON.stringify(s);

export async function freshApp(b, base, locale = "en") {
  await b.goto(`${base}/${locale}/app`, 1200);
  await b.waitFor("!!document.querySelector('[data-testid=dropzone]')", 15000, "dropzone");
}

export async function addFiles(b, paths) {
  await b.setFiles("[data-testid=file-input]", paths);
  await b.waitFor("document.querySelectorAll('[data-testid=file-chip]').length > 0", 10000, "file chips");
  await sleep(250);
}

export const wedgeIds = (b) => b.ev("[...document.querySelectorAll('[data-wedge]')].map(e => e.getAttribute('data-wedge'))");
/** Ids of ALL wedges of the active wheel, walking through its pages (and returning to the first page). */
export async function allWedgeIds(b) {
  const first = await wedgeIds(b);
  if (!first.some((i) => i.startsWith("more:"))) return first;
  const all = first.filter((i) => !i.startsWith("more:"));
  for (let page = 1; page < 8; page++) {
    await b.click('[data-wedge^="more:"]');
    await sleep(250);
    const ids = await wedgeIds(b);
    if (ids.some((i) => all.includes(i))) break; // wrapped around
    all.push(...ids.filter((i) => !i.startsWith("more:")));
    if (!ids.some((i) => i.startsWith("more:"))) break;
  }
  return all;
}

export const wedgeLabels = (b) => b.ev("[...document.querySelectorAll('[data-wedge]')].map(e => e.getAttribute('aria-label'))");
export const activeTab = (b) => b.ev("document.querySelector('[data-wheel][aria-pressed=true]')?.getAttribute('data-wheel')");
export const tabEnabled = (b, kind) => b.ev(`!document.querySelector('[data-wheel=${kind}]').disabled`);
export const chipNames = (b) => b.ev("[...document.querySelectorAll('[data-testid=file-chip]')].map(e => e.querySelector('span').textContent)");

export async function selectTab(b, kind) {
  await b.click(`[data-wheel=${kind}]`);
  await sleep(250);
}

/** Clicks a wedge; long wheels are paged (7 + "more"), so flip pages until the wedge shows up. */
export async function clickWedge(b, id) {
  for (let i = 0; i < 6; i++) {
    if (await b.ev(`!!document.querySelector('[data-wedge=${q(id)}]')`)) break;
    if (!(await b.ev("!!document.querySelector('[data-wedge^=\"more:\"]')"))) break;
    await b.click('[data-wedge^="more:"]');
    await sleep(250);
  }
  await b.click(`[data-wedge=${q(id)}]`);
  await sleep(150);
}

/** Text of every page of a PDF (decodes the content streams with pdf-lib; independent of our implementation). */
export async function pageTexts(bytes) {
  const doc = await PDFDocument.load(bytes);
  return doc.getPages().map((p) => {
    const c = p.node.Contents();
    const streams = c instanceof PDFArray ? c.asArray().map((r) => doc.context.lookup(r)) : [c];
    const content = streams.map((s) => Buffer.from(decodePDFRawStream(s).decode()).toString("latin1")).join("\n");
    // pdf-lib writes standard-font text as hex strings (<48656c6c6f> Tj); other writers use (literal) Tj.
    const hex = [...content.matchAll(/<([0-9A-Fa-f\s]+)>\s*Tj/g)].map((m) => Buffer.from(m[1].replace(/\s+/g, ""), "hex").toString("latin1"));
    const lit = [...content.matchAll(/\(((?:\\.|[^\\)])*)\)\s*Tj/g)].map((m) => m[1]);
    return [...hex, ...lit].join(" ");
  });
}

export const hasPanel = (b) => b.ev("!!document.querySelector('[data-testid=options-panel]')");

/** Sets the value of an option control inside the panel (id ends with `-key`). */
export async function setOption(b, key, value) {
  await b.ev(`(() => {
    const el = document.querySelector('[id$="-${key}"]');
    if (!el) throw new Error('no option ${key}');
    if (el.getAttribute('role') === 'radiogroup') {
      const btn = [...el.querySelectorAll('button')].find(x => x.textContent.trim() === ${q(String(value))});
      if (!btn) throw new Error('no choice ${value}');
      btn.click();
    } else if (el.type === 'checkbox') {
      if (el.checked !== ${q(!!value)}) el.click();
    } else if (el.tagName === 'SELECT') {
      el.value = ${q(String(value))};
      el.dispatchEvent(new Event('change', { bubbles: true }));
    } else {
      const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
      Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, ${q(String(value))});
      el.dispatchEvent(new Event('input', { bubbles: true }));
    }
  })()`);
  await sleep(80);
}

export async function runPanel(b) {
  await b.click("[data-testid=options-panel] button[type=submit]");
}

export const resultNames = (b) => b.ev("[...document.querySelectorAll('[data-testid=result]')].map(e => e.querySelector('p').textContent)");
export const jobStates = (b) => b.ev("[...document.querySelectorAll('[data-testid=jobs] li')].map(e => e.getAttribute('data-state'))");
export const jobTexts = (b) => b.ev("[...document.querySelectorAll('[data-testid=jobs] li')].map(e => e.textContent)");

/** Waits until `count` results exist (or a job failed). Returns result names; fails fast with the job text. */
export async function waitResults(b, count = 1, timeoutMs = 30000) {
  const t0 = Date.now();
  for (;;) {
    const names = await resultNames(b);
    if (names.length >= count) return names;
    const failed = await b.ev("[...document.querySelectorAll('[data-testid=jobs] li[data-state=error]')].map(e => e.textContent)");
    if (failed.length) throw new Error(`job failed: ${failed.join(" | ")}`);
    if (Date.now() - t0 > timeoutMs) throw new Error(`timeout waiting for ${count} result(s); have ${names.length}; jobs=${JSON.stringify(await jobTexts(b))}`);
    await sleep(150);
  }
}

export async function setDownloadDir(b, dir) {
  rmSync(dir, { recursive: true, force: true });
  mkdirSync(dir, { recursive: true });
  await b.send("Page.setDownloadBehavior", { behavior: "allow", downloadPath: dir });
}

/** Clicks the Download link of result `index` and returns { name, bytes } of the saved file. */
export async function downloadResult(b, dir, index = 0) {
  const before = new Set(existsSync(dir) ? readdirSync(dir) : []);
  await b.ev(`document.querySelectorAll('[data-testid=result] a[download]')[${index}].click()`);
  const t0 = Date.now();
  for (;;) {
    const files = (existsSync(dir) ? readdirSync(dir) : []).filter((f) => !before.has(f) && !f.endsWith(".crdownload") && !f.endsWith(".tmp"));
    if (files.length) {
      const f = files[0];
      await sleep(150);
      return { name: f, bytes: readFileSync(join(dir, f)), size: statSync(join(dir, f)).size };
    }
    if (Date.now() - t0 > 15000) throw new Error("download did not arrive");
    await sleep(120);
  }
}

/** Decodes bytes inside the browser with createImageBitmap; returns {w,h,px:[r,g,b,a] of (x,y)} */
export async function decodeInBrowser(b, bytes, mime, x = 0, y = 0) {
  return b.ev(`(async () => {
    const bin = atob(${q(Buffer.from(bytes).toString("base64"))});
    const u8 = Uint8Array.from(bin, c => c.charCodeAt(0));
    const bmp = await createImageBitmap(new Blob([u8], { type: ${q(mime)} }));
    const c = new OffscreenCanvas(bmp.width, bmp.height);
    const ctx = c.getContext('2d'); ctx.drawImage(bmp, 0, 0);
    return { w: bmp.width, h: bmp.height, px: [...ctx.getImageData(${x}, ${y}, 1, 1).data] };
  })()`);
}

export const clearAll = async (b) => {
  await b.ev("[...document.querySelectorAll('button')].find(x => x.textContent.trim() === 'Clear' || x.textContent.trim() === 'مسح')?.click()");
};

export function isPng(buf) {
  return buf.length > 8 && buf.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
}
export const isJpeg = (buf) => buf[0] === 0xff && buf[1] === 0xd8 && buf[2] === 0xff;
export const isPdf = (buf) => buf.subarray(0, 5).toString() === "%PDF-";
export const ascii = (buf, from, to) => buf.subarray(from, to).toString("latin1");
