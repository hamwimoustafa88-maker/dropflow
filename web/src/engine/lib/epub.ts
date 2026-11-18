// SPDX-License-Identifier: GPL-3.0-or-later
// EPUB in and out, ported from the Windows app (src/DropFlow.Engines.Pdf/Office/EpubReader.cs and Books/BookEpubWriter.cs).
// Reading: container.xml -> package document -> spine order -> cleaned chapters, links between chapters as in-page anchors, pictures
// kept as bytes. Writing: an EPUB 3 with one XHTML file per chapter, pictures in images/, a navigation document, and the reading
// direction of the language (dir="rtl" and page-progression-direction="rtl" for Arabic, Hebrew, Persian, Urdu...); also fixed-layout
// picture books (comics), one page per picture.

import { ConvertError } from "../types";
import { BookZip, writeZip, type ZipItem } from "./book-zip";
import {
  MAX_IMAGE_BYTES,
  PictureStore,
  chaptersOf,
  cleanNodes,
  cloneNodes,
  detectLanguage,
  escapeHtml,
  extractPictures,
  isRtl,
  mimeForPictureName,
  pictureExtension,
  plainText,
  primaryLanguage,
  type Book,
} from "./book-model";
import { attr, bodyOf, descendants, el, firstDescendant, localName, parseMarkup, rewrite, serialize, setAttr, removeAttr, text, textContent, type MNode } from "./markup";

const XHTML_NS = "http://www.w3.org/1999/xhtml";
const EPUB_NS = "http://www.idpf.org/2007/ops";
const FONT_OBFUSCATION = ["http://www.idpf.org/2008/embedding", "http://ns.adobe.com/pdf/enc#RC"];

/** Joins a relative reference to a folder inside the zip, resolving "..", "." and percent-encoding. */
export function resolvePath(baseDir: string, href: string): string {
  const combined = href.startsWith("/") ? href.replace(/^\/+/, "") : baseDir + href;
  let decoded = combined;
  try {
    decoded = decodeURIComponent(combined);
  } catch {
    /* a stray "%": keep it as written */
  }
  const parts: string[] = [];
  for (const part of decoded.split("/")) {
    if (!part || part === ".") continue;
    if (part === "..") parts.pop();
    else parts.push(part);
  }
  return parts.join("/");
}

const dirOf = (path: string) => (path.includes("/") ? path.slice(0, path.lastIndexOf("/") + 1) : "");

function xmlOf(zip: BookZip, name: string) {
  const textValue = zip.readText(name);
  if (textValue === undefined) throw new ConvertError("notABook");
  return parseMarkup(textValue, { xml: true });
}

// ---- reading -----------------------------------------------------------------------------------------------------------------------

