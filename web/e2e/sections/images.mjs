import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { PDFDocument } from "pdf-lib";
import { sleep } from "../cdp.mjs";
import {
  addFiles, ascii, check, chipNames, clickWedge, decodeInBrowser, downloadResult, freshApp, hasPanel, isJpeg, isPdf, isPng, runPanel,
  section, selectTab, setDownloadDir, setOption, waitResults, wedgeIds,
} from "../helpers.mjs";

export async function images({ b, base, out, files }) {
  const dl = join(out, "dl-images");
  await setDownloadDir(b, dl);

  await section("images: wheel is truthful and converts for real", async () => {
    await freshApp(b, base, "en");
    await addFiles(b, [files.png]);
    check("file chip shows the name", (await chipNames(b)).includes("photo.png"));
    const ids = await wedgeIds(b);
    const caps = await b.ev(`(async () => {
      const enc = (m) => new Promise((r) => { const c = document.createElement('canvas'); c.width = c.height = 4; c.toBlob((x) => r(x?.type === m), m); });
      return { webp: await enc('image/webp'), avif: await enc('image/avif') };
    })()`);
    check("convert wheel: JPG BMP ICO PDF always, never PNG->PNG", ["convert:jpg", "convert:bmp", "convert:ico", "convert:pdf"].every((i) => ids.includes(i)) && !ids.includes("convert:png"), ids.join(","));
    check(`WEBP wedge present iff this browser can encode it (${caps.webp})`, ids.includes("convert:webp") === caps.webp);
    check(`AVIF wedge present iff this browser can encode it (${caps.avif}) - never faked`, ids.includes("convert:avif") === caps.avif);
    check("HEIC / TIFF / GIF are never offered", !ids.some((i) => /heic|tiff|gif/.test(i)));

    // PNG -> JPG
    await clickWedge(b, "convert:jpg");
    const names = await waitResults(b, 1);
    check("output is named `photo (converted).jpg`, never the input name", names[0] === "photo (converted).jpg", names[0]);
    const jpg = await downloadResult(b, dl, 0);
    check("downloaded file is a real JPEG", isJpeg(jpg.bytes), jpg.name);
    const d = await decodeInBrowser(b, jpg.bytes, "image/jpeg", 2, 2);
    check("JPEG decodes at 96x64 and the red corner survived", d.w === 96 && d.h === 64 && d.px[0] > 200 && d.px[1] < 60, JSON.stringify(d));
  });

  await section("images: BMP and ICO encoders (our own) produce files the browser decodes", async () => {
    await freshApp(b, base, "en");
    await addFiles(b, [files.png]);
    await clickWedge(b, "convert:bmp");
    await clickWedge(b, "convert:ico");
    await waitResults(b, 2);
    const bmp = await downloadResult(b, dl, 0);
    check("BMP starts with BM and has a 24-bit DIB header", ascii(bmp.bytes, 0, 2) === "BM" && bmp.bytes.readUInt16LE(28) === 24, ascii(bmp.bytes, 0, 2));
    const bd = await decodeInBrowser(b, bmp.bytes, "image/bmp", 2, 2);
    check("browser decodes our BMP: 96x64, red corner exact", bd.w === 96 && bd.h === 64 && bd.px[0] === 255 && bd.px[1] === 0 && bd.px[2] === 0, JSON.stringify(bd));
    const ico = await downloadResult(b, dl, 1);
    check("ICO has a valid ICONDIR (type 1, >=2 sizes) with PNG payloads", ico.bytes.readUInt16LE(2) === 1 && ico.bytes.readUInt16LE(4) >= 2 && ico.bytes.includes(Buffer.from([137, 80, 78, 71])), `images=${ico.bytes.readUInt16LE(4)}`);
    const id = await decodeInBrowser(b, ico.bytes, "image/x-icon");
    check("browser decodes our ICO", id.w >= 16 && id.w <= 256, JSON.stringify(id));
  });

  await section("images: Arabic file name, WEBP, PDF", async () => {
    await freshApp(b, base, "ar");
    await addFiles(b, [files.pngArabic]);
    await clickWedge(b, "convert:jpg");
    const names = await waitResults(b, 1);
    check("Arabic name is preserved: `صورة (converted).jpg`", names[0] === "صورة (converted).jpg", names[0]);
    const ids = await wedgeIds(b);
    let n = 1;
    if (ids.includes("convert:webp")) {
      await clickWedge(b, "convert:webp");
      await waitResults(b, ++n);
      const w = await downloadResult(b, dl, n - 1);
      check("WEBP output is RIFF/WEBP", ascii(w.bytes, 0, 4) === "RIFF" && ascii(w.bytes, 8, 12) === "WEBP");
    }
    await clickWedge(b, "convert:pdf");
    await waitResults(b, ++n);
    const pdf = await downloadResult(b, dl, n - 1);
    check("image -> PDF is a valid 1-page PDF (re-opened with pdf-lib)", isPdf(pdf.bytes) && (await PDFDocument.load(pdf.bytes)).getPageCount() === 1);
  });

  await section("images: resize, crop with the generated options panel", async () => {
    await freshApp(b, base, "en");
    await addFiles(b, [files.png]);
    await selectTab(b, "tools");
    const tools = await wedgeIds(b);
    check(
      "tools wheel has real image tools + zip/hash/base64, but not unimplemented ones",
      ["img.compress", "img.resize", "img.crop", "img.strip-gps", "img.metadata", "arc.zip", "gen.hash", "gen.base64"].every((t) => tools.includes(t)) &&
        !tools.includes("img.edit") && !tools.includes("img.ocr") && !tools.includes("img.to-pdf"),
      tools.join(","),
    );

    await clickWedge(b, "img.resize");
    check("resize opens a compact options panel", await hasPanel(b));
    await setOption(b, "width", 32);
    await runPanel(b);
    await waitResults(b, 1);
    const r = await downloadResult(b, dl, 0);
    const rd = await decodeInBrowser(b, r.bytes, "image/png");
    check("resize 96x64 -> width 32 keeps the ratio (32x21)", isPng(r.bytes) && rd.w === 32 && rd.h === 21, JSON.stringify(rd));

    await clickWedge(b, "img.crop");
    await setOption(b, "ratio", "1:1");
    await runPanel(b);
    await waitResults(b, 2);
    const c = await downloadResult(b, dl, 1);
    const cd = await decodeInBrowser(b, c.bytes, "image/png");
    check("crop 1:1 of 96x64 gives 64x64", cd.w === 64 && cd.h === 64, JSON.stringify(cd));
  });

  await section("images: strip GPS (lossless + verified), view metadata, compress", async () => {
    await freshApp(b, base, "en");
    // make a real JPEG in the browser, then add an Exif APP1 with Make/Model + GPS to it
    const b64 = await b.ev(`(async () => {
      const c = document.createElement('canvas'); c.width = 120; c.height = 80; const x = c.getContext('2d');
      const g = x.createLinearGradient(0, 0, 120, 80); g.addColorStop(0, '#f43'); g.addColorStop(1, '#36f'); x.fillStyle = g; x.fillRect(0, 0, 120, 80);
      const blob = await new Promise((r) => c.toBlob(r, 'image/jpeg', 0.95));
      const u8 = new Uint8Array(await blob.arrayBuffer()); let s = ''; for (const v of u8) s += String.fromCharCode(v); return btoa(s);
    })()`);
    const plain = Buffer.from(b64, "base64");
    const geo = files.addExifGps(plain);
    writeFileSync(files.exifJpegPath, geo);
    check("test JPEG really carries Exif + GPS before stripping", geo.includes(Buffer.from("Exif\0\0")) && geo.includes(Buffer.from("DropFlowCam")));

    await addFiles(b, [files.exifJpegPath]);
    await selectTab(b, "tools");
    await clickWedge(b, "img.strip-gps");
    await waitResults(b, 1);
    const s = await downloadResult(b, dl, 0);
    check("stripped JPEG has no Exif/GPS/Make/Model left", isJpeg(s.bytes) && !s.bytes.includes(Buffer.from("Exif\0\0")) && !s.bytes.includes(Buffer.from("DropFlowCam")));
    const sos = (buf) => buf.indexOf(Buffer.from([0xff, 0xda]));
    check("strip is LOSSLESS: the compressed image data is byte-identical", Buffer.compare(s.bytes.subarray(sos(s.bytes)), geo.subarray(sos(geo))) === 0);
    const sd = await decodeInBrowser(b, s.bytes, "image/jpeg");
    check("stripped JPEG still decodes at 120x80", sd.w === 120 && sd.h === 80);

    await clickWedge(b, "img.metadata");
    check("metadata opens a panel (view / remove)", await hasPanel(b));
    await runPanel(b); // default action: view
    await waitResults(b, 2);
    const m = await downloadResult(b, dl, 1);
    const text = m.bytes.toString("utf8");
    check("metadata report lists camera and GPS coordinates", text.includes("DropFlowCam") && /25\.2/.test(text) && /55\.27/.test(text), text.slice(0, 200).replace(/\n/g, " | "));
    check("a text result offers `Copy text`", await b.ev("[...document.querySelectorAll('[data-testid=result] button')].some(x => /Copy text/.test(x.textContent))"));

    await clickWedge(b, "img.compress");
    await setOption(b, "quality", 20);
    await runPanel(b);
    await sleep(1200);
    const jobs = await b.ev("[...document.querySelectorAll('[data-testid=jobs] li')].map(e => e.textContent)");
    const names = await b.ev("[...document.querySelectorAll('[data-testid=result] p:first-child')].map(e => e.textContent)");
    check("compress either produced a smaller file or honestly says it is already small", names.some((n) => /compressed/.test(n)) || jobs.some((t) => /small/i.test(t)), JSON.stringify({ names, jobs }));
  });

  await section("images: two images -> one PDF (img.to-pdf is multi)", async () => {
    await freshApp(b, base, "en");
    await addFiles(b, [files.png, files.png2]);
    await selectTab(b, "tools");
    const tools = await wedgeIds(b);
    check("img.to-pdf appears only with 2+ images", tools.includes("img.to-pdf"));
    await clickWedge(b, "img.to-pdf");
    await runPanel(b);
    const names = await waitResults(b, 1);
    check("one PDF named after the first input", names.length === 1 && names[0] === "photo (pdf).pdf", names.join(","));
    const pdf = await downloadResult(b, dl, 0);
    const doc = await PDFDocument.load(pdf.bytes);
    check("PDF has 2 pages (one per image)", doc.getPageCount() === 2, String(doc.getPageCount()));
  });
}
