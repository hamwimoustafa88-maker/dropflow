import { describe, expect, it } from "vitest";
import catalog from "@/data/catalog.json";
import { booksConverter } from "../converters/books";
import { readBook, writeBook } from "../converters/books.impl";
import { parseMarkup, serialize, textContent } from "../lib/markup";
import { blocksToMarkdown, blocksToText, htmlToBlocks, markdownToNodes } from "../lib/book-text";
import { registry } from "../registry";
import { ConvertError } from "../types";
import { AR_AUTHOR, AR_P1, AR_P2, AR_TITLE, PICTURE, arabicEpub, arabicFb2, assertWellFormed, cp1251, text, unzip, zip } from "./book-fixtures";

const run = async (name: string, bytes: Uint8Array | string, operation: string) => {
  const [out] = await booksConverter.run({ id: "1", operation, files: [new File([bytes as BlobPart], name)], options: {} }, () => {}, new AbortController().signal);
  return out;
};
const bytesOf = async (f: File) => new Uint8Array(await f.arrayBuffer());
async function code(p: Promise<unknown> | (() => unknown)): Promise<string> {
  try {
    await (typeof p === "function" ? p() : p);
  } catch (e) {
    return e instanceof ConvertError ? e.code : `other: ${String(e)}`;
  }
  return "none";
}

/** Checks a written EPUB like an EPUB reader would, independently of lib/epub: OCF, package, spine, every XHTML well-formed. */
function inspectEpub(bytes: Uint8Array) {
  // OCF: the first local header is "mimetype", stored (method 0), no extra field, content right after the name.
  const v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  expect(v.getUint32(0, true)).toBe(0x04034b50);
  expect(v.getUint16(8, true)).toBe(0); // stored
  expect(v.getUint16(26, true)).toBe(8);
  expect(v.getUint16(28, true)).toBe(0); // no extra field
  expect(text(bytes.subarray(30, 38))).toBe("mimetype");
  expect(text(bytes.subarray(38, 58))).toBe("application/epub+zip");
  const files = unzip(bytes);
  const container = text(files["META-INF/container.xml"]);
  assertWellFormed(container);
  const opfPath = /full-path="([^"]+)"/.exec(container)![1];
  const opf = text(files[opfPath]);
  assertWellFormed(opf);
  const base = opfPath.slice(0, opfPath.lastIndexOf("/") + 1);
  const manifest = new Map([...opf.matchAll(/<item id="([^"]+)" href="([^"]+)" media-type="([^"]+)"/g)].map((m) => [m[1], { href: m[2], type: m[3] }]));
  for (const item of manifest.values()) expect(files[base + item.href], item.href).toBeDefined();
  const spine = [...opf.matchAll(/<itemref idref="([^"]+)"/g)].map((m) => manifest.get(m[1])!.href);
  const chapters = spine.map((href) => text(files[base + href]));
  for (const c of chapters) assertWellFormed(c);
  for (const [, item] of manifest) if (item.type === "application/xhtml+xml") assertWellFormed(text(files[base + item.href]));
  return { files, opf, spine, chapters, base };
}

describe("books manifest", () => {
  it("converts EPUB, FB2, HTMLZ and TXTZ to each other and to HTML, Markdown and text; MOBI stays on Windows", () => {
    for (const input of ["epub", "fb2", "htmlz", "txtz"])
      for (const target of ["epub", "fb2", "htmlz", "txtz", "html", "md", "txt"]) expect(booksConverter.supports(`convert:${target}`, input), `${input}->${target}`).toBe(input !== target);
    for (const [op, ext] of [["convert:epub", "mobi"], ["convert:pdf", "epub"], ["convert:docx", "fb2"], ["convert:epub", "html"], ["convert:epub", "cbz"]]) expect(booksConverter.supports(op, ext), `${ext}->${op}`).toBe(false);
    expect(registry.find("convert:txt", "epub")?.id).toBe("books");
    expect(booksConverter.runtime("convert:epub", "fb2")).toBe("worker");
    expect(booksConverter.options("convert:epub")).toEqual([]);
  });

  it("the catalog lists the e-book category for the web", () => {
    const category = (catalog as { categories: { id: string; platforms: string[]; outputs: string[] }[] }).categories.find((c) => c.id === "epub")!;
    expect(category.platforms).toContain("web");
    for (const t of ["epub", "fb2", "htmlz", "txtz", "html", "md", "txt"]) expect(category.outputs).toContain(t);
  });
});

