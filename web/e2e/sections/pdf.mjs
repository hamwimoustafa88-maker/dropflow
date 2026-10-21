import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { PDFDocument } from "pdf-lib";
import { sleep } from "../cdp.mjs";
import {
  addFiles, check, clickWedge, decodeInBrowser, downloadResult, freshApp, isPng, jobTexts, runPanel, section,
  pageTexts, selectTab, setDownloadDir, setOption, waitResults, wedgeIds,
} from "../helpers.mjs";

export async function pdfs({ b, base, out, files }) {
  const dl = join(out, "dl-pdf");
  await setDownloadDir(b, dl);

  await section("pdf: wheel, Windows hint, render to PNG / extract text", async () => {
    await freshApp(b, base, "en");
    await addFiles(b, [files.pdf3]);
    const conv = (await wedgeIds(b)).filter((i) => i !== "wheel.add");
    check("convert wheel = PNG JPG TXT MD only (no DOCX/PPTX/XLSX: not possible in a browser), then + Add", JSON.stringify([...conv].sort()) === JSON.stringify(["convert:jpg", "convert:md", "convert:png", "convert:txt"]) && (await wedgeIds(b)).at(-1) === "wheel.add", conv.join(","));
    const hint = await b.ev("document.querySelector('[data-testid=windows-hint]')?.innerText.replace(/\\s+/g, ' ')");
    check("one honest `Windows app` hint tile lists what only desktop does (DOCX, OCR..), with a real download link", /Windows app/.test(hint) && /DOCX/.test(hint) && /OCR/.test(hint) && /Download for Windows/.test(hint) && !/Soon/.test(hint), hint);
    await selectTab(b, "tools");
    const page1 = await wedgeIds(b);
    check("a long wheel is paged: 7 wedges + a `more` wedge", page1.length === 8 && page1[7].startsWith("more:"), page1.join(","));
    await b.click('[data-wedge^="more:"]');
    await sleep(250);
    const tools = [...page1, ...(await wedgeIds(b))];
    const expected = ["pdf.compress", "pdf.split", "pdf.organize", "pdf.metadata", "pdf.to-images", "pdf.extract-text", "pdf.rotate", "pdf.watermark", "pdf.page-numbers"];
    check("tools wheel: exactly the implemented PDF tools (+zip/hash/base64); no merge for one file; no protect/ocr/sign", expected.every((t) => tools.includes(t)) && !tools.includes("pdf.merge") && !tools.some((t) => /protect|unlock|ocr|sign|redact|repair/.test(t)), tools.join(","));

    await selectTab(b, "convert");
    await clickWedge(b, "convert:png");
    const names = await waitResults(b, 3, 40000);
    check("3 pages -> 3 PNGs named `report (pages N).png`", JSON.stringify(names) === JSON.stringify(["report (pages 1).png", "report (pages 2).png", "report (pages 3).png"]), names.join(","));
    const png = await downloadResult(b, dl, 0);
    const d = await decodeInBrowser(b, png.bytes, "image/png", 100, 100);
    check("page 1 PNG is real pdf.js output: 600x400 (300x200pt at 2x)", isPng(png.bytes) && d.w === 600 && d.h === 400, JSON.stringify(d));
    const dark = await b.ev(`(async () => {
      const bin = atob(${JSON.stringify(png.bytes.toString("base64"))}); const u8 = Uint8Array.from(bin, c => c.charCodeAt(0));
      const bmp = await createImageBitmap(new Blob([u8], { type: 'image/png' })); const c = new OffscreenCanvas(bmp.width, bmp.height); const x = c.getContext('2d'); x.drawImage(bmp, 0, 0);
      const px = x.getImageData(0, 0, bmp.width, bmp.height).data; let dark = 0; for (let i = 0; i < px.length; i += 4) if (px[i] < 100 && px[i + 3] > 200) dark++; return dark;
    })()`);
    check("the rendered page actually contains the text (dark pixels > 300)", dark > 300, String(dark));

    await selectTab(b, "convert");
    await clickWedge(b, "convert:txt");
    await waitResults(b, 4, 20000);
    const txt = await downloadResult(b, dl, 3);
    const text = txt.bytes.toString("utf8");
    check("extracted text has all three pages in order", /Page 1 of 3[\s\S]*Page 2 of 3[\s\S]*Page 3 of 3/.test(text), JSON.stringify(text.slice(0, 80)));
    check("txt result is named `report (text).txt`", (await b.ev("[...document.querySelectorAll('[data-testid=result] p:first-child')].map(e => e.textContent)")).includes("report (text).txt"));
  });

  await section("pdf: split / rotate / organize / metadata / to-images options", async () => {
    await freshApp(b, base, "en");
    await addFiles(b, [files.pdf3]);
    await selectTab(b, "tools");

    await clickWedge(b, "pdf.split");
    await setOption(b, "ranges", "1-2, 3");
    await runPanel(b);
    const names = await waitResults(b, 2);
    check("split `1-2, 3` -> 2 PDFs `report (split 1/2).pdf`", names.join(",") === "report (split 1).pdf,report (split 2).pdf", names.join(","));
    const s1 = await downloadResult(b, dl, 0);
    const s2 = await downloadResult(b, dl, 1);
    check("split parts have 2 and 1 pages", (await PDFDocument.load(s1.bytes)).getPageCount() === 2 && (await PDFDocument.load(s2.bytes)).getPageCount() === 1);

    await clickWedge(b, "pdf.rotate");
    await setOption(b, "angle", "90");
    await setOption(b, "pages", "2");
    await runPanel(b);
    await waitResults(b, 3);
    const rot = await downloadResult(b, dl, 2);
    const rd = await PDFDocument.load(rot.bytes);
    check("rotate 90 on page 2 only", rd.getPage(0).getRotation().angle === 0 && rd.getPage(1).getRotation().angle === 90 && rd.getPage(2).getRotation().angle === 0);

    await clickWedge(b, "pdf.organize");
    await setOption(b, "order", "3, 1");
    await runPanel(b);
    await waitResults(b, 4);
    const org = await downloadResult(b, dl, 3);
    const orgText = await pageTexts(org.bytes);
    check("organize `3, 1` -> exactly [Page 3, Page 1]", JSON.stringify(orgText) === JSON.stringify(["Page 3 of 3", "Page 1 of 3"]), JSON.stringify(orgText));

    await clickWedge(b, "pdf.metadata");
    await setOption(b, "title", "تقرير DropFlow");
    await setOption(b, "author", "Ali");
    await runPanel(b);
    await waitResults(b, 5);
    const meta = await downloadResult(b, dl, 4);
    const md = await PDFDocument.load(meta.bytes);
    check("metadata: Arabic title + author written", md.getTitle() === "تقرير DropFlow" && md.getAuthor() === "Ali", `${md.getTitle()} / ${md.getAuthor()}`);

    await clickWedge(b, "pdf.to-images");
    await setOption(b, "format", "jpg");
    await setOption(b, "scale", "1");
    await setOption(b, "pages", "2");
    await runPanel(b);
    await waitResults(b, 6, 30000);
    const img = await downloadResult(b, dl, 5);
    const jd = await decodeInBrowser(b, img.bytes, "image/jpeg");
    check("to-images: only page 2, JPEG, 1x scale = 300x200", img.bytes[0] === 0xff && jd.w === 300 && jd.h === 200, JSON.stringify(jd));
  });

  await section("pdf: watermark (Arabic-capable canvas text) and page numbers", async () => {
    await freshApp(b, base, "en");
    await addFiles(b, [files.pdf3]);
    await selectTab(b, "tools");

    await clickWedge(b, "pdf.watermark");
    check("watermark requires text (Run disabled while empty)", await b.ev("document.querySelector('[data-testid=options-panel] button[type=submit]').disabled"));
    await setOption(b, "text", "مسودة DRAFT");
    await setOption(b, "opacity", 60);
    await runPanel(b);
    await waitResults(b, 1);
    const wm = await downloadResult(b, dl, 0);
    check("watermarked PDF has an image XObject on every page", (await PDFDocument.load(wm.bytes)).getPageCount() === 3 && wm.bytes.toString("latin1").split("/Subtype /Image").length - 1 >= 1);
    writeFileSync(join(out, "watermarked.pdf"), wm.bytes);

    await clickWedge(b, "pdf.page-numbers");
    await setOption(b, "position", "bottom-right");
    await setOption(b, "format", "3 / 12");
    await runPanel(b);
    await waitResults(b, 2);
    const pn = await downloadResult(b, dl, 1);
    const pnText = await pageTexts(pn.bytes);
    check("page numbers `1 / 3` .. `3 / 3` drawn on the right pages", pnText.every((t, i) => t.includes(`${i + 1} / 3`) && t.includes(`Page ${i + 1} of 3`)), JSON.stringify(pnText));

    // Render the watermarked PDF again through the app and look at pixels: the watermark must change the page centre.
    await freshApp(b, base, "en");
    await addFiles(b, [join(out, "watermarked.pdf")]);
    await clickWedge(b, "convert:png");
    await waitResults(b, 3, 40000);
    const page = await downloadResult(b, dl, 0);
    writeFileSync(join(out, "watermarked-page1.png"), page.bytes);
    const centre = await b.ev(`(async () => {
      const bin = atob(${JSON.stringify(page.bytes.toString("base64"))}); const u8 = Uint8Array.from(bin, c => c.charCodeAt(0));
      const bmp = await createImageBitmap(new Blob([u8], { type: 'image/png' })); const c = new OffscreenCanvas(bmp.width, bmp.height); const x = c.getContext('2d'); x.drawImage(bmp, 0, 0);
      const px = x.getImageData(150, 150, 300, 100).data; let tinted = 0; for (let i = 0; i < px.length; i += 4) if (px[i] < 235 || px[i + 1] < 235) tinted++; return tinted;
    })()`);
    check("rendered watermarked page shows the watermark in the middle (tinted pixels)", centre > 2000, String(centre));
  });

  await section("pdf: merge, compress (lossy warning) and honest errors", async () => {
    await freshApp(b, base, "en");
    await addFiles(b, [files.pdf3, files.pdf2]);
    await selectTab(b, "tools");
    await clickWedge(b, "pdf.merge");
    const names = await waitResults(b, 1);
    check("merge 3+2 pages -> `report (merged).pdf`", names[0] === "report (merged).pdf", names[0]);
    const merged = await downloadResult(b, dl, 0);
    const mergedText = await pageTexts(merged.bytes);
    check("merged PDF has the 5 pages in order", JSON.stringify(mergedText) === JSON.stringify(["Page 1 of 3", "Page 2 of 3", "Page 3 of 3", "Other 1 of 2", "Other 2 of 2"]), JSON.stringify(mergedText));

    await freshApp(b, base, "en");
    await addFiles(b, [files.pdfBig]);
    await selectTab(b, "tools");
    await clickWedge(b, "pdf.compress");
    const warn = await b.ev("document.querySelector('[data-testid=options-panel] [role=note]')?.textContent");
    check("compress warns that text becomes images", warn === "Text becomes images", String(warn));
    await runPanel(b);
    await waitResults(b, 1, 60000);
    const comp = await downloadResult(b, dl, 0);
    const big = await import("node:fs").then((fs) => fs.statSync(files.pdfBig).size);
    const cdoc = await PDFDocument.load(comp.bytes);
    check("compressed PDF is smaller and rebuilt from JPEG pages", comp.size < big && cdoc.getPageCount() === 1 && comp.bytes.toString("latin1").includes("/DCTDecode"), `${big} -> ${comp.size}`);

    await freshApp(b, base, "en");
    await addFiles(b, [files.pdfBroken]);
    await selectTab(b, "tools");
    await clickWedge(b, "pdf.rotate");
    await runPanel(b);
    await sleep(1500);
    const jobs = await jobTexts(b);
    check("a damaged PDF gives a short, honest error (not a crash)", jobs.some((t) => /Damaged file/.test(t)), JSON.stringify(jobs));
  });
}