export function readEpub(bytes: Uint8Array, fallbackTitle: string, onChapter?: (done: number, total: number) => void): Book {
  const zip = new BookZip(bytes);
  rejectDrm(zip);
  const containerName = zip.find("META-INF/container.xml");
  if (!containerName) throw new ConvertError("notABook");
  const container = xmlOf(zip, containerName);
  const rootfileAttr = firstDescendant(container, "rootfile");
  let rootfile = rootfileAttr ? attr(rootfileAttr, "full-path") : undefined;
  if (!rootfile) throw new ConvertError("notABook");
  rootfile = resolvePath("", rootfile);
  const opfName = zip.find(rootfile);
  if (!opfName) throw new ConvertError("notABook");
  const opf = xmlOf(zip, opfName);
  const baseDir = dirOf(opfName);

  const manifest = new Map<string, { href: string; type: string; props: string }>();
  for (const item of descendants(opf).filter((e) => localName(e.name) === "item")) {
    const id = attr(item, "id");
    const href = attr(item, "href");
    if (id === undefined || href === undefined || manifest.has(id)) continue;
    manifest.set(id, { href: resolvePath(baseDir, href.split("#")[0]), type: attr(item, "media-type") ?? "", props: attr(item, "properties") ?? "" });
  }
  const chapters = descendants(opf)
    .filter((e) => localName(e.name) === "itemref" && (attr(e, "linear") ?? "").toLowerCase() !== "no")
    .map((e) => manifest.get(attr(e, "idref") ?? ""))
    .filter((m): m is { href: string; type: string; props: string } => !!m)
    .filter((m) => (m.type === "application/xhtml+xml" || m.type === "text/html") && !m.props.split(/\s+/).includes("nav"));
  if (chapters.length === 0) throw new ConvertError("bookEmpty");

  const chapterIds = new Map<string, string>();
  chapters.forEach((c, i) => {
    if (!chapterIds.has(c.href)) chapterIds.set(c.href, `ch${i + 1}`);
  });

  const store = new PictureStore();
  const body: MNode[] = [];
  chapters.forEach((chapter, i) => {
    const name = zip.find(chapter.href);
    const source = name ? zip.readText(name) : undefined;
    if (source === undefined) return;
    const doc = parseMarkup(source);
    const chapterDir = dirOf(chapter.href);
    const nodes = cleanNodes(bodyOf(doc).children, {
      store,
      rewriteLink: (href) => rewriteLink(href, chapterDir, chapterIds),
      resolveImage: (src) => {
        const entryName = zip.find(resolvePath(chapterDir, src.split("#")[0]));
        if (!entryName) return null;
        const mime = mimeForPictureName(entryName);
        if (!mime || zip.size(entryName) > MAX_IMAGE_BYTES) return null;
        const data = zip.read(entryName);
        return data ? store.add(entryName, mime, data) : null;
      },
    });
    body.push(el("section", [["id", chapterIds.get(chapter.href) ?? `ch${i + 1}`]], nodes), text("\n"));
    onChapter?.(i + 1, chapters.length);
  });

  const title = plainText(firstDescendant(opf, "title")?.children ?? []);
  const author = plainText(firstDescendant(opf, "creator")?.children ?? []);
  const language = plainText(firstDescendant(opf, "language")?.children ?? []);
  return {
    title: title || fallbackTitle,
    author: author || undefined,
    language: language || detectLanguage(textContent(body)),
    body,
    pictures: store.pictures,
  };
}

function rewriteLink(href: string, chapterDir: string, chapterIds: ReadonlyMap<string, string>): string | null {
  const h = href.trim();
  if (h.startsWith("#")) return h.length > 1 ? h : null;
  const colon = h.indexOf(":");
  if (colon > 0 && !h.slice(0, colon).includes("/")) {
    const scheme = h.slice(0, colon).toLowerCase();
    return scheme === "http" || scheme === "https" || scheme === "mailto" ? h : null;
  }
  const hash = h.indexOf("#");
  const path = hash >= 0 ? h.slice(0, hash) : h;
  const fragment = hash >= 0 ? h.slice(hash + 1) : "";
  const id = chapterIds.get(resolvePath(chapterDir, path));
  if (!id) return null;
  return fragment ? `#${fragment}` : `#${id}`;
}

function rejectDrm(zip: BookZip): void {
  const name = zip.find("META-INF/encryption.xml");
  if (!name) return;
  const doc = xmlOf(zip, name);
  // Font obfuscation (IDPF and Adobe) is not DRM; anything else means the chapters themselves are encrypted.
  const algorithms = descendants(doc).filter((e) => localName(e.name) === "EncryptionMethod").map((e) => attr(e, "Algorithm") ?? "");
  if (algorithms.some((a) => !FONT_OBFUSCATION.includes(a))) throw new ConvertError("bookDrm");
}

// ---- writing -----------------------------------------------------------------------------------------------------------------------

const CONTAINER =
  '<?xml version="1.0" encoding="utf-8"?>\n' +
  '<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">\n' +
  '  <rootfiles>\n    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>\n  </rootfiles>\n</container>\n';

const CSS =
  "body { font-family: serif; line-height: 1.5; margin: 5%; }\n" +
  "h1, h2, h3, h4, h5, h6 { line-height: 1.2; }\n" +
  "p, li, h1, h2, h3, h4, h5, h6, blockquote { unicode-bidi: plaintext; text-align: start; }\n" +
  "img { max-width: 100%; height: auto; }\n" +
  "table { border-collapse: collapse; margin: 1em 0; }\n" +
  "td, th { border: 1px solid #888; padding: 0.2em 0.5em; }\n";

