// SPDX-License-Identifier: GPL-3.0-or-later
// The shared e-book model of the web converters, ported from the Windows app (src/DropFlow.Engines.Pdf/Books/BookHtml.cs and
// Office/EpubReader.cs): a title, an author, a language and ONE body of <section> chapters. Pictures are kept apart as bytes; an <img>
// in the body points at one with `df-img:<index>` (no base64 strings in the tree). Readers clean what they bring in (no scripts, styles,
// forms or event handlers; only safe links); writers take the pictures out again as files, binaries or data: URIs.

import { base64Parts, base64ToBytes } from "./sniff-base64";
import {
  attr,
  descendants,
  el,
  elements,
  localName,
  removeAttr,
  rewrite,
  setAttr,
  text,
  textContent,
  type MNode,
} from "./markup";

export interface Picture {
  mime: string;
  bytes: Uint8Array;
}

export interface Book {
  title: string;
  author?: string;
  /** As declared ("ar", "ar-SA"...) or guessed from the letters. */
  language: string;
  /** Children of the book's body: usually one <section id="chN"> per chapter. */
  body: MNode[];
  pictures: Picture[];
}

export const PICTURE_SCHEME = "df-img:";
export const MAX_IMAGE_BYTES = 8 * 1024 * 1024;

export const REMOVED_ELEMENTS: ReadonlySet<string> = new Set([
  "script", "style", "link", "meta", "iframe", "object", "embed", "form", "input", "button", "select", "textarea", "base", "template",
  "noscript", "audio", "video", "canvas", "head", "title",
]);
export const KEPT_ATTRIBUTES: ReadonlySet<string> = new Set(["id", "dir", "lang", "href", "src", "alt", "title", "colspan", "rowspan", "start", "type"]);

// ---- language ---------------------------------------------------------------------------------------------------------------------

const RIGHT_TO_LEFT = ["ar", "he", "iw", "fa", "ur", "ps", "yi", "ckb", "sd", "ug", "dv", "syr"];

/** The primary subtag in lower case ("ar-SA" -> "ar"), or "en" for nothing. */
export function primaryLanguage(language: string | undefined): string {
  const p = (language ?? "").split(/[-_]/)[0].trim().toLowerCase();
  return /^[a-z]{2,8}$/.test(p) ? p : "en";
}

/** True for a language written right to left ("ar", "ar-SA", "fa-IR"...). */
export function isRtl(language: string | undefined): boolean {
  return RIGHT_TO_LEFT.includes(primaryLanguage(language));
}

/** Guesses the language of a text from its letters (Arabic, Hebrew, otherwise English), like the Windows app. */
export function detectLanguage(sample: string): string {
  let arabic = 0;
  let hebrew = 0;
  let latin = 0;
  const s = sample.length > 200_000 ? sample.slice(0, 200_000) : sample;
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if ((c >= 0x0600 && c <= 0x06ff) || (c >= 0x0750 && c <= 0x077f) || (c >= 0xfb50 && c <= 0xfeff)) arabic++;
    else if (c >= 0x0590 && c <= 0x05ff) hebrew++;
    else if ((c >= 65 && c <= 90) || (c >= 97 && c <= 122)) latin++;
  }
  if (arabic > latin && arabic >= hebrew) return "ar";
  if (hebrew > latin) return "he";
  return "en";
}

// ---- pictures -----------------------------------------------------------------------------------------------------------------------

/** The picture type from its first bytes: JPEG, PNG, GIF, BMP or WebP (SVG and others: null). */
export function sniffImage(b: Uint8Array): string | null {
  if (b.length >= 3 && b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff) return "image/jpeg";
  if (b.length >= 8 && b[0] === 0x89 && b[1] === 0x50 && b[2] === 0x4e && b[3] === 0x47) return "image/png";
  if (b.length >= 4 && b[0] === 0x47 && b[1] === 0x49 && b[2] === 0x46 && b[3] === 0x38) return "image/gif";
  if (b.length >= 2 && b[0] === 0x42 && b[1] === 0x4d) return "image/bmp";
  if (b.length >= 12 && b[0] === 0x52 && b[1] === 0x49 && b[2] === 0x46 && b[3] === 0x46 && b[8] === 0x57 && b[9] === 0x45 && b[10] === 0x42 && b[11] === 0x50) return "image/webp";
  return null;
}

export function pictureExtension(mime: string): string {
  switch (mime) {
    case "image/jpeg":
      return "jpg";
    case "image/png":
      return "png";
    case "image/gif":
      return "gif";
    case "image/bmp":
      return "bmp";
    case "image/webp":
      return "webp";
    case "image/svg+xml":
      return "svg";
    default:
      return "bin";
  }
}

