// SPDX-License-Identifier: GPL-3.0-or-later
// Comic books, ported from the Windows app (src/DropFlow.Engines.Pdf/Books/ComicTools.cs): a CBZ is a zip of pictures read in the
// natural order of their names ("page2" before "page10"); a ComicInfo.xml that says "YesAndRightToLeft" (manga), or an RTL language,
// makes the result read right to left. CBR (RAR) and CB7 (7z) are read by the Windows app only.

import { ConvertError } from "../types";
import { BookZip, isRarOr7z, isZip, writeZip, type ZipItem } from "./book-zip";
import { isRtl } from "./book-model";
import { elements, escapeText, localName, parseMarkup, textContent } from "./markup";

export const COMIC_PICTURES = ["jpg", "jpeg", "png", "gif", "webp", "bmp", "tif", "tiff", "avif", "heic", "jxl"];
const MAX_PAGE_BYTES = 200 * 1024 * 1024;
const MAX_TOTAL_BYTES = 2 * 1024 * 1024 * 1024;
const MAX_PAGES = 10_000;

export interface ComicPage {
  name: string;
  bytes: Uint8Array;
}

export interface Comic {
  pages: ComicPage[];
  title: string;
  author?: string;
  language?: string;
  rtl: boolean;
}

/** Compares names the way people number pages: runs of digits by value ("2" before "10"), the rest without regard to case. */
export function naturalCompare(a: string, b: string): number {
  const left = a.match(/\d+|\D+/g) ?? [];
  const right = b.match(/\d+|\D+/g) ?? [];
  for (let i = 0; i < Math.min(left.length, right.length); i++) {
    const x = left[i];
    const y = right[i];
    let result: number;
    if (/^\d/.test(x) && /^\d/.test(y)) {
      const xt = x.replace(/^0+/, "");
      const yt = y.replace(/^0+/, "");
      result = xt.length !== yt.length ? xt.length - yt.length : xt < yt ? -1 : xt > yt ? 1 : 0;
      if (result === 0) result = x.length - y.length;
    } else {
      const xl = x.toLowerCase();
      const yl = y.toLowerCase();
      result = xl < yl ? -1 : xl > yl ? 1 : 0;
    }
    if (result !== 0) return result;
  }
  if (left.length !== right.length) return left.length - right.length;
  return a < b ? -1 : a > b ? 1 : 0;
}

export function readComic(bytes: Uint8Array, fallbackTitle: string): Comic {
  if (isRarOr7z(bytes)) throw new ConvertError("comicArchive"); // a .cbz that is really a RAR or 7z: the Windows app reads those
  if (!isZip(bytes)) throw new ConvertError("corrupt");
  const zip = new BookZip(bytes);
  const pages: ComicPage[] = [];
  let info: Uint8Array | undefined;
  let total = 0;
  for (const name of zip.names) {
    const leaf = name.slice(name.lastIndexOf("/") + 1);
    if (/^__MACOSX\//i.test(name) || leaf.startsWith(".")) continue;
    const isInfo = leaf.toLowerCase() === "comicinfo.xml";
    const ext = leaf.includes(".") ? leaf.slice(leaf.lastIndexOf(".") + 1).toLowerCase() : "";
    if (!isInfo && !COMIC_PICTURES.includes(ext)) continue;
    if (zip.isEncrypted(name)) throw new ConvertError("encrypted");
    if (zip.size(name) > MAX_PAGE_BYTES) throw new ConvertError("tooLarge");
    const data = zip.read(name)!;
    total += data.length;
    if (total > MAX_TOTAL_BYTES) throw new ConvertError("tooLarge");
    if (isInfo) info = data;
    else pages.push({ name, bytes: data });
    if (pages.length > MAX_PAGES) throw new ConvertError("tooManyFiles", { max: MAX_PAGES });
  }
  if (pages.length === 0) throw new ConvertError("comicEmpty");
  pages.sort((a, b) => naturalCompare(a.name, b.name));
  const meta = comicInfo(info);
  return { pages, title: meta.title ?? fallbackTitle, author: meta.author, language: meta.language, rtl: meta.rtl };
}

function comicInfo(xml: Uint8Array | undefined): { title?: string; author?: string; language?: string; rtl: boolean } {
  if (!xml) return { rtl: false };
  const doc = parseMarkup(new TextDecoder("utf-8").decode(xml).replace(/^﻿/, ""), { xml: true });
  const root = elements(doc.children)[0];
  if (!root) return { rtl: false };
  const value = (local: string) => {
    const e = elements(root.children).find((c) => localName(c.name) === local);
    const v = e ? textContent(e).trim() : "";
    return v || undefined;
  };
  const series = value("Series");
  const number = value("Number");
  const title = value("Title") ?? (series === undefined ? undefined : number === undefined ? series : `${series} ${number}`);
  const language = value("LanguageISO");
  const rtl = (value("Manga") ?? "").toLowerCase() === "yesandrighttoleft" || isRtl(language);
  return { title, author: value("Writer"), language, rtl };
}

/** A CBZ of pages numbered so every reader keeps their order, with a ComicInfo.xml. */
export function writeCbz(pages: readonly { bytes: Uint8Array; ext: string }[], info: { title?: string; author?: string; language?: string; rtl?: boolean }): Uint8Array {
  const digits = Math.max(3, String(pages.length).length);
  const items: ZipItem[] = pages.map((p, i) => ({ name: `${String(i + 1).padStart(digits, "0")}.${p.ext}`, data: p.bytes, store: true }));
  let xml = '<?xml version="1.0" encoding="utf-8"?>\n<ComicInfo xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\n';
  if (info.title) xml += `  <Title>${escapeText(info.title)}</Title>\n`;
  if (info.author) xml += `  <Writer>${escapeText(info.author)}</Writer>\n`;
  xml += `  <PageCount>${pages.length}</PageCount>\n`;
  if (info.language) xml += `  <LanguageISO>${escapeText(info.language)}</LanguageISO>\n`;
  if (info.rtl) xml += "  <Manga>YesAndRightToLeft</Manga>\n";
  xml += "</ComicInfo>";
  items.push({ name: "ComicInfo.xml", data: xml });
  return writeZip(items);
}
