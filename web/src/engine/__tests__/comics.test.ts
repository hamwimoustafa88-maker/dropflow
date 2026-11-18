import { describe, expect, it, vi } from "vitest";
import { PDFDocument, PDFName, PDFDict, PDFRawStream } from "pdf-lib";
import catalog from "@/data/catalog.json";
import { comicsConverter } from "../converters/comics";
import { runComics, type Reencode } from "../converters/comics.impl";
import { naturalCompare, readComic, writeCbz } from "../lib/comic";
import { registry } from "../registry";
import { ConvertError } from "../types";
import { assertWellFormed, text, unzip, zip } from "./book-fixtures";
import { buildJpeg, buildPng, buildWebp } from "./images-fixtures";

const jpeg = buildJpeg({ width: 40, height: 60 }).bytes;
const pngA = buildPng(30, 20);
const pngB = buildPng(50, 10, [0, 0, 0, 255]);
const webp = buildWebp({ width: 12, height: 9 });

function cbz(entries: Record<string, string | Uint8Array>): File {
  return new File([zip(entries) as BlobPart], "قصة.cbz");
}
const sample = () =>
  cbz({
    "Comic/page10.png": pngB,
    "Comic/page2.png": pngA,
    "Comic/Page1.jpg": jpeg,
    "Comic/notes.txt": "not a page",
    "__MACOSX/Comic/._page1.jpg": new Uint8Array([0, 5, 22, 7]),
    "Comic/.thumb.png": pngA,
    "ComicInfo.xml": '<?xml version="1.0"?><ComicInfo><Series>السلسلة</Series><Number>3</Number><Writer>كاتب</Writer><LanguageISO>ar</LanguageISO><Manga>YesAndRightToLeft</Manga></ComicInfo>',
  });

const noReencode: Reencode = async () => {
  throw new Error("re-encoding was not expected");
};
const go = (file: File, operation: string, reencode: Reencode = noReencode) => runComics({ id: "1", operation, files: [file], options: {} }, () => {}, new AbortController().signal, reencode);
async function code(p: Promise<unknown>): Promise<string> {
  try {
    await p;
  } catch (e) {
    return e instanceof ConvertError ? e.code : `other: ${String(e)}`;
  }
  return "none";
}

describe("comics manifest", () => {
  it("CBZ to PDF and EPUB here; CBR and CB7 stay with the Windows app; PDF to CBZ belongs to the PDF converter", () => {
    expect(comicsConverter.supports("convert:pdf", "cbz")).toBe(true);
    expect(comicsConverter.supports("convert:epub", "cbz")).toBe(true);
    for (const [op, ext] of [["convert:pdf", "cbr"], ["convert:epub", "cb7"], ["convert:cbz", "cbz"], ["convert:cbz", "cbr"]]) expect(comicsConverter.supports(op, ext), `${ext} ${op}`).toBe(false);
    expect(registry.find("convert:pdf", "cbz")?.id).toBe("comics");
    expect(registry.find("convert:cbz", "pdf")?.id).toBe("pdf");
    expect(registry.find("convert:pdf", "cbr")).toBeUndefined();
    const category = (catalog as { categories: { id: string; platforms: string[] }[] }).categories.find((c) => c.id === "comic")!;
    expect(category.platforms).toContain("web");
  });

  it("orders pages the way people number them", () => {
    const names = ["p10.png", "P2.png", "p1.png", "p02.png", "a.png", "p1b.png"];
    expect([...names].sort(naturalCompare)).toEqual(["a.png", "p1.png", "p1b.png", "P2.png", "p02.png", "p10.png"]);
  });
});

describe("CBZ in", () => {
  it("keeps only pictures, in natural order, and reads ComicInfo.xml (manga: right to left)", async () => {
    const comic = readComic(new Uint8Array(await sample().arrayBuffer()), "fallback");
    expect(comic.pages.map((p) => p.name)).toEqual(["Comic/Page1.jpg", "Comic/page2.png", "Comic/page10.png"]);
    expect(comic.title).toBe("السلسلة 3");
    expect(comic.author).toBe("كاتب");
    expect(comic.language).toBe("ar");
    expect(comic.rtl).toBe(true);
  });

  it("refuses a RAR or 7z renamed .cbz, an empty comic and a broken file", async () => {
    const rar = new File([new Uint8Array([0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x01, 0x00, 1, 2, 3])], "x.cbz");
    expect(await code(go(rar, "convert:pdf"))).toBe("comicArchive");
    const sevenZip = new File([new Uint8Array([0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c, 0, 4, 1, 2])], "x.cbz");
    expect(await code(go(sevenZip, "convert:epub"))).toBe("comicArchive");
    expect(await code(go(cbz({ "read me.txt": "no pictures" }), "convert:pdf"))).toBe("comicEmpty");
    expect(await code(go(new File([new TextEncoder().encode("hello")], "x.cbz"), "convert:pdf"))).toBe("corrupt");
  });
});

