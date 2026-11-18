// Hand-built e-book fixtures (not a test file): EPUB, FB2, HTMLZ, TXTZ and CBZ written byte by byte with Node's zlib and fflate's plain
// zip writer, independently of the readers under test. Plus a strict XML well-formedness checker used to verify what the writers produce.
import { zipSync, unzipSync, type Zippable } from "fflate";
import { buildPng } from "./images-fixtures";

const enc = new TextEncoder();

export const AR_TITLE = "رحلة إلى البحر";
export const AR_AUTHOR = "سارة المصري";
export const AR_P1 = "كان صباحاً هادئاً على شاطئ بيروت.";
export const AR_P2 = "ثم بدأت الأمواج تعلو شيئاً فشيئاً.";
export const PICTURE = buildPng(3, 2, [10, 120, 200, 255]);

/** A zip whose "mimetype" entry comes first and is stored, like a real EPUB. */
export function zip(entries: Record<string, string | Uint8Array>, storeFirst = true): Uint8Array {
  const z: Zippable = {};
  for (const [name, data] of Object.entries(entries)) {
    const bytes = typeof data === "string" ? enc.encode(data) : data;
    z[name] = [bytes, { level: storeFirst && name === "mimetype" ? 0 : 6 }];
  }
  return zipSync(z);
}

export function unzip(bytes: Uint8Array): Record<string, Uint8Array> {
  return unzipSync(bytes);
}

export const text = (b: Uint8Array | undefined) => (b ? new TextDecoder().decode(b) : "");

/** An Arabic EPUB 3 in OPS/, with a navigation document, a skipped non-linear note, a picture, a script and unsafe attributes. */
export function arabicEpub(): Uint8Array {
  const xhtml = (title: string, body: string) =>
    `<?xml version="1.0" encoding="utf-8"?>\n<!DOCTYPE html>\n<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="ar" lang="ar" dir="rtl"><head><title>${title}</title><link rel="stylesheet" href="../css/s.css"/><style>p{color:red}</style></head><body>${body}</body></html>`;
  return zip({
    mimetype: "application/epub+zip",
    "META-INF/container.xml": `<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OPS/package.opf" media-type="application/oebps-package+xml"/></rootfiles></container>`,
    "OPS/package.opf": `<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">x</dc:identifier><dc:title>${AR_TITLE}</dc:title><dc:creator>${AR_AUTHOR}</dc:creator><dc:language>ar</dc:language></metadata>
  <manifest>
    <item id="nav" href="text/nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
    <item id="c1" href="text/one.xhtml" media-type="application/xhtml+xml"/>
    <item id="c2" href="text/two%20b.xhtml" media-type="application/xhtml+xml"/>
    <item id="note" href="text/note.xhtml" media-type="application/xhtml+xml"/>
    <item id="pic" href="images/sea.png" media-type="image/png"/>
    <item id="css" href="css/s.css" media-type="text/css"/>
  </manifest>
  <spine><itemref idref="nav"/><itemref idref="c1"/><itemref idref="c2"/><itemref idref="note" linear="no"/></spine>
</package>`,
    "OPS/text/nav.xhtml": xhtml("nav", `<nav epub:type="toc"><ol><li><a href="one.xhtml">1</a></li></ol></nav>`),
    "OPS/text/one.xhtml": xhtml(
      "one",
      `<h1>الفصل الأول</h1><p onclick="steal()" style="color:red" class="x">${AR_P1} <b>بقوة</b> &amp; <i>هدوء</i>&nbsp;!</p>` +
        `<script>alert(1)</script><p><img src="../images/sea.png" alt="البحر"/></p>` +
        `<p><a href="two%20b.xhtml#later">إلى الأمواج</a> <a href="javascript:alert(1)">رابط سيئ</a> <a href="https://example.org/">موقع</a></p>` +
        `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><image xlink:href="../images/sea.png"/></svg>` +
        `<form><input value="x"/></form><div/><p>بعد</p>`,
    ),
    "OPS/text/two b.xhtml": xhtml(
      "two",
      `<h1>الفصل الثاني</h1><p id="later">${AR_P2}</p><ul><li>أولاً</li><li>ثانياً<ul><li>فرعي</li></ul></li></ul>` +
        `<table><tr><th>اليوم</th><th>الموج</th></tr><tr><td>الإثنين</td><td>عالٍ</td></tr></table><p><a href="one.xhtml">العودة</a></p>`,
    ),
    "OPS/text/note.xhtml": xhtml("note", "<p>NOT LINEAR</p>"),
    "OPS/images/sea.png": PICTURE,
    "OPS/css/s.css": "p{}",
  });
}

