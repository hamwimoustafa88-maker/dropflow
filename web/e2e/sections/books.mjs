// E-books, comics and fonts in the real browser: EPUB/FB2 conversions, CBZ -> PDF/EPUB (with a WebP page re-encoded by the worker's
// OffscreenCanvas), PDF -> CBZ (pdf.js), TTF -> EOT (the wrapped font must still load as a FontFace), and the honest refusals:
// WOFF2 (no Brotli in browsers) and CBR (RAR) only point at the Windows app.
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { PDFDocument } from "pdf-lib";
import { unzipSync, zipSync } from "fflate";
import {
  addFiles, check, clickWedge, decodeInBrowser, downloadResult, freshApp, q, section, setDownloadDir, tabEnabled, waitResults, wedgeIds,
} from "../helpers.mjs";
import { makePng } from "../fixtures.mjs";

const enc = new TextEncoder();
const text = (b) => Buffer.from(b ?? []).toString("utf8");
const AR_P = "كان صباحاً هادئاً على شاطئ بيروت.";

function arabicEpub() {
  const xhtml = (body) =>
    `<?xml version="1.0" encoding="utf-8"?><html xmlns="http://www.w3.org/1999/xhtml" xml:lang="ar" lang="ar" dir="rtl"><head><title>x</title></head><body>${body}</body></html>`;
  return zipSync({
    mimetype: [enc.encode("application/epub+zip"), { level: 0 }],
    "META-INF/container.xml": enc.encode('<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OPS/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>'),
    "OPS/book.opf": enc.encode(
      '<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="i"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="i">x</dc:identifier><dc:title>رحلة إلى البحر</dc:title><dc:language>ar</dc:language></metadata>' +
        '<manifest><item id="a" href="a.xhtml" media-type="application/xhtml+xml"/><item id="b" href="b.xhtml" media-type="application/xhtml+xml"/><item id="p" href="sea.png" media-type="image/png"/></manifest><spine><itemref idref="a"/><itemref idref="b"/></spine></package>',
    ),
    "OPS/a.xhtml": enc.encode(xhtml(`<h1>الفصل الأول</h1><p onclick="x()">${AR_P} <b>بقوة</b></p><script>alert(1)</script><p><img src="sea.png" alt="البحر"/></p><p><a href="b.xhtml#w">الأمواج</a></p>`)),
    "OPS/b.xhtml": enc.encode(xhtml('<h1>الفصل الثاني</h1><p id="w">ثم بدأت الأمواج تعلو.</p><ul><li>أولاً</li><li>ثانياً</li></ul>')),
    "OPS/sea.png": new Uint8Array(makePng(30, 20)),
  });
}

/** The first Windows font that exists (the e2e runs on the developer's Windows PC; elsewhere the font check is skipped). */
function systemFont() {
  for (const p of ["C:/Windows/Fonts/arial.ttf", "C:/Windows/Fonts/segoeui.ttf", "C:/Windows/Fonts/tahoma.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"]) if (existsSync(p)) return p;
  return null;
}