describe("markup reader", () => {
  it("is tolerant of tag soup and never expands a DOCTYPE's entities", () => {
    const doc = parseMarkup(`<!DOCTYPE x [<!ENTITY a "AAAAAAAAAA"><!ENTITY b "&a;&a;&a;">]><P>one<p>two<br>three<ul><li>x<li>y</ul><img src=a.png alt='q'>&b; &amp; &#x627; &copy;</div></p>`);
    expect(serialize(doc.children)).toBe('<p>one</p><p>two<br>three</p><ul><li>x</li><li>y</li></ul><img src="a.png" alt="q">&amp;b; &amp; ا ©');
    expect(serialize(parseMarkup("<a><b/><c>t</c></a>", { xml: true }).children, { xml: true })).toBe("<a><b></b><c>t</c></a>");
  });

  it("reads a big book quickly (no quadratic copying)", () => {
    const paragraph = `<p dir="rtl">${AR_P1} <b>غامق</b> <i>مائل</i> <a href="#x">رابط</a><style>p{}</style></p>\n`;
    const html = `<html><body>${paragraph.repeat(20_000)}</body></html>`; // about 2.3 million characters (4 MB of UTF-8)
    const t0 = Date.now();
    const doc = parseMarkup(html);
    const out = serialize(doc.children);
    expect(out.length).toBeGreaterThan(2_000_000);
    expect(Date.now() - t0).toBeLessThan(8000);
  });
});

describe("EPUB in", () => {
  it("reads the spine in order, skips the navigation and non-linear items, cleans the chapters and keeps Arabic in logical order", () => {
    const book = readBook(arabicEpub(), "epub", "fallback");
    expect(book.title).toBe(AR_TITLE);
    expect(book.author).toBe(AR_AUTHOR);
    expect(book.language).toBe("ar");
    const html = serialize(book.body);
    expect(html.indexOf("الفصل الأول")).toBeLessThan(html.indexOf("الفصل الثاني"));
    expect(html).toContain(AR_P1);
    expect(html).toContain(AR_P2);
    expect(html).not.toMatch(/NOT LINEAR|<script|onclick|style=|class=|<form|<input|javascript:|<svg|<nav/);
    expect(html).toContain('<a href="#later">'); // a link to another chapter becomes an in-page anchor
    expect(html).toContain('<a href="#ch1">العودة</a>'); // a link to a whole chapter points at its section
    expect(html).toContain('<a href="https://example.org/">');
    expect(book.pictures).toHaveLength(1); // the <img> and the <svg><image> are the same picture
    expect([...book.pictures[0].bytes]).toEqual([...PICTURE]);
  });

  it("refuses DRM, a zip without a package and a file that is not a zip", async () => {
    const drm = zip({
      mimetype: "application/epub+zip",
      "META-INF/container.xml": '<container><rootfiles><rootfile full-path="a.opf"/></rootfiles></container>',
      "META-INF/encryption.xml": '<encryption><EncryptedData><EncryptionMethod Algorithm="http://www.w3.org/2001/04/xmlenc#aes128-cbc"/></EncryptedData></encryption>',
      "a.opf": "<package/>",
    });
    expect(await code(() => readBook(drm, "epub", "x"))).toBe("bookDrm");
    expect(await code(() => readBook(zip({ mimetype: "application/epub+zip", "a.txt": "x" }), "epub", "x"))).toBe("notABook");
    expect(await code(() => readBook(new TextEncoder().encode("plain text, no zip at all"), "epub", "x"))).toBe("corrupt");
    // Font obfuscation is not DRM.
    const fonts = zip({
      mimetype: "application/epub+zip",
      "META-INF/container.xml": '<container><rootfiles><rootfile full-path="a.opf"/></rootfiles></container>',
      "META-INF/encryption.xml": '<encryption><EncryptedData><EncryptionMethod Algorithm="http://www.idpf.org/2008/embedding"/></EncryptedData></encryption>',
      "a.opf": '<package><manifest><item id="c" href="c.html" media-type="text/html"/></manifest><spine><itemref idref="c"/></spine></package>',
      "c.html": "<p>English words only</p>",
    });
    const book = readBook(fonts, "epub", "Fallback");
    expect(book.title).toBe("Fallback");
    expect(book.language).toBe("en");
  });
});