function head(title: string, language: string, rtl: boolean, fixedSize?: { width: number; height: number }): string {
  return (
    '<?xml version="1.0" encoding="utf-8"?>\n<!DOCTYPE html>\n' +
    `<html xmlns="${XHTML_NS}" xmlns:epub="${EPUB_NS}" lang="${language}" xml:lang="${language}" dir="${rtl ? "rtl" : "ltr"}">\n` +
    `<head>\n<meta charset="utf-8"/>\n<title>${escapeHtml(title)}</title>\n` +
    (fixedSize ? `<meta name="viewport" content="width=${fixedSize.width}, height=${fixedSize.height}"/>\n` : "") +
    '<link rel="stylesheet" type="text/css" href="style.css"/>\n</head>\n<body>\n'
  );
}

function nav(titles: readonly string[], bookTitle: string, language: string, rtl: boolean, firstFile?: string): string {
  let s = head(bookTitle, language, rtl);
  s += `<nav epub:type="toc" id="toc">\n<h1>${escapeHtml(bookTitle)}</h1>\n<ol>\n`;
  titles.forEach((t, i) => (s += `<li><a href="${firstFile ?? `chapter${i + 1}.xhtml`}">${escapeHtml(t)}</a></li>\n`));
  return s + "</ol>\n</nav>\n</body>\n</html>\n";
}

