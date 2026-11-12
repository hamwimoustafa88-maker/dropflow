// Turns pdf.js text items into lines, plain text and Markdown. Pure and Node-compatible.
// pdf.js already returns each run in LOGICAL order (it applies bidi per run), so Arabic text is never reversed here;
// we only decide which run comes first on a line (right-to-left lines read from the right edge).
// Arabic presentation forms (what Chrome/Edge-printed PDFs put in their ToUnicode maps) are turned back into base letters.

export interface TextItemLike {
  str: string;
  /** "ltr" | "rtl" | "ttb" (pdf.js). */
  dir?: string;
  /** PDF text matrix [a, b, c, d, e, f]: e/f are x/y of the baseline start; hypot(a, b) is the font size. */
  transform: number[];
  /** Advance width of the run (PDF user space). */
  width: number;
  height: number;
  hasEOL?: boolean;
}

export interface TextLine {
  text: string;
  /** Baseline y of the line (PDF space: larger = higher on the page). */
  y: number;
  /** Left edge for LTR lines, right edge for RTL lines. */
  x: number;
  /** Dominant font size of the line. */
  size: number;
  rtl: boolean;
}

const RTL_CHAR = /[֐-ࣿיִ-﷿ﹰ-﻿]/g;
const LTR_CHAR = /[A-Za-zÀ-ɏͰ-ϿЀ-ӿ]/g;

const PRESENTATION = /[ﭐ-﷿ﹰ-﻿]/g;
/** Persian/Urdu-only letters (پ چ ژ گ ٹ ڈ ڑ ں ے ۓ): a document with any of them keeps ی ک ھ ہ as they are. */
const PERSIAN_URDU = /[پچژگٹڈڑںےۓ]/;
/** Letters Arabic uses and Persian/Urdu text (almost) never does (ة ى ي ك). */
const ARABIC_ONLY = /[ةىيك]/;
/** Code points Chromium/Skia give to Arial glyphs that Arabic shares with Persian/Urdu letters: ی ک ھ ہ -> ي ك ه ه. */
const SHARED = /[یکھہ]/g;
const SHARED_TO_ARABIC: Record<string, string> = { "ی": "ي", "ک": "ك", "ھ": "ه", "ہ": "ه" };

/** Presentation forms (ﻣ ﺑ ﻷ ...) -> base letters (compatibility decomposition of those characters only). */
export function basePresentationForms(text: string): string {
  return text.replace(PRESENTATION, (c) => c.normalize("NFKC"));
}

/**
 * Document-level fix for PDFs whose ToUnicode maps shaped Arabic glyphs to the Persian/Urdu code points that share the
 * glyph (initial yeh -> ی U+06CC, heh -> ھ U+06BE...). Applied only when the text is clearly Arabic: it uses Arabic-only
 * letters and no Persian/Urdu-only letter, so Persian and Urdu text are never changed.
 */
export function arabicSharedLetters(pages: ReadonlyArray<ReadonlyArray<TextLine>>): TextLine[][] {
  const all = pages.map((p) => p.map((l) => l.text).join("\n")).join("\n");
  if (PERSIAN_URDU.test(all) || !ARABIC_ONLY.test(all)) return pages.map((p) => [...p]);
  return pages.map((p) => p.map((l) => ({ ...l, text: l.text.replace(SHARED, (c) => SHARED_TO_ARABIC[c]) })));
}

function isWordSpace(s: string): boolean {
  return /^\s+$/.test(s);
}

function itemSize(it: TextItemLike): number {
  const s = Math.hypot(it.transform[0] ?? 0, it.transform[1] ?? 0);
  return s > 0 ? s : Math.abs(it.height) || 0;
}

function lineIsRtl(items: TextItemLike[]): boolean {
  let rtl = 0;
  let ltr = 0;
  for (const it of items) {
    const r = (it.str.match(RTL_CHAR) ?? []).length;
    const l = (it.str.match(LTR_CHAR) ?? []).length;
    rtl += r;
    ltr += l;
  }
  if (rtl === 0 && ltr === 0) return items.some((i) => i.dir === "rtl");
  return rtl > ltr;
}

function joinRun(items: TextItemLike[], rtl: boolean): string {
  const ordered = [...items].sort((a, b) => (rtl ? b.transform[4] - a.transform[4] : a.transform[4] - b.transform[4]));
  let out = "";
  let prev: TextItemLike | null = null;
  for (const it of ordered) {
    if (it.str === "") continue;
    if (prev) {
      const size = Math.max(itemSize(prev), itemSize(it), 1);
      const gap = rtl ? prev.transform[4] - (it.transform[4] + it.width) : it.transform[4] - (prev.transform[4] + prev.width);
      const needsSpace = gap > size * 0.15 && !/\s$/.test(out) && !/^\s/.test(it.str);
      if (needsSpace) out += " ";
    }
    out += it.str;
    prev = it;
  }
  return out.replace(/[ \t]{2,}/g, " ").trim();
}