describe("EPUB out", () => {
  it("FB2 to EPUB: valid OCF, right-to-left package and chapters, pictures as files, note links across files", async () => {
    const out = await run("كتاب.fb2", arabicFb2(), "convert:epub");
    expect(out.name).toBe("كتاب.epub");
    expect(out.type).toBe("application/epub+zip");
    const { files, opf, chapters, base } = inspectEpub(await bytesOf(out));
    expect(opf).toContain(`<dc:title>${AR_TITLE}</dc:title>`);
    expect(opf).toContain("<dc:creator>سارة المصري</dc:creator>");
    expect(opf).toContain("<dc:language>ar</dc:language>");
    expect(opf).toContain('page-progression-direction="rtl"');
    expect(opf).toMatch(/<meta property="dcterms:modified">\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ<\/meta>/);
    for (const c of chapters) expect(c).toMatch(/<html [^>]*lang="ar" xml:lang="ar" dir="rtl"/);
    const all = chapters.join("\n");
    expect(all).toContain(AR_P1);
    expect(all).toContain(AR_P2);
    expect(all).toContain("سطر أول<br/>سطر ثان");
    expect(all).toMatch(/<img src="images\/image1\.png" alt=""\/>/);
    expect([...files[`${base}images/image1.png`]]).toEqual([...PICTURE]);
    // The note link lives in chapter 2 (the first <section>), its target in the notes chapter: it must name that file.
    const noteChapter = chapters.findIndex((c) => c.includes("حاشية"));
    expect(noteChapter).toBeGreaterThan(0);
    expect(all).toContain(`href="chapter${noteChapter + 1}.xhtml#n1"`);
    expect(text(files[`${base}nav.xhtml`])).toContain('epub:type="toc"');
  });

  it("EPUB in, EPUB out (through FB2) keeps chapters, lists, tables and the picture", async () => {
    const fb2 = await run("a.epub", arabicEpub(), "convert:fb2");
    const back = await run("a.fb2", await bytesOf(fb2), "convert:epub");
    const { chapters, files, base } = inspectEpub(await bytesOf(back));
    const all = chapters.join("\n");
    for (const s of ["الفصل الأول", "الفصل الثاني", AR_P1, AR_P2, "الإثنين"]) expect(all).toContain(s);
    expect(files[`${base}images/image1.png`]).toBeDefined();
  });

  it("English books read left to right", async () => {
    const htmlz = zip({ "index.html": "<html><head><title>Plain</title></head><body><h1>One</h1><p>Hello there.</p><h1>Two</h1><p>Bye.</p></body></html>" });
    const { opf, chapters } = inspectEpub(await bytesOf(await run("p.htmlz", htmlz, "convert:epub")));
    expect(opf).toContain('page-progression-direction="ltr"');
    expect(opf).toContain("<dc:title>Plain</dc:title>");
    expect(chapters).toHaveLength(2); // cut before each <h1>
    expect(chapters[0]).toContain('dir="ltr"');
  });
});

