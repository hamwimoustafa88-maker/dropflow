// What used to be "Windows app only" and now runs in the browser: OCR (image -> TXT), HEIC/TIFF input, GIF/TIFF output,
// background removal, the photo tools, the "+ Add" list with pins that survive a reload, and the Windows tile's real
// download link. Real files, real engines (served from /vendor on our own origin), real downloads.
import { copyFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { sleep } from "../cdp.mjs";
import {
  activeTab, addFiles, allWedgeIds, ascii, check, clickWedge, decodeInBrowser, downloadResult, freshApp, hasPanel, isPng, runPanel, section,
  selectTab, setDownloadDir, setOption, waitResults, wedgeIds,
} from "../helpers.mjs";

const RELEASES = "https://github.com/hamwimoustafa88-maker/dropflow/releases/latest";
const fixture = (rel) => fileURLToPath(new URL(rel, import.meta.url));

export async function extras({ b, base, out, files }) {
  const dl = join(out, "dl-extras");
  await setDownloadDir(b, dl);
  const origin = new URL(base).origin;
  const ocrPng = join(files.dir, "ocr-ar-en.png");
  copyFileSync(fixture("../../src/engine/__tests__/fixtures/ocr-ar-en.png"), ocrPng);
  const heic = join(files.dir, "sample.heic");
  copyFileSync(fixture("../../../tests/samples/heic/RGB_8__128x128.heic"), heic);
  const tiff = join(files.dir, "quadrants.tiff");
  copyFileSync(fixture("../../src/engine/__tests__/fixtures/quadrants-lzw.tiff"), tiff);
  const fromOrigin = (re) => {
    const r = b.requests.filter((x) => re.test(x.url));
    return { n: r.length, ok: r.length > 0 && r.every((x) => new URL(x.url).origin === origin) };
  };

  await section("extras: the Windows tile has a real download link and lists only what the browser cannot do", async () => {
    for (const [locale, label, soon] of [["en", "Download for Windows", "Soon"], ["ar", "حمّل تطبيق ويندوز", "قريباً"]]) {
      await freshApp(b, base, locale);
      await addFiles(b, [files.png]);
      const tile = await b.ev(`(() => { const t = document.querySelector('[data-testid=windows-hint]'); const a = t?.querySelector('[data-testid=windows-download]');
        return t ? { text: t.innerText.replace(/\\s+/g, ' '), href: a?.href, label: a?.textContent.trim(), target: a?.target, rel: a?.rel } : null; })()`);
      check(`${locale}: tile links to the latest release, opens in a new tab, no "Soon"`, tile && tile.href === RELEASES && tile.label === label && tile.target === "_blank" && /noopener/.test(tile.rel) && !tile.text.includes(soon), JSON.stringify(tile));
      if (locale === "en") {
        check("en: for a picture it no longer lists OCR, Cutout, GIF or TIFF; HEIC and Annotate remain", !/OCR|Cutout|GIF|TIFF/.test(tile.text) && /HEIC/.test(tile.text) && /Annotate/.test(tile.text), tile.text);
      }
    }
  });

  await section("extras: image -> TXT (OCR, Arabic + English) straight from the Convert wheel", async () => {
    await freshApp(b, base, "en");
    await b.ev("localStorage.removeItem('dropflow.loaded.ocr'); localStorage.removeItem('dropflow.pins')");
    await addFiles(b, [ocrPng]);
    const ids = await allWedgeIds(b);
    check("the image Convert wheel offers TXT", ids.includes("convert:txt"), ids.join(","));
    const before = b.requests.filter((r) => r.url.includes("/vendor/tesseract/")).length;
    check("the OCR engine is not loaded before it is used", before === 0, String(before));
    await clickWedge(b, "convert:txt");
    const note = await b.ev("document.querySelector('[data-testid=options-panel] [role=note]')?.textContent ?? ''");
    check("first use: a short note says the text reader is downloaded once (about 8 MB) and the picture stays local", (await hasPanel(b)) && /8 MB/.test(note) && /stays on your device/.test(note), note);
    await runPanel(b);
    const names = await waitResults(b, 1, 180000);
    check("result is `ocr-ar-en (converted).txt`", names[0] === "ocr-ar-en (converted).txt", names[0]);
    const txt = (await downloadResult(b, dl, 0)).bytes.toString("utf8");
    check("the text has both lines: `Hello DropFlow 2026` and Arabic `مرحبا بالعالم` in reading order", txt.includes("Hello DropFlow 2026") && txt.includes("مرحبا") && txt.indexOf("مرحبا") < txt.indexOf("بالعالم"), JSON.stringify(txt));
    const tess = fromOrigin(/\/vendor\/tesseract\//);
    check("Tesseract (library, worker, core, ara+eng models) came from our own /vendor, nothing from a CDN", tess.ok && b.requests.some((r) => r.url.endsWith("/vendor/tesseract/lang/ara.traineddata.gz")), `${tess.n} requests`);
    await clickWedge(b, "convert:txt");
    await sleep(200);
    check("second time: no note, it runs straight away", !(await hasPanel(b)));
    await waitResults(b, 2, 120000);

    // A scanned PDF (the same picture as an image-only PDF): PDF -> TXT finds no text layer and falls back to OCR.
    await clickWedge(b, "convert:pdf");
    await waitResults(b, 3, 60000);
    const scanned = await downloadResult(b, dl, 2);
    const scannedPath = join(files.dir, "scanned.pdf");
    (await import("node:fs")).writeFileSync(scannedPath, scanned.bytes);
    await freshApp(b, base, "en");
    await addFiles(b, [scannedPath]);
    await clickWedge(b, "convert:txt");
    const pn = await waitResults(b, 1, 180000);
    const ptxt = (await downloadResult(b, dl, 0)).bytes.toString("utf8");
    check("scanned PDF -> TXT: no text layer, so the pages are read with OCR (both languages)", pn[0] === "scanned (text).txt" &&ptxt.includes("Hello DropFlow 2026") && ptxt.includes("مرحبا"), JSON.stringify(ptxt));
  });

  await section("extras: HEIC and TIFF in, PNG / GIF / TIFF out", async () => {
    await freshApp(b, base, "en");
    await addFiles(b, [heic]);
    const ids = await allWedgeIds(b);
    check("a HEIC file gets a Convert wheel (PNG, JPG, PDF ...)", ["convert:png", "convert:jpg", "convert:pdf"].every((i) => ids.includes(i)), ids.join(","));
    await clickWedge(b, "convert:png");
    const names = await waitResults(b, 1, 60000);
    const png = await downloadResult(b, dl, 0);
    const d = await decodeInBrowser(b, png.bytes, "image/png", 64, 64);
    check("HEIC -> PNG: a real 128x128 PNG", names[0] === "sample (converted).png" && isPng(png.bytes) && d.w === 128 && d.h === 128 && d.px[3] === 255, JSON.stringify(d));
    check("libheif came from our own /vendor/heif", fromOrigin(/\/vendor\/heif\//).ok);

    await freshApp(b, base, "en");
    await addFiles(b, [tiff]);
    await clickWedge(b, "convert:png");
    await waitResults(b, 1, 30000);
    const tp = await downloadResult(b, dl, 0);
    const td = await decodeInBrowser(b, tp.bytes, "image/png", 50, 5);
    check("TIFF (LZW, written by Windows) -> PNG keeps the exact colours", td.w === 64 && td.h === 48 && td.px.slice(0, 3).join() === "0,255,0", JSON.stringify(td));

    await freshApp(b, base, "en");
    await addFiles(b, [files.png]);
    await clickWedge(b, "convert:gif");
    await clickWedge(b, "convert:tiff");
    await waitResults(b, 2, 30000);
    const gif = await downloadResult(b, dl, 0);
    const gd = await decodeInBrowser(b, gif.bytes, "image/gif", 2, 2);
    check("PNG -> GIF: GIF89a that the browser decodes at 96x64, red corner kept", ascii(gif.bytes, 0, 6) === "GIF89a" && gd.w === 96 && gd.h === 64 && gd.px[0] > 200 && gd.px[1] < 60, JSON.stringify(gd));
    const tf = await downloadResult(b, dl, 1);
    check("PNG -> TIFF: a real TIFF header (UTIF writes big-endian MM 00 2A)", (ascii(tf.bytes, 0, 2) === "MM" && tf.bytes[3] === 42) || (ascii(tf.bytes, 0, 2) === "II" && tf.bytes[2] === 42), ascii(tf.bytes, 0, 4));
  });

  await section("extras: photo tools (edit, background, redact, icons) and remove background", async () => {
    await freshApp(b, base, "en");
    await b.ev("localStorage.removeItem('dropflow.loaded.rembg')");
    await addFiles(b, [files.png]);
    await selectTab(b, "tools");
    let n = 0;
    await clickWedge(b, "img.edit");
    await setOption(b, "rotate", "90°");
    await setOption(b, "filter", "Grayscale");
    await runPanel(b);
    await waitResults(b, ++n);
    const e = await decodeInBrowser(b, (await downloadResult(b, dl, n - 1)).bytes, "image/png", 10, 10);
    check("edit: rotate 90 swaps the size (96x64 -> 64x96) and grayscale makes R=G=B", e.w === 64 && e.h === 96 && e.px[0] === e.px[1] && e.px[1] === e.px[2], JSON.stringify(e));

    await clickWedge(b, "img.background");
    await setOption(b, "background", "#0e7490");
    await setOption(b, "aspect", "1:1");
    await runPanel(b);
    await waitResults(b, ++n);
    const bg = await decodeInBrowser(b, (await downloadResult(b, dl, n - 1)).bytes, "image/png", 1, 1);
    check("background: square canvas on #0e7490", bg.w === bg.h && bg.w > 96 && bg.px.slice(0, 3).join() === "14,116,144", JSON.stringify(bg));

    await clickWedge(b, "img.redact");
    await setOption(b, "regions", "0,0,20,20");
    await runPanel(b);
    await waitResults(b, ++n);
    const rd = await decodeInBrowser(b, (await downloadResult(b, dl, n - 1)).bytes, "image/png", 5, 5);
    check("redact: the area is solid black", rd.px.slice(0, 3).join() === "0,0,0", JSON.stringify(rd));

    await clickWedge(b, "img.icons");
    await runPanel(b);
    const names = await waitResults(b, n + 2);
    n += 2;
    check("icons: an .ico and a favicon .zip", names.some((x) => /\(icons 1\)\.ico$/.test(x)) && names.some((x) => /\(icons 2\)\.zip$/.test(x)), names.join(","));

    await clickWedge(b, "img.remove-bg");
    const note = await b.ev("document.querySelector('[data-testid=options-panel] [role=note]')?.textContent ?? ''");
    check("remove background: first-use note names the size (about 18 MB)", /18 MB/.test(note), note);
    await runPanel(b);
    const cut = await waitResults(b, ++n, 180000);
    const cp = await downloadResult(b, dl, n - 1);
    const cd = await decodeInBrowser(b, cp.bytes, "image/png", 48, 32);
    check("remove background: a PNG cut-out of the same size", /\(cutout\)\.png$/.test(cut.at(-1)) && isPng(cp.bytes) && cd.w === 96 && cd.h === 64, JSON.stringify(cd));
    const ort = fromOrigin(/\/vendor\/(ort|models)\//);
    check("ONNX Runtime and the U2-Net-P model came from our own /vendor", ort.ok && b.requests.some((r) => r.url.endsWith("/vendor/models/u2netp.onnx")), `${ort.n} requests`);
  });

  await section("extras: + Add puts TXT on the image Tools wheel, it runs, and it is still there after a reload", async () => {
    await freshApp(b, base, "en");
    await b.ev("localStorage.removeItem('dropflow.pins')");
    await addFiles(b, [ocrPng]);
    await selectTab(b, "tools");
    const tools = await allWedgeIds(b);
    check("the Tools wheel ends with + Add and has no TXT yet", tools.at(-1) === "wheel.add" && !tools.includes("convert:txt"), tools.join(","));
    await clickWedge(b, "wheel.add");
    await b.waitFor("!!document.querySelector('[data-testid=add-list]')", 5000, "add list");
    const row = await b.ev("document.querySelector('[data-add=\"convert:txt\"]')?.innerText.replace(/\\s+/g, ' ')");
    check("the + Add list offers `TXT Text (OCR)` under Convert to", /TXT/.test(row ?? "") && /Text \(OCR\)/.test(row ?? ""), String(row));
    check("keyboard: focus starts on the first row; ArrowDown moves", await b.ev("document.activeElement?.hasAttribute('data-add-row')"));
    const first = await b.ev("document.activeElement?.getAttribute('data-add')");
    await b.key("ArrowDown", { code: "ArrowDown", vk: 40 });
    check("ArrowDown moves to the next row", (await b.ev("document.activeElement?.getAttribute('data-add')")) !== first);
    await b.key("Escape", { code: "Escape", vk: 27 });
    await sleep(200);
    check("Esc closes the list and returns to the wheel", !(await b.ev("!!document.querySelector('[data-testid=add-list]')")) && (await wedgeIds(b)).length > 0 && (await activeTab(b)) === "tools");
    await clickWedge(b, "wheel.add");
    await b.waitFor("!!document.querySelector('[data-testid=add-list]')", 5000, "add list");
    await b.click('[data-add="convert:txt"]');
    const names = await waitResults(b, 1, 120000);
    check("picking it runs it now (a .txt result)", /\.txt$/.test(names[0]), names[0]);
    const after = await allWedgeIds(b);
    check("TXT is now on the Tools wheel, marked as added", after.includes("convert:txt") && (await b.ev("!!document.querySelector('[data-wedge=\"convert:txt\"][data-pinned=true]')")), after.join(","));
    check("saved on this device only (localStorage)", (await b.ev("localStorage.getItem('dropflow.pins')")) === JSON.stringify({ tools: { image: ["convert:txt"] } }));

    await freshApp(b, base, "en"); // reload
    await addFiles(b, [files.png]);
    await selectTab(b, "tools");
    const reloaded = await allWedgeIds(b);
    check("after a reload, another picture's Tools wheel still has TXT", reloaded.includes("convert:txt"), reloaded.join(","));
    await clickWedge(b, "wheel.add");
    await b.waitFor("!!document.querySelector('[data-testid=add-list]')", 5000, "add list");
    await b.click('[data-unpin="convert:txt"]');
    await sleep(200);
    await b.click("[data-testid=add-back]");
    await sleep(200);
    check("removing it (x in the list) takes it off the wheel", !(await allWedgeIds(b)).includes("convert:txt"));
    check("and off the device", (await b.ev("localStorage.getItem('dropflow.pins')")) === "{}");
  });

  await section("extras: batch rename makes renamed copies; collage combines pictures", async () => {
    await freshApp(b, base, "en");
    await addFiles(b, [files.png, files.png2]);
    await selectTab(b, "tools");
    await clickWedge(b, "gen.rename");
    await setOption(b, "pattern", "trip-{counter:000}{ext}");
    await runPanel(b);
    const names = await waitResults(b, 2);
    check("rename: `trip-001.png`, `trip-002.png` (copies; the originals are untouched)", names.join(",") === "trip-001.png,trip-002.png", names.join(","));
    const r1 = await downloadResult(b, dl, 1);
    check("the renamed copy has the original bytes (second.png, 40x40)", isPng(r1.bytes) && (await decodeInBrowser(b, r1.bytes, "image/png")).w === 40);
    await clickWedge(b, "img.collage");
    await setOption(b, "cell", 100);
    await setOption(b, "spacing", 10);
    await runPanel(b);
    await waitResults(b, 3);
    const c = await downloadResult(b, dl, 2);
    const cd = await decodeInBrowser(b, c.bytes, "image/jpeg", 0, 0);
    check("collage of two: a 230x120 JPEG grid (2 cells of 100 + 10 px spacing) on white", cd.w === 230 && cd.h === 120 && cd.px[0] > 240, JSON.stringify(cd));
  });

  await section("extras: + Add list in Arabic at 390 px (RTL, no horizontal overflow)", async () => {
    await b.setViewport(390, 844, "light", true);
    try {
      await freshApp(b, base, "ar");
      await addFiles(b, [files.png]);
      await clickWedge(b, "wheel.add");
      await b.waitFor("!!document.querySelector('[data-testid=add-list]')", 5000, "add list");
      const info = await b.ev(`(() => { let worst = 0; for (const e of document.querySelectorAll('[data-testid=add-list] *')) { const r = e.getBoundingClientRect(); if (r.width) worst = Math.max(worst, r.right - innerWidth, -r.left); }
        const l = document.querySelector('[data-testid=add-list]'); return { worst, dir: getComputedStyle(l).direction, title: l.querySelector('h2').textContent }; })()`);
      check("390 px Arabic: the list fits (no edge past the screen), reads right-to-left", info.worst <= 0.5 && info.dir === "rtl" && /إضافة إلى/.test(info.title), JSON.stringify(info));
    } finally {
      await b.setViewport(1280, 900, "dark", false);
    }
  });
}
