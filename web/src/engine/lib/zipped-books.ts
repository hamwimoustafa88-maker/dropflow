// SPDX-License-Identifier: GPL-3.0-or-later
// HTMLZ and TXTZ, the zipped HTML and zipped text books e-book managers such as calibre exchange, ported from the Windows app
// (src/DropFlow.Engines.Pdf/Books/ZippedBooks.cs): one index.html (or index.txt, Markdown), its pictures in an images folder, and the
// book's title, author and language in a metadata.opf file.

import { ConvertError } from "../types";
import { BookZip, writeZip, type ZipItem } from "./book-zip";
import { MAX_IMAGE_BYTES, PictureStore, cleanNodes, cloneNodes, detectLanguage, escapeHtml, extractPictures, isRtl, pictureExtension, primaryLanguage, sniffImage, type Book } from "./book-model";
import { blocksToMarkdown, htmlToBlocks, markdownToNodes } from "./book-text";
import { resolvePath } from "./epub";
import { bodyOf, descendants, escapeText, firstDescendant, localName, parseMarkup, serialize, textContent, type MNode } from "./markup";
import { decodeText } from "./text-decode";

const depth = (name: string) => name.split("/").length;

export function readZippedBook(bytes: Uint8Array, kind: "htmlz" | "txtz", fallbackTitle: string): Book {
  const zip = new BookZip(bytes);
  const isText = kind === "txtz";
  const main = isText
    ? (zip.find("index.txt") ?? zip.names.filter((n) => /\.(txt|md|text|markdown)$/i.test(n)).sort((a, b) => depth(a) - depth(b))[0])
    : (zip.find("index.html") ?? zip.names.filter((n) => /\.(html|htm|xhtml)$/i.test(n)).sort((a, b) => depth(a) - depth(b))[0]);
  if (!main) throw new ConvertError("notABook");
  const data = zip.read(main)!;
  const baseDir = main.includes("/") ? main.slice(0, main.lastIndexOf("/") + 1) : "";

  let nodes: MNode[];
  let htmlTitle: string | undefined;
  if (isText) nodes = markdownToNodes(decodeText(data).text);
  else {
    const doc = parseMarkup(decodeHtml(data));
    htmlTitle = textContent(firstDescendant(doc, "title") ?? []).trim() || undefined;
    nodes = bodyOf(doc).children;
  }
  const store = new PictureStore();
  const body = cleanNodes(nodes, {
    store,
    resolveImage: (src) => {
      const name = zip.find(resolvePath(baseDir, src.split(/[#?]/)[0]));
      if (!name || zip.size(name) > MAX_IMAGE_BYTES) return null;
      const bytes = zip.read(name);
      const mime = bytes ? sniffImage(bytes) : null;
      return bytes && mime ? store.add(name, mime, bytes) : null;
    },
  });
  const meta = metadata(zip);
  return {
    title: meta.title ?? htmlTitle ?? fallbackTitle,
    author: meta.author,
    language: meta.language ?? detectLanguage(textContent(body)),
    body,
    pictures: store.pictures,
  };
}

function metadata(zip: BookZip): { title?: string; author?: string; language?: string } {
  const name = zip.names.find((n) => n.toLowerCase().endsWith(".opf"));
  if (!name) return {};
  const opf = parseMarkup(zip.readText(name) ?? "", { xml: true });
  const value = (local: string) => {
    const e = descendants(opf).find((d) => localName(d.name) === local);
    const v = e ? textContent(e).trim() : "";
    return v || undefined;
  };
  return { title: value("title"), author: value("creator"), language: value("language") };
}

/** HTML bytes: a declared non-UTF-8 charset wins, otherwise UTF-8 / legacy detection. */
export function decodeHtml(bytes: Uint8Array): string {
  let head = "";
  for (let i = 0; i < Math.min(bytes.length, 2048); i++) head += String.fromCharCode(bytes[i]);
  const declared = /charset\s*=\s*["']?\s*([\w-]+)/i.exec(head)?.[1];
  if (declared && !/^utf-?8$/i.test(declared) && !(bytes[0] === 0xef && bytes[1] === 0xbb)) {
    try {
      return new TextDecoder(declared.toLowerCase()).decode(bytes);
    } catch {
      /* unknown name: detect below */
    }
  }
  return decodeText(bytes).text;
}

// ---- writing -----------------------------------------------------------------------------------------------------------------------

function savePictures(book: Book, items: ZipItem[]): MNode[] {
  return extractPictures(cloneNodes(book.body), book.pictures, (index, p) => {
    const name = `images/image${index}.${pictureExtension(p.mime)}`;
    items.push({ name, data: p.bytes, store: true });
    return name;
  });
}

/** An OPF 2.0 package that carries the book's metadata (what e-book managers read from HTMLZ and TXTZ files). */
export function metadataOpf(book: Book, mainFile: string, mediaType: string): string {
  const id = globalThis.crypto?.randomUUID?.() ?? `${Date.now()}`;
  return (
    '<?xml version="1.0" encoding="utf-8"?>\n' +
    '<package version="2.0" unique-identifier="id" xmlns="http://www.idpf.org/2007/opf">' +
    '<metadata xmlns:dc="http://purl.org/dc/elements/1.1/">' +
    `<dc:title>${escapeText(book.title)}</dc:title>` +
    (book.author ? `<dc:creator>${escapeText(book.author)}</dc:creator>` : "") +
    `<dc:language>${primaryLanguage(book.language)}</dc:language>` +
    `<dc:identifier id="id">urn:uuid:${id}</dc:identifier>` +
    `</metadata><manifest><item id="main" href="${mainFile}" media-type="${mediaType}" /></manifest>` +
    '<spine><itemref idref="main" /></spine></package>'
  );
}

/** index.html (a complete page, pictures in images/), style.css and metadata.opf. */
export function writeHtmlz(book: Book): Uint8Array {
  const pictures: ZipItem[] = [];
  const body = savePictures(book, pictures);
  const language = primaryLanguage(book.language);
  let page = `<!DOCTYPE html>\n<html lang="${escapeHtml(language)}" dir="${isRtl(language) ? "rtl" : "auto"}">\n<head>\n<meta charset="utf-8">\n`;
  page += `<title>${escapeText(book.title)}</title>\n`;
  if (book.author) page += `<meta name="author" content="${escapeHtml(book.author)}">\n`;
  page += `<link rel="stylesheet" type="text/css" href="style.css">\n</head>\n<body>\n${serialize(body)}\n</body>\n</html>\n`;
  return writeZip([
    ...pictures,
    { name: "index.html", data: page },
    { name: "style.css", data: "body{font-family:serif;line-height:1.5;margin:0 5%}img{max-width:100%;height:auto}\np,li,h1,h2,h3,h4,h5,h6,blockquote{unicode-bidi:plaintext;text-align:start}\n" },
    { name: "metadata.opf", data: metadataOpf(book, "index.html", "text/html") },
  ]);
}

/** index.txt (Markdown, pictures referenced as images/...), the pictures, and metadata.opf. */
export function writeTxtz(book: Book): Uint8Array {
  const pictures: ZipItem[] = [];
  const body = savePictures(book, pictures);
  return writeZip([
    ...pictures,
    { name: "index.txt", data: blocksToMarkdown(htmlToBlocks(body)) },
    { name: "metadata.opf", data: metadataOpf(book, "index.txt", "text/plain") },
  ]);
}