describe("FB2", () => {
  it("reads sections, notes, poems, pictures; writes FB2 that reads back the same", async () => {
    const book = readBook(new TextEncoder().encode(arabicFb2()), "fb2", "x");
    expect(book.title).toBe(AR_TITLE);
    expect(book.author).toBe("سارة المصري");
    expect(book.pictures).toHaveLength(1);
    expect(booksConverter.supports("convert:fb2", "fb2")).toBe(false); // same format: not offered
    const fb2 = await writeBook(readBook(arabicEpub(), "epub", "x"), "fb2");
    const xml = typeof fb2 === "string" ? fb2 : text(fb2);
    assertWellFormed(xml);
    expect(xml).toContain('<FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0" xmlns:l="http://www.w3.org/1999/xlink">');
    expect(xml).toContain("<lang>ar</lang>");
    expect(xml).toContain("<first-name>سارة</first-name><last-name>المصري</last-name>");
    expect(xml).toMatch(/<binary id="image1\.png" content-type="image\/png">[A-Za-z0-9+/=]+<\/binary>/);
    expect(xml).toContain('<image l:href="#image1.png" alt="البحر"/>');
    const again = readBook(new TextEncoder().encode(xml), "fb2", "x");
    expect(textContent(again.body)).toContain(AR_P1);
    expect([...again.pictures[0].bytes]).toEqual([...PICTURE]);
  });

  it("honours a declared legacy encoding (windows-1251) and opens a zipped FB2", () => {
    const head = new TextEncoder().encode('<?xml version="1.0" encoding="windows-1251"?><FictionBook><description><title-info><book-title>');
    const tail = new TextEncoder().encode("</book-title></title-info></description><body><section><p>");
    const end = new TextEncoder().encode("</p></section></body></FictionBook>");
    const bytes = new Uint8Array([...head, ...cp1251("Привет"), ...tail, ...cp1251("Мир"), ...end]);
    const book = readBook(bytes, "fb2", "x");
    expect(book.title).toBe("Привет");
    expect(textContent(book.body)).toContain("Мир");
    const zipped = zip({ "book.fb2": bytes });
    expect(readBook(zipped, "fb2", "x").title).toBe("Привет");
  });

  it("refuses what is not FictionBook", async () => {
    expect(await code(() => readBook(new TextEncoder().encode("<html><body>x</body></html>"), "fb2", "x"))).toBe("notABook");
    expect(await code(() => readBook(new TextEncoder().encode("<FictionBook><description/></FictionBook>"), "fb2", "x"))).toBe("bookEmpty");
  });
});

