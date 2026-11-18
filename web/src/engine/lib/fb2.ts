// SPDX-License-Identifier: GPL-3.0-or-later
// FictionBook 2 (FB2), ported from the Windows app (src/DropFlow.Engines.Pdf/Books/Fb2.cs): one XML file with the book's description,
// its text as nested <section>s of paragraphs, and the pictures as base64 <binary> elements. Reading turns it into the shared book
// body; writing builds FB2 from it (UTF-8; JPEG and PNG pictures as binaries).

import { ConvertError } from "../types";
import { BookZip, isZip } from "./book-zip";
import {
  PictureStore,
  chaptersOf,
  cleanNodes,
  cloneNodes,
  detectLanguage,
  escapeHtml,
  extractPictures,
  primaryLanguage,
  sniffImage,
  MAX_IMAGE_BYTES,
  pictureExtension,
  type Book,
} from "./book-model";
import { base64ToBytes, bytesToBase64 } from "./sniff-base64";
import { attr, bodyOf, childNamed, childrenNamed, el, elements, localName, parseMarkup, serialize, text, textContent, type MElement, type MNode } from "./markup";

export const FB2_NS = "http://www.gribuser.ru/xml/fictionbook/2.0";
export const XLINK_NS = "http://www.w3.org/1999/xlink";

// ---- reading -----------------------------------------------------------------------------------------------------------------------