/** Cyrillic to windows-1251 (А..я are 0xC0..0xFF), for an FB2 in its usual legacy encoding. */
export function cp1251(s: string): Uint8Array {
  return Uint8Array.from([...s].map((c) => {
    const code = c.charCodeAt(0);
    if (code >= 0x410 && code <= 0x44f) return code - 0x410 + 0xc0;
    if (code < 0x80) return code;
    throw new Error(`no cp1251 for ${c}`);
  }));
}

export function arabicFb2(): string {
  const b64 = Buffer.from(PICTURE).toString("base64").replace(/(.{40})/g, "$1\n");
  return `<?xml version="1.0" encoding="UTF-8"?>
<FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0" xmlns:l="http://www.w3.org/1999/xlink">
<description><title-info><genre>prose</genre><author><first-name>سارة</first-name><last-name>المصري</last-name></author><book-title>${AR_TITLE}</book-title><lang>ar</lang></title-info></description>
<body>
<title><p>${AR_TITLE}</p></title>
<epigraph><p>البحر لا ينام</p><text-author>مثل</text-author></epigraph>
<section id="s1"><title><p>الفصل الأول</p></title><p>${AR_P1}<a l:href="#n1" type="note">1</a></p><empty-line/><image l:href="#sea.png"/>
<poem><stanza><v>سطر أول</v><v>سطر ثان</v></stanza></poem>
<section><title><p>جزء</p></title><p><strong>قوي</strong> و<emphasis>مائل</emphasis></p></section>
</section>
<section id="s2"><title><p>الفصل الثاني</p></title><p>${AR_P2}</p><table><tr><th>أ</th><th>ب</th></tr><tr><td>1</td><td>2</td></tr></table></section>
</body>
<body name="notes"><section id="n1"><title><p>1</p></title><p>حاشية</p></section></body>
<binary id="sea.png" content-type="image/png">${b64}</binary>
</FictionBook>`;
}

// ---- strict XML well-formedness (independent of lib/markup) ------------------------------------------------------------------------

/** Throws when `xml` is not well-formed: unbalanced or mis-nested tags, a bare "&" or "<", duplicate attributes, junk after the root. */
export function assertWellFormed(xml: string): void {
  const s = xml.replace(/^﻿/, "");
  const stack: string[] = [];
  let roots = 0;
  const re = /<!--[\s\S]*?-->|<\?[\s\S]*?\?>|<!DOCTYPE[^>]*>|<!\[CDATA\[[\s\S]*?\]\]>|<\/([^\s>]+)\s*>|<([^\s/>!?]+)((?:\s+[^\s=/>]+\s*=\s*(?:"[^"<]*"|'[^'<]*'))*)\s*(\/?)>|([^<]+)|(<)/g;
  for (const m of s.matchAll(re)) {
    const [whole, close, open, attrs, selfClose, chars, bare] = m;
    if (bare) throw new Error(`bare "<" at ${m.index}: ${s.slice(m.index, m.index! + 40)}`);
    if (chars !== undefined) {
      if (stack.length === 0 && chars.trim()) throw new Error(`text outside the root: ${chars.slice(0, 40)}`);
      const amp = /&(?!(?:amp|lt|gt|quot|apos|#\d+|#x[0-9a-fA-F]+);)/.exec(chars);
      if (amp) throw new Error(`bare "&" in text: ${chars.slice(Math.max(0, amp.index - 20), amp.index + 20)}`);
      continue;
    }
    if (close) {
      const expected = stack.pop();
      if (expected !== close) throw new Error(`</${close}> closes <${expected}>`);
      continue;
    }
    if (open) {
      const names = [...(attrs ?? "").matchAll(/([^\s=]+)\s*=/g)].map((a) => a[1]);
      if (new Set(names).size !== names.length) throw new Error(`duplicate attribute in <${open}>`);
      for (const v of (attrs ?? "").matchAll(/=\s*(?:"([^"]*)"|'([^']*)')/g)) {
        const value = v[1] ?? v[2] ?? "";
        if (/&(?!(?:amp|lt|gt|quot|apos|#\d+|#x[0-9a-fA-F]+);)/.test(value)) throw new Error(`bare "&" in an attribute of <${open}>`);
      }
      if (stack.length === 0) roots++;
      if (!selfClose) stack.push(open);
      continue;
    }
    void whole;
  }
  if (stack.length) throw new Error(`unclosed <${stack.join("> <")}>`);
  if (roots !== 1) throw new Error(`${roots} root elements`);
}