function uuid(): string {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") return crypto.randomUUID();
  const b = new Uint8Array(16);
  for (let i = 0; i < 16; i++) b[i] = Math.floor(Math.random() * 256);
  b[6] = (b[6] & 0x0f) | 0x40;
  b[8] = (b[8] & 0x3f) | 0x80;
  const h = [...b].map((x) => x.toString(16).padStart(2, "0")).join("");
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

const modifiedNow = () => new Date().toISOString().replace(/\.\d{3}Z$/, "Z");

function packageDocument(o: { title: string; author?: string; language: string; rtl: boolean; manifest: string; spine: string; fixedLayout: boolean }): string {
  let s = '<?xml version="1.0" encoding="utf-8"?>\n';
  s += `<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid" xml:lang="${o.language}"`;
  if (o.fixedLayout) s += ' prefix="rendition: http://www.idpf.org/vocab/rendition/#"';
  s += '>\n  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">\n';
  s += `    <dc:identifier id="bookid">urn:uuid:${uuid()}</dc:identifier>\n`;
  s += `    <dc:title>${escapeHtml(o.title)}</dc:title>\n`;
  s += `    <dc:language>${o.language}</dc:language>\n`;
  if (o.author) s += `    <dc:creator>${escapeHtml(o.author)}</dc:creator>\n`;
  s += `    <meta property="dcterms:modified">${modifiedNow()}</meta>\n`;
  if (o.fixedLayout) {
    s += '    <meta property="rendition:layout">pre-paginated</meta>\n';
    s += '    <meta property="rendition:spread">auto</meta>\n';
  }
  s += `  </metadata>\n  <manifest>\n${o.manifest}  </manifest>\n`;
  s += `  <spine page-progression-direction="${o.rtl ? "rtl" : "ltr"}">\n${o.spine}  </spine>\n</package>\n`;
  return s;
}

/** Writes a reflowable EPUB 3. */
export function writeEpub(book: Book): Uint8Array {
  const language = primaryLanguage(book.language);
  const rtl = isRtl(language);
  const pictures: { name: string; mime: string; bytes: Uint8Array }[] = [];
  const body = extractPictures(cloneNodes(book.body), book.pictures, (index, picture) => {
    const name = `images/image${index}.${pictureExtension(picture.mime)}`;
    pictures.push({ name, mime: picture.mime, bytes: picture.bytes });
    return name;
  });
  const chapters = chaptersOf(body, book.title);

  // Anchors move into separate files: "#id" must become "chapterN.xhtml#id".
  const owner = new Map<string, number>();
  chapters.forEach((c, i) => {
    if (!owner.has(c.id)) owner.set(c.id, i);
    for (const e of descendants(c.nodes)) {
      const id = attr(e, "id");
      if (id && !owner.has(id)) owner.set(id, i);
    }
  });
  chapters.forEach((c, i) => {
    c.nodes = rewrite(c.nodes, (e) => {
      if (e.name !== "a") return undefined;
      const href = attr(e, "href");
      if (href?.startsWith("#")) {
        const target = owner.get(href.slice(1));
        if (target === undefined) removeAttr(e, "href");
        else if (target !== i) setAttr(e, "href", `chapter${target + 1}.xhtml${href}`);
      }
      return undefined;
    });
  });

  const items: ZipItem[] = [
    { name: "mimetype", data: "application/epub+zip", store: true },
    { name: "META-INF/container.xml", data: CONTAINER },
  ];
  chapters.forEach((c, i) => {
    items.push({ name: `OEBPS/chapter${i + 1}.xhtml`, data: head(c.title, language, rtl) + serialize(c.nodes, { xml: true }) + "\n</body>\n</html>\n" });
  });
  for (const p of pictures) items.push({ name: `OEBPS/${p.name}`, data: p.bytes, store: true });
  items.push({ name: "OEBPS/nav.xhtml", data: nav(chapters.map((c) => c.title), book.title, language, rtl) });
  items.push({ name: "OEBPS/style.css", data: CSS });

  let manifest = '    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>\n';
  manifest += '    <item id="css" href="style.css" media-type="text/css"/>\n';
  chapters.forEach((_, i) => (manifest += `    <item id="chapter${i + 1}" href="chapter${i + 1}.xhtml" media-type="application/xhtml+xml"/>\n`));
  pictures.forEach((p, i) => (manifest += `    <item id="image${i + 1}" href="${p.name}" media-type="${p.mime}"/>\n`));
  const spine = chapters.map((_, i) => `    <itemref idref="chapter${i + 1}"/>\n`).join("");
  items.push({ name: "OEBPS/content.opf", data: packageDocument({ title: book.title, author: book.author, language, rtl, manifest, spine, fixedLayout: false }) });
  return writeZip(items);
}

export interface PicturePage {
  bytes: Uint8Array;
  mime: string;
  width: number;
  height: number;
}

/** A fixed-layout EPUB with one page per picture (comics). `rtl` turns the pages right to left (manga). */
export function writePictureBook(o: { title: string; author?: string; language: string; rtl: boolean; pages: readonly PicturePage[] }): Uint8Array {
  const language = primaryLanguage(o.language);
  const digits = Math.max(3, String(o.pages.length).length);
  const items: ZipItem[] = [
    { name: "mimetype", data: "application/epub+zip", store: true },
    { name: "META-INF/container.xml", data: CONTAINER },
  ];
  let manifest = '    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>\n';
  manifest += '    <item id="css" href="style.css" media-type="text/css"/>\n';
  let spine = "";
  o.pages.forEach((page, i) => {
    const number = String(i + 1).padStart(digits, "0");
    const image = `images/page${number}.${pictureExtension(page.mime)}`;
    items.push({ name: `OEBPS/${image}`, data: page.bytes, store: true });
    const xhtml =
      head(`${o.title} ${i + 1}`, language, o.rtl, { width: page.width, height: page.height }) +
      `<div class="page"><img src="${image}" alt="${i + 1}" width="${page.width}" height="${page.height}"/></div>\n</body>\n</html>\n`;
    items.push({ name: `OEBPS/page${number}.xhtml`, data: xhtml });
    manifest += `    <item id="p${number}" href="page${number}.xhtml" media-type="application/xhtml+xml"/>\n`;
    manifest += `    <item id="i${number}" href="${image}" media-type="${page.mime}"/>\n`;
    spine += `    <itemref idref="p${number}"/>\n`;
  });
  items.push({ name: "OEBPS/nav.xhtml", data: nav([o.title], o.title, language, o.rtl, `page${"1".padStart(digits, "0")}.xhtml`) });
  items.push({ name: "OEBPS/style.css", data: "html,body{margin:0;padding:0}.page{margin:0;padding:0}.page img{display:block;width:100%;height:100%}\n" });
  items.push({ name: "OEBPS/content.opf", data: packageDocument({ title: o.title, author: o.author, language, rtl: o.rtl, manifest, spine, fixedLayout: true }) });
  return writeZip(items);
}