/** The text of an FB2 file, honouring its byte order mark or declared encoding (often windows-1251). */
export function decodeFb2(bytes: Uint8Array): string {
  if (bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) return new TextDecoder("utf-8").decode(bytes.subarray(3));
  if (bytes.length >= 2 && bytes[0] === 0xff && bytes[1] === 0xfe) return new TextDecoder("utf-16le").decode(bytes.subarray(2));
  if (bytes.length >= 2 && bytes[0] === 0xfe && bytes[1] === 0xff) return new TextDecoder("utf-16be").decode(bytes.subarray(2));
  let head = "";
  for (let i = 0; i < Math.min(bytes.length, 256); i++) head += String.fromCharCode(bytes[i]);
  const declared = /^\s*<\?xml[^>]*encoding\s*=\s*["']([\w.:-]+)["']/i.exec(head)?.[1];
  if (declared && !/^utf-?8$/i.test(declared)) {
    try {
      return new TextDecoder(declared.toLowerCase()).decode(bytes);
    } catch {
      /* an encoding the browser does not know: read as UTF-8 */
    }
  }
  return new TextDecoder("utf-8").decode(bytes);
}

const cleanText = (s: string | undefined) => (s ?? "").split(/\s+/).filter(Boolean).join(" ") || undefined;

export function readFb2(input: Uint8Array, fallbackTitle: string): Book {
  let bytes = input;
  if (isZip(bytes)) {
    const zip = new BookZip(bytes);
    const name = zip.names.find((n) => n.toLowerCase().endsWith(".fb2"));
    if (!name) throw new ConvertError("notABook");
    bytes = zip.read(name)!;
  }
  const doc = parseMarkup(decodeFb2(bytes), { xml: true });
  const root = elements(doc.children).find((e) => localName(e.name) === "FictionBook");
  if (!root) throw new ConvertError("notABook");

  const info = childNamed(childNamed(root, "description"), "title-info");
  const title = cleanText(textContent(childNamed(info, "book-title") ?? [])) ?? fallbackTitle;
  const authors = (info ? childrenNamed(info, "author") : []).map(authorName).filter((a) => a.length > 0);
  const language = cleanText(textContent(childNamed(info, "lang") ?? []));

  const store = new PictureStore();
  const binaries = new Map<string, string | null>();
  for (const binary of childrenNamed(root, "binary")) {
    const id = attr(binary, "id");
    if (id === undefined || binaries.has(id)) continue;
    binaries.set(id, picture(binary, id, store));
  }

  const reader = new Fb2HtmlReader(binaries);
  let html = "";
  let chapter = 0;
  childrenNamed(root, "body").forEach((fb2Body, index) => {
    const isNotes = index > 0 && attr(fb2Body, "name") !== undefined;
    const children = elements(fb2Body.children);
    const leading = children.filter((e) => ["title", "epigraph", "image"].includes(localName(e.name)));
    if (leading.length > 0 && !isNotes) {
      let part = "";
      for (const e of leading) part += reader.block(e, 1);
      html += `<section id="ch${++chapter}">${part}</section>\n`;
    }
    if (isNotes) {
      let part = "";
      for (const e of children) part += reader.block(e, 1);
      html += `<section id="ch${++chapter}">${part}</section>\n`;
      return;
    }
    for (const section of children.filter((e) => localName(e.name) === "section")) {
      html += `<section id="ch${++chapter}">${reader.section(section, 1)}</section>\n`;
    }
  });
  if (chapter === 0) throw new ConvertError("bookEmpty");

  const body = cleanNodes(bodyOf(parseMarkup(html)).children, { store, resolveImage: () => null });
  return {
    title,
    author: authors.length > 0 ? authors.join(", ") : undefined,
    language: language ?? detectLanguage(textContent(body)),
    body,
    pictures: store.pictures,
  };
}

function authorName(author: MElement): string {
  const parts = ["first-name", "middle-name", "last-name"].map((p) => cleanText(textContent(childNamed(author, p) ?? []))).filter((p): p is string => !!p);
  if (parts.length > 0) return parts.join(" ");
  return cleanText(textContent(childNamed(author, "nickname") ?? [])) ?? "";
}

function picture(binary: MElement, id: string, store: PictureStore): string | null {
  let bytes: Uint8Array;
  try {
    bytes = base64ToBytes(textContent(binary).replace(/\s+/g, ""));
  } catch {
    return null;
  }
  const mime = sniffImage(bytes);
  if (!mime || bytes.length > MAX_IMAGE_BYTES) return null;
  return store.add(`#${id}`, mime, bytes);
}

const enc = escapeHtml;

/** FB2 elements to HTML. Sections become headings of increasing level; poems, epigraphs and citations become quotes. */
class Fb2HtmlReader {
  constructor(private readonly binaries: ReadonlyMap<string, string | null>) {}

  section(section: MElement, depth: number): string {
    let html = "";
    const id = attr(section, "id");
    if (id !== undefined) html += `<a id="${enc(id)}"></a>`;
    for (const child of elements(section.children)) {
      html += localName(child.name) === "section" ? this.section(child, Math.min(depth + 1, 6)) : this.block(child, depth);
    }
    return html;
  }

  block(e: MElement, depth: number): string {
    const id = attr(e, "id");
    const idAttr = id === undefined ? "" : ` id="${enc(id)}"`;
    switch (localName(e.name)) {
      case "title": {
        const lines = elements(e.children).filter((p) => localName(p.name) === "p").map((p) => this.inline(p));
        return `<h${depth}${idAttr}>${lines.join("<br>")}</h${depth}>`;
      }
      case "subtitle": {
        const level = Math.min(depth + 1, 6);
        return `<h${level}${idAttr}>${this.inline(e)}</h${level}>`;
      }
      case "p":
        return `<p${idAttr}>${this.inline(e)}</p>`;
      case "empty-line":
        return "<p><br></p>";
      case "image":
        return `<p>${this.image(e)}</p>`;
      case "epigraph":
      case "cite":
      case "annotation":
        return `<blockquote${idAttr}>${elements(e.children).map((c) => this.block(c, depth)).join("")}</blockquote>`;
      case "text-author":
        return `<p><em>${this.inline(e)}</em></p>`;
      case "poem": {
        let html = `<blockquote${idAttr}>`;
        for (const child of elements(e.children)) {
          if (localName(child.name) === "stanza") {
            const verses = elements(child.children)
              .filter((v) => ["v", "subtitle", "title"].includes(localName(v.name)))
              .map((v) => this.inline(localName(v.name) === "title" ? (elements(v.children)[0] ?? v) : v));
            html += `<p>${verses.join("<br>")}</p>`;
          } else html += this.block(child, depth);
        }
        return html + "</blockquote>";
      }
      case "table": {
        let html = "<table>";
        for (const row of elements(e.children).filter((r) => localName(r.name) === "tr")) {
          html += "<tr>";
          for (const cell of elements(row.children)) {
            const tag = localName(cell.name) === "th" ? "th" : "td";
            html += `<${tag}>${this.inline(cell)}</${tag}>`;
          }
          html += "</tr>";
        }
        return html + "</table>";
      }
      case "section":
        return this.section(e, Math.min(depth + 1, 6));
      default:
        return textContent(e).trim() ? `<p>${this.inline(e)}</p>` : "";
    }
  }

  inline(e: MElement): string {
    let html = "";
    for (const node of e.children) {
      if (node.type === "text") {
        html += enc(node.text);
        continue;
      }
      const name = localName(node.name);
      const tag = ({ strong: "strong", emphasis: "em", strikethrough: "s", sub: "sub", sup: "sup", code: "code" } as Record<string, string>)[name];
      if (tag) html += `<${tag}>${this.inline(node)}</${tag}>`;
      else if (name === "a") {
        const href = attr(node, "href", true);
        const note = attr(node, "type") === "note";
        html += href === undefined ? "<a>" : `<a href="${enc(href)}">`;
        html += note ? `<sup>${this.inline(node)}</sup>` : this.inline(node);
        html += "</a>";
      } else if (name === "image") html += this.image(node);
      else html += this.inline(node); // style and unknown inline elements: keep the text
    }
    return html;
  }

  image(image: MElement): string {
    const href = attr(image, "href", true);
    const alt = attr(image, "alt") ?? attr(image, "title") ?? "";
    const data = href?.startsWith("#") ? this.binaries.get(href.slice(1)) : undefined;
    if (data) return `<img src="${data}" alt="${enc(alt)}">`;
    return alt ? enc(alt) : "";
  }
}

// ---- writing -----------------------------------------------------------------------------------------------------------------------

/** Writes the book as FB2 (UTF-8). Each chapter becomes a top-level section; JPEG and PNG pictures become binaries, others go (alt text). */
export function writeFb2(book: Book): Uint8Array {
  const language = primaryLanguage(book.language);
  const binaries: MElement[] = [];
  const body = extractPictures(cloneNodes(book.body), book.pictures, (index, p) => {
    if (p.mime !== "image/jpeg" && p.mime !== "image/png") return null;
    const id = `image${index}.${pictureExtension(p.mime)}`;
    binaries.push(el("binary", [["id", id], ["content-type", p.mime]], [text(bytesToBase64(p.bytes))]));
    return `#${id}`;
  });

  const sections = chaptersOf(body, book.title).map((c) => new Fb2Builder().section(c.nodes, c.title));
  const today = new Date().toISOString().slice(0, 10);
  const fiction = el("FictionBook", [["xmlns", FB2_NS], ["xmlns:l", XLINK_NS]], [
    el("description", [], [
      el("title-info", [], [
        el("genre", [], [text("nonfiction")]),
        authorElement(book.author),
        el("book-title", [], [text(book.title)]),
        el("lang", [], [text(language)]),
      ]),
      el("document-info", [], [
        el("author", [], [el("nickname", [], [text("DropFlow")])]),
        el("program-used", [], [text("DropFlow")]),
        el("date", [["value", today]], [text(today)]),
        el("id", [], [text(globalThis.crypto?.randomUUID?.() ?? `dropflow-${Date.now()}`)]),
        el("version", [], [text("1.0")]),
      ]),
    ]),
    el("body", [], [el("title", [], [el("p", [], [text(book.title)])]), ...sections]),
    ...binaries,
  ]);
  return new TextEncoder().encode('<?xml version="1.0" encoding="utf-8"?>' + serialize([fiction], { xml: true, selfCloseEmpty: true }));
}

function authorElement(author: string | undefined): MElement {
  const name = author?.split(",")[0].trim();
  if (!name) return el("author", [], [el("nickname", [], [text("Unknown")])]);
  const words = name.split(" ").filter(Boolean);
  if (words.length === 1) return el("author", [], [el("nickname", [], [text(words[0])])]);
  return el("author", [], [el("first-name", [], [text(words.slice(0, -1).join(" "))]), el("last-name", [], [text(words[words.length - 1])])]);
}

const FB2_BLOCK_CHILDREN = new Set(["p", "subtitle", "empty-line", "table", "poem"]);
const IS_BLOCK = new Set([
  "p", "div", "section", "article", "blockquote", "ul", "ol", "table", "pre", "h1", "h2", "h3", "h4", "h5", "h6", "figure", "header", "footer",
  "aside", "main", "nav", "hr", "dl", "img",
]);

const normalize = (s: string, keepEdges = false) => {
  const collapsed = s.replace(/\s+/g, " ");
  return keepEdges ? collapsed : collapsed.trim();
};

/** Turns chapter HTML into FB2 section content: paragraphs with inline formatting, subtitles, citations, tables and images. */
class Fb2Builder {
  section(nodes: readonly MNode[], title: string): MElement {
    const section = el("section");
    const content: MElement[] = [];
    const state = { titled: false };
    for (const node of nodes) this.blocks(node, content, state, section);
    if (!state.titled) section.children.push(el("title", [], [el("p", [], [text(title)])]));
    section.children.push(...content);
    if (content.length === 0) section.children.push(el("empty-line"));
    return section;
  }

  private blocks(node: MNode, output: MElement[], state: { titled: boolean }, section: MElement): void {
    if (node.type === "text") {
      if (node.text.trim()) output.push(el("p", [], [text(normalize(node.text))]));
      return;
    }
    const e = node;
    switch (e.name) {
      case "h1":
      case "h2":
      case "h3":
      case "h4":
      case "h5":
      case "h6": {
        const heading = normalize(textContent(e));
        if (!heading) return;
        if (!state.titled && output.length === 0) {
          section.children.push(el("title", [], [el("p", [], [text(heading)])]));
          state.titled = true;
        } else output.push(el("subtitle", [], [text(heading)]));
        return;
      }
      case "p":
      case "figcaption":
      case "dt":
      case "dd":
      case "caption":
        this.paragraphs(e, output);
        return;
      case "blockquote": {
        const inner: MElement[] = [];
        const dummy = { titled: true };
        for (const child of e.children) this.blocks(child, inner, dummy, section);
        const cite = el("cite", [], inner.map((item) => (FB2_BLOCK_CHILDREN.has(item.name) ? item : el("p", [], [text(textContent(item))]))));
        if (cite.children.length) output.push(cite);
        return;
      }
      case "ul":
      case "ol":
        this.list(e, output, 0);
        return;
      case "table": {
        const table = el("table");
        const rows: MElement[] = [];
        const collect = (x: MElement) => {
          for (const c of elements(x.children)) {
            if (c.name === "tr") rows.push(c);
            else if (c.name === "thead" || c.name === "tbody" || c.name === "tfoot") collect(c);
          }
        };
        collect(e);
        for (const row of rows) {
          const tr = el("tr");
          for (const cell of elements(row.children).filter((c) => c.name === "td" || c.name === "th")) {
            const cellElement = el(cell.name === "th" ? "th" : "td");
            this.appendInline(cell, cellElement);
            tr.children.push(cellElement);
          }
          if (tr.children.length) table.children.push(tr);
        }
        if (table.children.length) output.push(table);
        return;
      }
      case "pre":
        for (const line of textContent(e).replace(/\r\n/g, "\n").split("\n")) output.push(line ? el("p", [], [el("code", [], [text(line)])]) : el("empty-line"));
        return;
      case "img": {
        const image = this.image(e);
        if (image) output.push(image);
        return;
      }
      case "hr":
      case "br":
        output.push(el("empty-line"));
        return;
      default:
        if (elements(e.children).some((c) => IS_BLOCK.has(c.name))) {
          for (const child of e.children) this.blocks(child, output, state, section);
        } else this.paragraphs(e, output);
    }
  }

  /** List items as paragraphs with their marker; a nested list follows its item, indented (FB2 has no lists). */
  private list(list: MElement, output: MElement[], depth: number): void {
    let number = 1;
    for (const item of elements(list.children).filter((c) => c.name === "li")) {
      const marker = list.name === "ol" ? `${number++}. ` : "• ";
      const nested = elements(item.children).filter((c) => c.name === "ul" || c.name === "ol");
      const p = el("p", [], [text("  ".repeat(depth) + marker)]);
      this.appendInline({ ...item, children: item.children.filter((c) => !nested.includes(c as MElement)) }, p);
      output.push(p);
      for (const sub of nested) this.list(sub, output, depth + 1);
    }
  }

  /** One or more <p>: an FB2 paragraph cannot hold a line break, so each <br> starts a new one. */
  private paragraphs(e: MElement, output: MElement[]): void {
    let paragraph = el("p");
    const produced = [paragraph];
    this.appendInline(e, paragraph, () => {
      paragraph = el("p");
      produced.push(paragraph);
      return paragraph;
    });
    const meaningful = (p: MElement) => textContent(p).trim() !== "" || descendantsNamed(p, "image");
    for (const p of produced) if (meaningful(p)) output.push(p);
    if (!produced.some(meaningful) && !descendantsNamed(e, "img") && output.length > 0) output.push(el("empty-line"));
  }

  private appendInline(node: MElement, target: MElement, breakLine?: () => MElement): void {
    let current = target;
    for (const child of node.children) {
      if (child.type === "text") {
        current.children.push(text(normalize(child.text, true)));
        continue;
      }
      switch (child.name) {
        case "br":
          if (breakLine) current = breakLine();
          else current.children.push(text(" "));
          break;
        case "b":
        case "strong":
          current.children.push(this.styled("strong", child));
          break;
        case "i":
        case "em":
        case "cite":
        case "dfn":
          current.children.push(this.styled("emphasis", child));
          break;
        case "s":
        case "strike":
        case "del":
          current.children.push(this.styled("strikethrough", child));
          break;
        case "sub":
        case "sup":
          current.children.push(this.styled(child.name, child));
          break;
        case "code":
        case "tt":
        case "kbd":
        case "samp":
          current.children.push(this.styled("code", child));
          break;
        case "a": {
          const href = attr(child, "href");
          if (href && /^(https?:\/\/|mailto:)/i.test(href)) {
            const a = el("a", [["l:href", href]]);
            this.appendInline(child, a);
            current.children.push(a);
          } else this.appendInline(child, current, breakLine);
          break;
        }
        case "img": {
          const image = this.image(child);
          if (image) current.children.push(image);
          break;
        }
        default:
          this.appendInline(child, current, breakLine);
      }
    }
  }

  private styled(name: string, e: MElement): MElement {
    const styled = el(name);
    this.appendInline(e, styled);
    return styled;
  }

  private image(img: MElement): MElement | null {
    const src = attr(img, "src");
    if (!src || !src.startsWith("#")) return null;
    const image = el("image", [["l:href", src]]);
    const alt = attr(img, "alt");
    if (alt && alt.trim()) image.attrs.push(["alt", alt]);
    return image;
  }
}

function descendantsNamed(e: MElement, name: string): boolean {
  const stack: MNode[] = [...e.children];
  while (stack.length) {
    const n = stack.pop()!;
    if (n.type !== "el") continue;
    if (n.name === name) return true;
    stack.push(...n.children);
  }
  return false;
}