describe("HTML, Markdown and text out", () => {
  it("HTML: one self-contained page, right to left for Arabic, picture inlined, no script", async () => {
    const out = await run("a.epub", arabicEpub(), "convert:html");
    expect(out.type).toBe("text/html;charset=utf-8");
    const html = await out.text();
    expect(html).toMatch(/^<!DOCTYPE html>\n<html lang="ar" dir="rtl">/);
    expect(html).toContain(`<title>${AR_TITLE}</title>`);
    expect(html).toContain(`<meta name="author" content="${AR_AUTHOR}">`);
    expect(html).toContain(`src="data:image/png;base64,${Buffer.from(PICTURE).toString("base64")}"`);
    expect(html).not.toMatch(/<script|onclick/);
  });

  it("Markdown: headings, emphasis, lists and a table; pictures become their alt text", async () => {
    const md = await (await run("a.epub", arabicEpub(), "convert:md")).text();
    expect(md).toContain("# الفصل الأول");
    expect(md).toContain(`${AR_P1} **بقوة** & *هدوء*`);
    expect(md).toContain("- أولاً\n- ثانياً\n    - فرعي");
    expect(md).toContain("| اليوم | الموج |\n| --- | --- |\n| الإثنين | عالٍ |");
    expect(md).toContain("البحر");
    expect(md).not.toMatch(/data:|df-img|!\[/);
  });

  it("text: a byte order mark, plain paragraphs, list markers and tab-separated tables", async () => {
    const out = await run("a.fb2", arabicFb2(), "convert:txt");
    const bytes = await bytesOf(out);
    expect([...bytes.subarray(0, 3)]).toEqual([0xef, 0xbb, 0xbf]);
    const t = new TextDecoder().decode(bytes.subarray(3));
    expect(t).toContain(`${AR_P1}1`); // the note reference follows the sentence
    expect(t).toContain("أ\tب\n1\t2");
    expect(t).toContain("سطر أول\nسطر ثان");
    expect(t).not.toMatch(/[<>*#]/);
  });

  it("the block model writes what Windows writes", () => {
    const blocks = htmlToBlocks(parseMarkup("<h2> A  title </h2><p>x<br>y</p><ol start='3'><li>c</li><li>d</li></ol><pre>a\n b</pre><hr><blockquote><p>q</p></blockquote>").children);
    expect(blocksToText(blocks)).toBe("A title\n\nx\ny\n\n3. c\n4. d\n\na\n b\n\n----------\n\nq\n");
    expect(blocksToMarkdown(blocks)).toBe("## A title\n\nx  \ny\n\n3. c\n4. d\n\n```\na\n b\n```\n\n---\n\n> q\n");
  });
});

describe("HTMLZ and TXTZ", () => {
  it("EPUB to HTMLZ: index.html with images/, style.css and metadata.opf", async () => {
    const files = unzip(await bytesOf(await run("a.epub", arabicEpub(), "convert:htmlz")));
    expect(Object.keys(files).sort()).toEqual(["images/image1.png", "index.html", "metadata.opf", "style.css"]);
    const index = text(files["index.html"]);
    expect(index).toMatch(/<html lang="ar" dir="rtl">/);
    expect(index).toContain('src="images/image1.png"');
    const opf = text(files["metadata.opf"]);
    assertWellFormed(opf);
    expect(opf).toContain(`<dc:title>${AR_TITLE}</dc:title>`);
    expect(opf).toContain("<dc:language>ar</dc:language>");
  });

  it("TXTZ round trip: Markdown with picture references reads back into the same book", async () => {
    const txtz = await bytesOf(await run("a.epub", arabicEpub(), "convert:txtz"));
    const files = unzip(txtz);
    const md = text(files["index.txt"]);
    expect(md).toContain("![البحر](images/image1.png)");
    expect(md).toContain("# الفصل الثاني");
    const book = readBook(txtz, "txtz", "x");
    expect(book.title).toBe(AR_TITLE);
    expect(book.language).toBe("ar");
    expect(book.pictures).toHaveLength(1);
    const html = serialize(book.body);
    expect(html).toContain("<h1>الفصل الأول</h1>");
    expect(html).toContain("<strong>بقوة</strong>");
    expect(html).toContain("<li>ثانياً<ul><li>فرعي</li></ul></li>");
    expect(html).toContain("<th>اليوم</th>");
  });

  it("reads a calibre-style HTMLZ in a legacy charset, and a TXTZ without metadata", () => {
    const html = new Uint8Array([
      ...new TextEncoder().encode('<html><head><meta charset="windows-1256"><title>T</title></head><body><p>'),
      0xc7, 0xe1, 0xd3, 0xe1, 0xc7, 0xe3, // "السلام" in windows-1256
      ...new TextEncoder().encode("</p></body></html>"),
    ]);
    const book = readBook(zip({ "index.html": html }), "htmlz", "x");
    expect(textContent(book.body)).toContain("السلام");
    expect(book.title).toBe("T");
    expect(book.language).toBe("ar");
    const plain = readBook(zip({ "book/notes.md": "Title\n=====\n\nSome *words* and `code`.\n" }), "txtz", "fallback");
    expect(plain.title).toBe("fallback");
    expect(serialize(plain.body)).toBe("<h1>Title</h1><p>Some <em>words</em> and <code>code</code>.</p>");
  });

  it("Markdown in: hostile nesting stays text instead of overflowing the stack", () => {
    const quotes = serialize(markdownToNodes(">".repeat(20_000) + " deep"));
    expect(quotes).toContain("deep");
    const brackets = serialize(markdownToNodes("[".repeat(5000) + "x" + "](y)".repeat(5000)));
    expect(brackets).toContain("x");
    const deepHtml = readBook(zip({ "index.html": "<div>".repeat(50_000) + "deep text" }), "htmlz", "x");
    expect(textContent(deepHtml.body)).toContain("deep text");
    expect(serialize(deepHtml.body).length).toBeGreaterThan(0);
  });

  it("Markdown in: raw HTML is shown as text, never run", () => {
    expect(serialize(markdownToNodes("<script>alert(1)</script> [x](javascript:alert(1))"))).toBe('<p>&lt;script&gt;alert(1)&lt;/script&gt; <a href="javascript:alert(1)">x</a></p>');
    // ...and the cleaner then drops the unsafe link (what readBook does):
    const book = readBook(zip({ "index.txt": "[x](javascript:alert(1)) <img src=x onerror=alert(1)>" }), "txtz", "x");
    expect(serialize(book.body)).toBe("<p><a>x</a> &lt;img src=x onerror=alert(1)&gt;</p>");
  });
});