/** A picture's type from its file extension (what the EPUB reader trusts), or null for anything a book cannot show. */
export function mimeForPictureName(name: string): string | null {
  const ext = name.split(/[?#]/)[0].split(".").pop()?.toLowerCase() ?? "";
  switch (ext) {
    case "png":
      return "image/png";
    case "jpg":
    case "jpeg":
      return "image/jpeg";
    case "gif":
      return "image/gif";
    case "webp":
      return "image/webp";
    case "svg":
      return "image/svg+xml";
    default:
      return null;
  }
}

/** Collects pictures without duplicates (same key = same picture). */
export class PictureStore {
  readonly pictures: Picture[] = [];
  private readonly byKey = new Map<string, string>();

  /** The `df-img:` reference of a picture, adding it on first use. */
  add(key: string, mime: string, bytes: Uint8Array): string {
    const known = this.byKey.get(key);
    if (known) return known;
    const ref = `${PICTURE_SCHEME}${this.pictures.length}`;
    this.pictures.push({ mime, bytes });
    this.byKey.set(key, ref);
    return ref;
  }

  /** A data: URI picture (base64 only); null for anything else or a picture that is not one. */
  addDataUri(src: string): string | null {
    const m = /^data:(image\/[a-z0-9.+-]+);base64,/i.exec(src.trim());
    if (!m) return null;
    const known = this.byKey.get(src);
    if (known) return known;
    let bytes: Uint8Array;
    try {
      bytes = base64ToBytes(src.trim());
    } catch {
      return null;
    }
    if (bytes.length === 0 || bytes.length > MAX_IMAGE_BYTES) return null;
    return this.add(src, m[1].toLowerCase(), bytes);
  }
}

/** Index of a `df-img:` reference, or -1. */
export function pictureIndex(src: string | undefined): number {
  if (!src || !src.startsWith(PICTURE_SCHEME)) return -1;
  const n = Number(src.slice(PICTURE_SCHEME.length));
  return Number.isInteger(n) && n >= 0 ? n : -1;
}

/** A base64 data: URI (built in parts: no quadratic string growth for big pictures). */
export function dataUri(picture: Picture): string {
  const dec = new TextDecoder("ascii");
  let out = `data:${picture.mime};base64,`;
  for (const part of base64Parts(picture.bytes)) out += dec.decode(part);
  return out;
}

// ---- cleaning -----------------------------------------------------------------------------------------------------------------------

/** Keeps in-page anchors and ordinary web/mail links; anything else (javascript:, file:, other files) loses its link. */
export function defaultLink(href: string): string | null {
  const h = href.trim();
  if (h.startsWith("#")) return h.length > 1 ? h : null;
  const colon = h.indexOf(":");
  if (colon > 0 && !h.slice(0, colon).includes("/")) {
    const scheme = h.slice(0, colon).toLowerCase();
    return scheme === "http" || scheme === "https" || scheme === "mailto" ? h : null;
  }
  return null;
}

export interface CleanOptions {
  /** Maps an img src (not a data: URI) to a `df-img:` reference, or null to drop the picture and keep its alt text. */
  resolveImage: (src: string) => string | null;
  /** Maps an href; null drops the link and keeps its text. Default: `defaultLink`. */
  rewriteLink?: (href: string) => string | null;
  /** Receives data: URI pictures already in the markup. */
  store: PictureStore;
}

/**
 * Cleans one chapter: inline SVG becomes the picture it wraps (or goes), unsafe elements and attributes go (xml:lang becomes lang so the
 * direction metadata survives), links are rewritten and pictures resolved. Works in place on `nodes` and returns the new list.
 */
export function cleanNodes(nodes: MNode[], options: CleanOptions): MNode[] {
  const rewriteLink = options.rewriteLink ?? defaultLink;
  return rewrite(nodes, (e) => {
    const name = localName(e.name).toLowerCase();
    if (name === "svg") {
      const image = descendants(e).find((d) => localName(d.name).toLowerCase() === "image");
      const href = image ? attr(image, "href", true) : undefined;
      if (!href) return [];
      return cleanNodes([el("img", [["src", href]])], options);
    }
    if (REMOVED_ELEMENTS.has(name)) return [];
    if (name === "html" || name === "body") return cleanNodes(e.children, options); // a page pasted whole: keep what it holds
    if (name !== e.name) e.name = name; // "xhtml:p" -> "p", "P" -> "p"
    const xmlLang = attr(e, "xml:lang");
    e.attrs = e.attrs.filter(([k]) => KEPT_ATTRIBUTES.has(k.toLowerCase())).map(([k, v]) => [k.toLowerCase(), v]);
    if (xmlLang && !attr(e, "lang")) e.attrs.push(["lang", xmlLang]);
    if (name === "a") {
      const href = attr(e, "href");
      if (href !== undefined) {
        const rewritten = rewriteLink(href);
        if (rewritten === null) removeAttr(e, "href");
        else setAttr(e, "href", rewritten);
      }
    }
    if (name === "img") {
      const src = attr(e, "src") ?? "";
      const resolved = src.startsWith(PICTURE_SCHEME)
        ? src
        : /^data:image\//i.test(src.trim())
          ? options.store.addDataUri(src)
          : src
            ? options.resolveImage(src)
            : null;
      if (resolved) {
        setAttr(e, "src", resolved);
        return [e];
      }
      const alt = attr(e, "alt");
      return alt && alt.trim() ? [text(alt)] : [];
    }
    return undefined;
  });
}

/** Replaces every picture reference with what `name` returns for it (once per picture, numbered by first appearance); null drops it. */
export function extractPictures(nodes: MNode[], pictures: readonly Picture[], name: (index: number, picture: Picture) => string | null): MNode[] {
  const seen = new Map<number, string | null>();
  return rewrite(nodes, (e) => {
    if (e.name !== "img") return undefined;
    const index = pictureIndex(attr(e, "src"));
    const picture = index >= 0 ? pictures[index] : undefined;
    let target: string | null = null;
    if (picture) {
      if (!seen.has(index)) seen.set(index, name(seen.size + 1, picture));
      target = seen.get(index) ?? null;
    }
    if (target) {
      setAttr(e, "src", target);
      return [e];
    }
    const alt = attr(e, "alt");
    return alt && alt.trim() ? [text(alt)] : [];
  });
}

/** Deep copy of a node list (writers change the tree they are given). */
export function cloneNodes(nodes: readonly MNode[]): MNode[] {
  return nodes.map((n) => (n.type === "text" ? { type: "text", text: n.text } : { type: "el", name: n.name, attrs: n.attrs.map(([k, v]) => [k, v] as [string, string]), children: cloneNodes(n.children) }));
}

// ---- chapters -----------------------------------------------------------------------------------------------------------------------

export interface Chapter {
  id: string;
  title: string;
  nodes: MNode[];
}

const isBlank = (n: MNode) => n.type === "text" && n.text.trim() === "";
const collapse = (s: string) => s.replace(/\s+/g, " ").trim();
const WRAPPERS = new Set(["section", "div", "article", "main", "body"]);

/** Plain text of nodes (whitespace collapsed). */
export function plainText(nodes: readonly MNode[]): string {
  return collapse(textContent(nodes));
}

function hasPicture(nodes: readonly MNode[]): boolean {
  return descendants(nodes).some((d) => d.name === "img");
}

/**
 * Splits a book body into chapters: one per top-level <section>. A body without sections (or with one wrapper around several headings)
 * is cut before each top heading that occurs at least twice (h1, else h2).
 */
export function chaptersOf(body: readonly MNode[], bookTitle: string): Chapter[] {
  let root: MNode[] = [...body];
  for (;;) {
    const els = elements(root);
    if (els.length === 1 && WRAPPERS.has(els[0].name) && root.every((n) => n.type === "el" || isBlank(n))) root = els[0].children;
    else break;
  }
  const sections = elements(root).filter((e) => e.name === "section");
  const hasOther = root.some((n) => (n.type === "el" ? n.name !== "section" : n.text.trim() !== ""));
  const parts: { id?: string; nodes: MNode[] }[] =
    sections.length > 1 && !hasOther ? sections.map((s) => ({ id: attr(s, "id"), nodes: s.children })) : splitAtHeadings(root);

  const chapters: Chapter[] = [];
  for (const part of parts) {
    if (!plainText(part.nodes) && !hasPicture(part.nodes)) continue;
    const heading = descendants(part.nodes).find((d) => d.name === "h1" || d.name === "h2" || d.name === "h3");
    const headingText = heading ? collapse(textContent(heading)) : "";
    const title = headingText || (chapters.length === 0 ? bookTitle : `${chapters.length + 1}`);
    chapters.push({ id: part.id ?? `ch${chapters.length + 1}`, title, nodes: part.nodes });
  }
  if (chapters.length === 0) chapters.push({ id: "ch1", title: bookTitle, nodes: [el("p")] });
  return chapters;
}

function splitAtHeadings(root: readonly MNode[]): { nodes: MNode[] }[] {
  const count = (n: string) => elements(root).filter((e) => e.name === n).length;
  const level = count("h1") >= 2 ? "h1" : count("h2") >= 2 ? "h2" : null;
  const parts: { nodes: MNode[] }[] = [];
  let current: MNode[] = [];
  for (const node of root) {
    if (level && node.type === "el" && node.name === level && current.length > 0 && plainText(current)) {
      parts.push({ nodes: current });
      current = [];
    }
    current.push(node);
  }
  if (current.length > 0) parts.push({ nodes: current });
  return parts;
}

// ---- a complete HTML page -----------------------------------------------------------------------------------------------------------

export function escapeHtml(s: string): string {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}