export async function books({ b, base, out }) {
  const dl = join(out, "dl-books");
  await setDownloadDir(b, dl);

  await section("e-books: Arabic EPUB -> FB2 / TXT / HTML, then FB2 -> EPUB (right to left, valid OCF)", async () => {
    const epubPath = join(out, "رحلة.epub");
    writeFileSync(epubPath, arabicEpub());
    await freshApp(b, base, "ar");
    await addFiles(b, [epubPath]);
    const ids = (await wedgeIds(b)).filter((i) => i !== "wheel.add");
    check("epub Convert wheel = TXT, MD, HTML, FB2, HTMLZ, TXTZ (PDF/Word stay with the Windows app)", JSON.stringify(ids) === JSON.stringify(["convert:txt", "convert:md", "convert:html", "convert:fb2", "convert:htmlz", "convert:txtz"]), ids.join(","));
    const hint = await b.ev("document.querySelector('[data-testid=windows-hint]')?.innerText.replace(/\\s+/g, ' ') ?? ''");
    check("the Windows-app hint names PDF and DOCX for this book", /PDF/.test(hint) && /DOCX/.test(hint), hint);
    await clickWedge(b, "convert:fb2");
    await clickWedge(b, "convert:txt");
    await clickWedge(b, "convert:html");
    const names = await waitResults(b, 3);
    const at = (ext) => names.indexOf(`رحلة (converted).${ext}`); // the three jobs run side by side: any order
    check("results keep the Arabic name", at("fb2") >= 0 && at("txt") >= 0 && at("html") >= 0, names.join(","));
    const fb2 = (await downloadResult(b, dl, at("fb2"))).bytes;
    const fb2Text = fb2.toString("utf8");
    check("FB2: FictionBook with <lang>ar</lang>, the Arabic paragraph, a PNG binary, no script", fb2Text.includes("<FictionBook") && fb2Text.includes("<lang>ar</lang>") && fb2Text.includes(AR_P) && /<binary id="image1\.png" content-type="image\/png">/.test(fb2Text) && !/<script|onclick/.test(fb2Text), fb2Text.slice(0, 160));
    const txt = (await downloadResult(b, dl, at("txt"))).bytes;
    check("TXT: UTF-8 with BOM, chapters and list in reading order", txt[0] === 0xef && txt.toString("utf8").includes(`${AR_P} بقوة`) && txt.toString("utf8").indexOf("الفصل الأول") < txt.toString("utf8").indexOf("الفصل الثاني") && txt.toString("utf8").includes("- أولاً\n- ثانياً"), JSON.stringify(txt.toString("utf8").slice(0, 120)));
    const html = (await downloadResult(b, dl, at("html"))).bytes.toString("utf8");
    check("HTML: one page, lang=ar dir=rtl, picture inlined, no script", /<html lang="ar" dir="rtl">/.test(html) && html.includes('src="data:image/png;base64,') && !/<script|onclick/.test(html), html.slice(0, 80));
    // The browser lays the page out right to left.
    const dir = await b.ev(`(async () => { const d = new DOMParser().parseFromString(${q(html)}, "text/html"); return d.documentElement.dir + "|" + d.querySelectorAll("section").length; })()`);
    check("the HTML parses in the browser: rtl, two chapters", dir === "rtl|2", dir);

    const fb2Path = join(out, "رحلة2.fb2");
    writeFileSync(fb2Path, fb2);
    await freshApp(b, base, "en");
    await addFiles(b, [fb2Path]);
    await clickWedge(b, "convert:epub");
    await waitResults(b, 1);
    const epub = new Uint8Array((await downloadResult(b, dl, 0)).bytes);
    check("EPUB: the stored mimetype comes first", Buffer.from(epub.subarray(30, 58)).toString() === "mimetypeapplication/epub+zip");
    const files = unzipSync(epub);
    const opf = text(files["OEBPS/content.opf"]);
    const chapters = Object.keys(files).filter((n) => /chapter\d+\.xhtml$/.test(n));
    // The FB2 body opens with the book's title (as the Windows app writes it), which becomes a title chapter of its own.
    check("EPUB: rtl spine, Arabic language, title page + two chapters, picture file", opf.includes('page-progression-direction="rtl"') && opf.includes("<dc:language>ar</dc:language>") && chapters.length === 3 && !!files["OEBPS/images/image1.png"], `${chapters.join(",")} ${opf.slice(0, 80)}`);
    const xmlOk = await b.ev(`(() => { const p = new DOMParser(); return ${q(chapters.map((c) => text(files[c])))}.every((x) => !p.parseFromString(x, "application/xhtml+xml").querySelector("parsererror")); })()`);
    check("every chapter is well-formed XHTML for the browser's own XML parser", xmlOk === true);
    // (Links between chapters are covered by the unit tests: FB2, like on Windows, keeps only web links.)
  });

  await section("comics: CBZ -> PDF and EPUB (WebP page re-encoded in the worker), CBR points at the Windows app", async () => {
    // A WebP page made by this browser's own encoder.
    const webp = await b.ev(`(async () => { const c = new OffscreenCanvas(25, 35); const x = c.getContext("2d"); x.fillStyle = "#3a7"; x.fillRect(0, 0, 25, 35); const bl = await c.convertToBlob({ type: "image/webp" }); return bl.type === "image/webp" ? [...new Uint8Array(await bl.arrayBuffer())] : null; })()`);
    const pages = { "c/page10.png": new Uint8Array(makePng(50, 10)), "c/page2.png": new Uint8Array(makePng(30, 20)), "c/page1.png": new Uint8Array(makePng(40, 60)) };
    if (webp) pages["c/page11.webp"] = new Uint8Array(webp);
    const cbzPath = join(out, "قصة.cbz");
    writeFileSync(cbzPath, zipSync({ ...pages, "ComicInfo.xml": enc.encode("<ComicInfo><Title>مانغا</Title><Manga>YesAndRightToLeft</Manga></ComicInfo>") }));
    await freshApp(b, base, "en");
    await addFiles(b, [cbzPath]);
    const ids = (await wedgeIds(b)).filter((i) => i !== "wheel.add");
    check("cbz Convert wheel = PDF, EPUB", JSON.stringify(ids) === JSON.stringify(["convert:pdf", "convert:epub"]), ids.join(","));
    await clickWedge(b, "convert:pdf");
    await clickWedge(b, "convert:epub");
    const comicNames = await waitResults(b, 2);
    const pdf = await PDFDocument.load(new Uint8Array((await downloadResult(b, dl, comicNames.findIndex((n) => n.endsWith(".pdf")))).bytes));
    const sizes = pdf.getPages().map((p) => `${p.getWidth()}x${p.getHeight()}`);
    const expected = ["40x60", "30x20", "50x10", ...(webp ? ["25x35"] : [])];
    check("PDF: one page per picture in natural order, sized to it", JSON.stringify(sizes) === JSON.stringify(expected), sizes.join(","));
    check("PDF: the ComicInfo title", pdf.getTitle() === "مانغا", pdf.getTitle());
    const files = unzipSync(new Uint8Array((await downloadResult(b, dl, comicNames.findIndex((n) => n.endsWith(".epub")))).bytes));
    const opf = text(files["OEBPS/content.opf"]);
    check("EPUB: fixed layout, manga reads right to left, WebP page kept as WebP", opf.includes("pre-paginated") && opf.includes('page-progression-direction="rtl"') && (!webp || !!files["OEBPS/images/page004.webp"]), Object.keys(files).join(","));

    const cbrPath = join(out, "old.cbr");
    writeFileSync(cbrPath, Buffer.from([0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x01, 0x00, 1, 2, 3, 4]));
    await freshApp(b, base, "en");
    await addFiles(b, [cbrPath]);
    const hint = await b.ev("document.querySelector('[data-testid=windows-hint]')?.innerText.replace(/\\s+/g, ' ') ?? ''");
    check("cbr: Convert tab disabled, the Windows-app hint lists PDF / EPUB / CBZ", (await tabEnabled(b, "convert")) === false && /PDF/.test(hint) && /CBZ/.test(hint), hint);

    // A RAR renamed .cbz is refused with a clear message (no crash).
    const fakePath = join(out, "renamed.cbz");
    writeFileSync(fakePath, Buffer.from([0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x01, 0x00, 9, 9, 9, 9]));
    await freshApp(b, base, "en");
    await addFiles(b, [fakePath]);
    await clickWedge(b, "convert:pdf");
    let jobs = [];
    for (let i = 0; i < 40 && !jobs.some((t) => /Windows app/.test(t)); i++) {
      await new Promise((r) => setTimeout(r, 150));
      jobs = await b.ev("[...document.querySelectorAll('[data-testid=jobs] li')].map(e => e.textContent)");
    }
    check("a RAR named .cbz: `RAR and 7z comics need the Windows app`", jobs.some((t) => /RAR and 7z comics need the Windows app/.test(t)), JSON.stringify(jobs));
  });

  await section("pdf -> cbz: pages drawn by pdf.js at 200 DPI", async () => {
    const { makePdf } = await import("../fixtures.mjs");
    const pdfPath = join(out, "مجلة.pdf");
    writeFileSync(pdfPath, await makePdf(2, "Page"));
    await freshApp(b, base, "en");
    await addFiles(b, [pdfPath]);
    await clickWedge(b, "convert:cbz");
    const names = await waitResults(b, 1, 60000);
    check("result `مجلة (converted).cbz`", names[0] === "مجلة (converted).cbz", names[0]);
    const z = unzipSync(new Uint8Array((await downloadResult(b, dl, 0)).bytes));
    check("CBZ: 001.jpg, 002.jpg and ComicInfo.xml", JSON.stringify(Object.keys(z)) === JSON.stringify(["001.jpg", "002.jpg", "ComicInfo.xml"]), Object.keys(z).join(","));
    const img = await decodeInBrowser(b, z["001.jpg"], "image/jpeg", 2, 2);
    check("page 1 is a 200 DPI JPEG of a 300x200 pt page (about 834x556), white background", Math.abs(img.w - 834) <= 2 && Math.abs(img.h - 556) <= 2 && img.px[0] > 240, JSON.stringify(img));
  });

  await section("fonts: TTF -> EOT (the font inside still loads), WOFF2 only points at the Windows app", async () => {
    const font = systemFont();
    if (!font) {
      check("fonts: a system TrueType font is available (skipped: none found)", true);
    } else {
      const ttfPath = join(out, "خط.ttf");
      writeFileSync(ttfPath, readFileSync(font));
      await freshApp(b, base, "en");
      await addFiles(b, [ttfPath]);
      const ids = (await wedgeIds(b)).filter((i) => i !== "wheel.add");
      check("ttf Convert wheel = WOFF, EOT (no WOFF2: no Brotli in this browser)", JSON.stringify(ids) === JSON.stringify(["convert:woff", "convert:eot"]), ids.join(","));
      await clickWedge(b, "convert:eot");
      await clickWedge(b, "convert:woff");
      const fontNames = await waitResults(b, 2);
      const eot = (await downloadResult(b, dl, fontNames.findIndex((n) => n.endsWith(".eot")))).bytes;
      const source = readFileSync(font);
      const dataSize = eot.readUInt32LE(4);
      check("EOT: size field, version 0x00020001, magic 0x504C, the TTF stored unchanged at the end", eot.readUInt32LE(0) === eot.length && eot.readUInt32LE(8) === 0x00020001 && eot.readUInt16LE(34) === 0x504c && dataSize === source.length && eot.subarray(eot.length - dataSize).equals(source), `${eot.length} ${dataSize}`);
      const loads = await b.ev(`(async () => { const u8 = Uint8Array.from(atob(${q(eot.subarray(eot.length - dataSize).toString("base64"))}), c => c.charCodeAt(0)); const f = new FontFace("eotInner", u8); await f.load(); return f.status; })()`);
      check("the font inside the EOT loads as a FontFace", loads === "loaded", loads);
      const woff = (await downloadResult(b, dl, fontNames.findIndex((n) => n.endsWith(".woff")))).bytes;
      const woffLoads = await b.ev(`(async () => { const u8 = Uint8Array.from(atob(${q(woff.toString("base64"))}), c => c.charCodeAt(0)); const f = new FontFace("woffOut", u8); await f.load(); return f.status; })()`);
      check("the WOFF loads as a FontFace", woffLoads === "loaded", woffLoads);
    }
    const woff2Path = join(out, "web.woff2");
    writeFileSync(woff2Path, Buffer.concat([Buffer.from("wOF2"), Buffer.alloc(60)]));
    await freshApp(b, base, "en");
    await addFiles(b, [woff2Path]);
    const hint = await b.ev("document.querySelector('[data-testid=windows-hint]')?.innerText.replace(/\\s+/g, ' ') ?? ''");
    check("woff2: Convert tab disabled, the Windows-app hint lists TTF and WOFF", (await tabEnabled(b, "convert")) === false && /TTF/.test(hint) && /WOFF/.test(hint), hint);
    const brotli = await b.ev(`(() => { try { new DecompressionStream("brotli"); return "decompress"; } catch { return "none"; } })()`);
    // When this fails, the browser gained a Brotli stream: WOFF2 could then be done here too (port Woff2.cs; see fonts.ts).
    check("this browser has no Brotli stream, so WOFF2 rightly stays with the Windows app", brotli === "none", brotli);
  });
}