describe("CBZ to PDF", () => {
  it("one page per picture, sized to it, in order; JPEG embedded byte for byte; title, author and right-to-left reading", async () => {
    const [out] = await go(sample(), "convert:pdf");
    expect(out.name).toBe("قصة.pdf");
    expect(out.type).toBe("application/pdf");
    const doc = await PDFDocument.load(new Uint8Array(await out.arrayBuffer()));
    expect(doc.getPages().map((p) => [p.getWidth(), p.getHeight()])).toEqual([[40, 60], [30, 20], [50, 10]]);
    expect(doc.getTitle()).toBe("السلسلة 3");
    expect(doc.getAuthor()).toBe("كاتب");
    const prefs = doc.catalog.lookup(PDFName.of("ViewerPreferences"), PDFDict);
    expect(prefs.get(PDFName.of("Direction"))?.toString()).toBe("/R2L");
    const streams = doc.context.enumerateIndirectObjects().map(([, o]) => o).filter((o): o is PDFRawStream => o instanceof PDFRawStream);
    const dct = streams.find((s) => s.dict.get(PDFName.of("Filter"))?.toString() === "/DCTDecode");
    expect(dct && [...dct.contents]).toEqual([...jpeg]);
  });

  it("re-encodes pages a PDF cannot hold as they are (WebP), through the canvas", async () => {
    const reencode = vi.fn<Reencode>(async () => ({ bytes: buildPng(12, 9), mime: "image/png", width: 12, height: 9 }));
    const [out] = await go(cbz({ "1.webp": webp, "2.png": pngA }), "convert:pdf", reencode);
    const doc = await PDFDocument.load(new Uint8Array(await out.arrayBuffer()));
    expect(doc.getPages().map((p) => p.getWidth())).toEqual([12, 30]);
    expect(reencode).toHaveBeenCalledTimes(1);
    expect(reencode.mock.calls[0][0].name).toBe("1.webp");
    expect(doc.catalog.lookup(PDFName.of("ViewerPreferences"))).toBeUndefined(); // no ComicInfo: left to right
  });
});

describe("CBZ to EPUB", () => {
  it("a fixed-layout EPUB: one XHTML page per picture with its size, manga reads right to left", async () => {
    const [out] = await go(sample(), "convert:epub");
    expect(out.name).toBe("قصة.epub");
    const bytes = new Uint8Array(await out.arrayBuffer());
    expect(text(bytes.subarray(30, 58))).toBe("mimetypeapplication/epub+zip");
    const files = unzip(bytes);
    const opf = text(files["OEBPS/content.opf"]);
    assertWellFormed(opf);
    expect(opf).toContain('<meta property="rendition:layout">pre-paginated</meta>');
    expect(opf).toContain('page-progression-direction="rtl"');
    expect(opf).toContain("<dc:title>السلسلة 3</dc:title>");
    expect([...opf.matchAll(/<itemref idref="(p\d+)"/g)].map((m) => m[1])).toEqual(["p001", "p002", "p003"]);
    const page1 = text(files["OEBPS/page001.xhtml"]);
    assertWellFormed(page1);
    expect(page1).toContain('<meta name="viewport" content="width=40, height=60"/>');
    expect(page1).toContain('<img src="images/page001.jpg" alt="1" width="40" height="60"/>');
    expect(page1).toMatch(/lang="ar" xml:lang="ar" dir="rtl"/);
    expect([...files["OEBPS/images/page001.jpg"]]).toEqual([...jpeg]);
    expect([...files["OEBPS/images/page003.png"]]).toEqual([...pngB]);
    expect(text(files["OEBPS/page002.xhtml"])).toContain("width=30, height=20");
    assertWellFormed(text(files["OEBPS/nav.xhtml"]));
  });

  it("WebP and GIF stay as they are; BMP is re-encoded", async () => {
    const gif = new Uint8Array([0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 7, 0, 5, 0, 0, 0, 0, 0x3b]);
    const bmp = new Uint8Array([0x42, 0x4d, ...new Array(52).fill(0)]);
    const reencode = vi.fn<Reencode>(async () => ({ bytes: pngA, mime: "image/png", width: 30, height: 20 }));
    const [out] = await go(cbz({ "a1.webp": webp, "a2.gif": gif, "a3.bmp": bmp }), "convert:epub", reencode);
    const files = unzip(new Uint8Array(await out.arrayBuffer()));
    expect(Object.keys(files).filter((n) => n.startsWith("OEBPS/images/")).sort()).toEqual(["OEBPS/images/page001.webp", "OEBPS/images/page002.gif", "OEBPS/images/page003.png"]);
    expect(text(files["OEBPS/page001.xhtml"])).toContain("width=12, height=9");
    expect(text(files["OEBPS/page002.xhtml"])).toContain("width=7, height=5");
    expect(reencode).toHaveBeenCalledTimes(1);
    expect(text(files["OEBPS/content.opf"])).toContain('page-progression-direction="ltr"');
  });
});

describe("writing a CBZ", () => {
  it("numbers the pages and adds ComicInfo.xml", () => {
    const files = unzip(writeCbz([{ bytes: jpeg, ext: "jpg" }, { bytes: jpeg, ext: "jpg" }], { title: "كتاب & قصة" }));
    expect(Object.keys(files)).toEqual(["001.jpg", "002.jpg", "ComicInfo.xml"]);
    const info = text(files["ComicInfo.xml"]);
    assertWellFormed(info);
    expect(info).toContain("<Title>كتاب &amp; قصة</Title>");
    expect(info).toContain("<PageCount>2</PageCount>");
  });
});