/** Groups items into lines (top of the page first). Items may arrive in any order; marked-content entries (no `str`) are ignored. */
export function itemsToLines(items: ReadonlyArray<Partial<TextItemLike> & { transform?: number[] }>): TextLine[] {
  const usable = items.filter((i): i is TextItemLike => typeof i.str === "string" && Array.isArray(i.transform) && i.transform.length >= 6);
  const sorted = [...usable].sort((a, b) => b.transform[5] - a.transform[5] || a.transform[4] - b.transform[4]);

  const groups: TextItemLike[][] = [];
  let currentY = 0;
  let current: TextItemLike[] | null = null;
  for (const it of sorted) {
    const size = itemSize(it);
    const tol = Math.max(1.5, size * 0.4);
    if (current && Math.abs(it.transform[5] - currentY) <= tol) {
      current.push(it);
    } else {
      current = [it];
      currentY = it.transform[5];
      groups.push(current);
    }
  }

  const lines: TextLine[] = [];
  for (const g of groups) {
    const rtl = lineIsRtl(g);
    const text = basePresentationForms(joinRun(g, rtl));
    if (text.length === 0) continue;
    const meaningful = g.filter((i) => !isWordSpace(i.str));
    const size = Math.max(...(meaningful.length ? meaningful : g).map(itemSize));
    const x = rtl ? Math.max(...g.map((i) => i.transform[4] + i.width)) : Math.min(...g.map((i) => i.transform[4]));
    lines.push({ text, y: g[0].transform[5], x, size, rtl });
  }
  return lines;
}

function median(values: number[]): number {
  if (values.length === 0) return 0;
  const s = [...values].sort((a, b) => a - b);
  const m = Math.floor(s.length / 2);
  return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2;
}

const PARAGRAPH_GAP = 1.6;

/** Lines of one page grouped into paragraphs (blank-line separated) using vertical gaps relative to the median size. */
function paragraphs(lines: TextLine[]): TextLine[][] {
  const med = median(lines.map((l) => l.size)) || 10;
  const out: TextLine[][] = [];
  let cur: TextLine[] = [];
  let prev: TextLine | null = null;
  for (const l of lines) {
    if (prev && prev.y - l.y > med * PARAGRAPH_GAP) {
      out.push(cur);
      cur = [];
    }
    cur.push(l);
    prev = l;
  }
  if (cur.length) out.push(cur);
  return out;
}

/** Plain text: line breaks kept, a blank line between paragraphs and between pages. */
export function linesToText(pages: ReadonlyArray<ReadonlyArray<TextLine>>): string {
  const blocks: string[] = [];
  for (const lines of arabicSharedLetters(pages)) {
    for (const p of paragraphs([...lines])) blocks.push(p.map((l) => l.text).join("\n"));
  }
  return blocks.join("\n\n") + (blocks.length ? "\n" : "");
}

const BULLET = /^([•●▪◦·∙‣⁃■□▫\-–—*])\s+/;
const NUMBERED = /^([0-9٠-٩۰-۹]{1,3})[.)]\s+/;
const ARABIC_DIGITS = "٠١٢٣٤٥٦٧٨٩";
const PERSIAN_DIGITS = "۰۱۲۳۴۵۶۷۸۹";

function asciiDigits(s: string): string {
  let out = "";
  for (const ch of s) {
    const a = ARABIC_DIGITS.indexOf(ch);
    const b = PERSIAN_DIGITS.indexOf(ch);
    out += a >= 0 ? String(a) : b >= 0 ? String(b) : ch;
  }
  return out;
}

function escapeLead(text: string): string {
  return /^[#>]/.test(text) ? `\\${text}` : text;
}

function joinWrapped(a: string, b: string): string {
  if (/[A-Za-z]-$/.test(a) && /^[a-z]/.test(b)) return a.slice(0, -1) + b;
  return `${a} ${b}`;
}

/**
 * Markdown heuristics: headings from the font size relative to the page median (>=1.4x "#", >=1.15x "##"),
 * paragraphs from vertical gaps, bullet and numbered lists. Pages are separated by a blank line only.
 */
export function linesToMarkdown(pages: ReadonlyArray<ReadonlyArray<TextLine>>): string {
  const blocks: string[] = [];
  for (const lines of arabicSharedLetters(pages)) {
    if (lines.length === 0) continue;
    const med = median(lines.map((l) => l.size)) || 10;
    for (const para of paragraphs([...lines])) {
      let text: string[] = []; // pending paragraph lines
      const flush = () => {
        if (text.length) blocks.push(escapeLead(text.reduce(joinWrapped)));
        text = [];
      };
      let list: string[] | null = null;
      const flushList = () => {
        if (list && list.length) blocks.push(list.join("\n"));
        list = null;
      };
      for (const l of para) {
        const ratio = l.size / med;
        const isHeading = l.text.length <= 140 && !BULLET.test(l.text) && !NUMBERED.test(l.text) && ratio >= 1.15;
        if (isHeading) {
          flush();
          flushList();
          blocks.push(`${ratio >= 1.4 ? "#" : "##"} ${l.text}`);
          continue;
        }
        const bullet = BULLET.exec(l.text);
        const numbered = bullet ? null : NUMBERED.exec(l.text);
        if (bullet || numbered) {
          flush();
          if (!list) list = [];
          list.push(bullet ? `- ${l.text.slice(bullet[0].length)}` : `${asciiDigits(numbered![1])}. ${l.text.slice(numbered![0].length)}`);
          continue;
        }
        if (list && list.length) {
          // continuation of the previous list item
          list[list.length - 1] = joinWrapped(list[list.length - 1], l.text);
          continue;
        }
        text.push(l.text);
      }
      flush();
      flushList();
    }
  }
  return blocks.join("\n\n") + (blocks.length ? "\n" : "");
}

/** True when no page produced any non-blank text (a scanned PDF). */
export function hasNoText(pages: ReadonlyArray<ReadonlyArray<TextLine>>): boolean {
  return pages.every((lines) => lines.every((l) => l.text.trim().length === 0));
}
