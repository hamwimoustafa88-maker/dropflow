// SPDX-License-Identifier: GPL-3.0-or-later
// Book bodies as Markdown and plain text, and Markdown back into a body (TXTZ). Like the Windows app, HTML is first read into simple
// blocks (headings, paragraphs, quotes, list items, tables, code, rules) and both text writers work from those
// (src/DropFlow.Engines.Pdf/Office/RichTextWriters.cs): plain text keeps list markers and tab-separated tables. Arabic stays in logical
// order: nothing here reorders characters. Pure functions.

import { attr, el, elements, escapeAttr, escapeText, serialize, text, textContent, type MElement, type MNode } from "./markup";
import { PICTURE_SCHEME, dataUri, escapeHtml, isRtl, pictureIndex, primaryLanguage, type Book } from "./book-model";

// ---- blocks --------------------------------------------------------------------------------------------------------------------------

export interface Run {
  text: string;
  bold?: boolean;
  italic?: boolean;
  strike?: boolean;
  code?: boolean;
  href?: string;
  image?: { alt: string; src: string };
  lineBreak?: boolean;
}

export type Block =
  | { k: "h"; level: number; runs: Run[] }
  | { k: "p"; runs: Run[]; quote: boolean }
  | { k: "li"; depth: number; ordered: boolean; number: number; runs: Run[] }
  | { k: "table"; rows: Run[][][] }
  | { k: "code"; text: string }
  | { k: "rule" };

const HEADINGS = new Set(["h1", "h2", "h3", "h4", "h5", "h6"]);
const BLOCKS = new Set([
  "p", "div", "section", "article", "main", "header", "footer", "aside", "nav", "figure", "figcaption", "blockquote", "ul", "ol", "table",
  "pre", "hr", "dl", "dt", "dd", "body", "html", "caption", "address", "center", "details", "summary", ...HEADINGS,
]);

interface Style {
  bold?: boolean;
  italic?: boolean;
  strike?: boolean;
  code?: boolean;
  href?: string;
}

function inlineRuns(nodes: readonly MNode[], style: Style, out: Run[]): void {
  for (const node of nodes) {
    if (node.type === "text") {
      if (node.text) out.push({ ...style, text: style.code ? node.text : node.text.replace(/\s+/g, " ") });
      continue;
    }
    switch (node.name) {
      case "br":
        out.push({ text: "", lineBreak: true });
        break;
      case "b":
      case "strong":
        inlineRuns(node.children, { ...style, bold: true }, out);
        break;
      case "i":
      case "em":
      case "cite":
      case "dfn":
        inlineRuns(node.children, { ...style, italic: true }, out);
        break;
      case "s":
      case "strike":
      case "del":
        inlineRuns(node.children, { ...style, strike: true }, out);
        break;
      case "code":
      case "tt":
      case "kbd":
      case "samp":
        inlineRuns(node.children, { ...style, code: true }, out);
        break;
      case "a": {
        const href = attr(node, "href");
        inlineRuns(node.children, href ? { ...style, href } : style, out);
        break;
      }
      case "img": {
        const alt = attr(node, "alt") ?? "";
        const src = attr(node, "src") ?? "";
        out.push({ text: alt, image: { alt, src } });
        break;
      }
      default:
        inlineRuns(node.children, style, out);
    }
  }
}

/** Trims a paragraph's runs (whitespace at the start and end, spaces around line breaks). */
function tidy(runs: Run[]): Run[] {
  const out = runs.filter((r) => r.text !== "" || r.lineBreak || r.image);
  for (let i = 0; i < out.length; i++) {
    const r = out[i];
    if (r.lineBreak || r.code) continue;
    const prev = out[i - 1];
    const next = out[i + 1];
    if (!prev || prev.lineBreak) r.text = r.text.replace(/^\s+/, "");
    if (!next || next.lineBreak) r.text = r.text.replace(/\s+$/, "");
    if (prev && !prev.lineBreak && /\s$/.test(prev.text) && !prev.code) r.text = r.text.replace(/^\s+/, "");
  }
  while (out.length && out[out.length - 1].lineBreak) out.pop();
  while (out.length && out[0].lineBreak) out.shift();
  return out.filter((r) => r.text !== "" || r.lineBreak || r.image);
}

const hasContent = (runs: Run[]) => runs.some((r) => r.image || (!r.lineBreak && r.text.trim() !== ""));

/** Reads HTML nodes into blocks. */
export function htmlToBlocks(nodes: readonly MNode[]): Block[] {
  const blocks: Block[] = [];
  walkBlocks(nodes, blocks, false);
  return blocks;
}

function walkBlocks(nodes: readonly MNode[], blocks: Block[], quote: boolean): void {
  let pending: Run[] = [];
  const flush = () => {
    const runs = tidy(pending);
    if (hasContent(runs)) blocks.push({ k: "p", runs, quote });
    pending = [];
  };
  for (const node of nodes) {
    if (node.type === "text" || !BLOCKS.has(node.name)) {
      if (node.type === "el" && node.name === "li") {
        flush();
        listItems([node], false, 0, blocks, { n: 1 });
        continue;
      }
      inlineRuns([node], {}, pending);
      continue;
    }
    flush();
    const e = node;
    if (HEADINGS.has(e.name)) {
      const runs: Run[] = [];
      inlineRuns(e.children, {}, runs);
      const t = tidy(runs);
      if (hasContent(t)) blocks.push({ k: "h", level: Number(e.name[1]), runs: t });
    } else if (e.name === "blockquote") {
      walkBlocks(e.children, blocks, true);
    } else if (e.name === "ul" || e.name === "ol") {
      const start = Number(attr(e, "start") ?? "1");
      listItems(e.children, e.name === "ol", 0, blocks, { n: Number.isFinite(start) ? start : 1 });
    } else if (e.name === "table") {
      const rows: Run[][][] = [];
      for (const tr of rowsOf(e)) {
        const cells = elements(tr.children).filter((c) => c.name === "td" || c.name === "th");
        if (cells.length === 0) continue;
        rows.push(cells.map((c) => {
          const runs: Run[] = [];
          inlineRuns(c.children, {}, runs);
          return tidy(runs.map((r) => (r.lineBreak ? { text: " " } : r)));
        }));
      }
      if (rows.length) blocks.push({ k: "table", rows });
    } else if (e.name === "pre") {
      blocks.push({ k: "code", text: textContent(e).replace(/\r\n?/g, "\n").replace(/\n+$/, "") });
    } else if (e.name === "hr") {
      blocks.push({ k: "rule" });
    } else {
      walkBlocks(e.children, blocks, quote);
    }
  }
  flush();
}

function rowsOf(table: MElement): MElement[] {
  const rows: MElement[] = [];
  const visit = (e: MElement) => {
    for (const c of elements(e.children)) {
      if (c.name === "tr") rows.push(c);
      else if (c.name === "thead" || c.name === "tbody" || c.name === "tfoot") visit(c);
    }
  };
  visit(table);
  return rows;
}

function listItems(nodes: readonly MNode[], ordered: boolean, depth: number, blocks: Block[], counter: { n: number }): void {
  for (const li of elements(nodes)) {
    if (li.name !== "li") {
      if (li.name === "ul" || li.name === "ol") listItems(li.children, li.name === "ol", depth + 1, blocks, { n: 1 });
      continue;
    }
    const own: MNode[] = [];
    const nested: MElement[] = [];
    for (const c of li.children) {
      if (c.type === "el" && (c.name === "ul" || c.name === "ol")) nested.push(c);
      else own.push(c);
    }
    const runs: Run[] = [];
    // Paragraphs inside an item are joined with a line break.
    own.forEach((c, i) => {
      if (c.type === "el" && BLOCKS.has(c.name) && i > 0 && runs.length) runs.push({ text: "", lineBreak: true });
      inlineRuns(c.type === "el" && BLOCKS.has(c.name) ? c.children : [c], {}, runs);
    });
    blocks.push({ k: "li", depth, ordered, number: counter.n++, runs: tidy(runs) });
    for (const list of nested) listItems(list.children, list.name === "ol", depth + 1, blocks, { n: Number(attr(list, "start") ?? "1") || 1 });
  }
}

const continuesList = (blocks: readonly Block[], i: number) => blocks[i].k === "li" && blocks[i - 1]?.k === "li";

// ---- Markdown out --------------------------------------------------------------------------------------------------------------------

/** Characters that would start Markdown syntax inside ordinary text. */
function escapeMd(s: string): string {
  return s.replace(/([\\`*_[\]<>|])/g, "\\$1");
}

function safeHref(href: string): string | null {
  const h = href.trim();
  if (!h || /\s/.test(h)) return h.startsWith("#") && !/\s/.test(h) ? h : null;
  if (h.startsWith(PICTURE_SCHEME) || /^data:/i.test(h)) return null;
  const colon = h.indexOf(":");
  if (colon > 0 && !h.slice(0, colon).includes("/")) {
    const scheme = h.slice(0, colon).toLowerCase();
    return ["http", "https", "mailto", "tel", "ftp"].includes(scheme) ? h : null;
  }
  return h; // relative links and in-page anchors
}

function inlineMd(runs: readonly Run[], lineBreak: string): string {
  let s = "";
  for (const r of runs) {
    if (r.lineBreak) {
      s += lineBreak;
      continue;
    }
    if (r.image) {
      const src = safeHref(r.image.src);
      s += src ? `![${escapeMd(r.image.alt)}](${src})` : escapeMd(r.image.alt);
      continue;
    }
    const lead = /^\s*/.exec(r.text)![0];
    const trail = r.text.length > lead.length ? /\s*$/.exec(r.text)![0] : "";
    let core = r.text.slice(lead.length, r.text.length - trail.length);
    if (!core) {
      s += r.text;
      continue;
    }
    if (r.code) {
      const fence = core.includes("`") ? "``" : "`";
      core = `${fence}${core.includes("`") ? " " + core + " " : core}${fence}`;
    } else {
      core = escapeMd(core);
      if (r.strike) core = `~~${core}~~`;
      if (r.italic) core = `*${core}*`;
      if (r.bold) core = `**${core}**`;
    }
    const href = r.href ? safeHref(r.href) : null;
    if (href) core = `[${core}](${href})`;
    s += lead + core + trail;
  }
  return s;
}

/** Escapes what would turn the start of a paragraph into another block ("# ", "- ", "1. ", "> "). */
function guardLineStart(s: string): string {
  return s.replace(/^(\s*)([#+-]|\d+[.)])(?=\s)/gm, "$1\\$2").replace(/^(\s*)>/gm, "$1\\>");
}

export function blocksToMarkdown(blocks: readonly Block[]): string {
  let out = "";
  blocks.forEach((b, i) => {
    if (i > 0) out += continuesList(blocks, i) ? "\n" : "\n\n";
    switch (b.k) {
      case "h":
        out += `${"#".repeat(Math.min(6, Math.max(1, b.level)))} ${inlineMd(b.runs, " ")}`;
        break;
      case "p": {
        const t = guardLineStart(inlineMd(b.runs, "  \n"));
        out += b.quote ? t.split("\n").map((l) => `> ${l}`).join("\n") : t;
        break;
      }
      case "li": {
        const indent = " ".repeat(b.depth * 4);
        out += `${indent}${b.ordered ? `${b.number}. ` : "- "}${inlineMd(b.runs, "  \n").replace(/\n/g, `\n${indent}   `)}`;
        break;
      }
      case "table": {
        const columns = Math.max(...b.rows.map((r) => r.length));
        const row = (cells: Run[][]) => `| ${Array.from({ length: columns }, (_, c) => inlineMd(cells[c] ?? [], " ")).join(" | ")} |`;
        out += [row(b.rows[0]), `| ${Array.from({ length: columns }, () => "---").join(" | ")} |`, ...b.rows.slice(1).map(row)].join("\n");
        break;
      }
      case "code": {
        const fence = b.text.includes("```") ? "~~~~" : "```";
        out += `${fence}\n${b.text}\n${fence}`;
        break;
      }
      case "rule":
        out += "---";
        break;
    }
  });
  return out.trimEnd() + "\n";
}

// ---- plain text out ------------------------------------------------------------------------------------------------------------------

const plainRuns = (runs: readonly Run[]) => runs.map((r) => (r.lineBreak ? "\n" : r.image ? r.image.alt : r.text)).join("");

export function blocksToText(blocks: readonly Block[]): string {
  let out = "";
  blocks.forEach((b, i) => {
    if (i > 0) out += continuesList(blocks, i) ? "\n" : "\n\n";
    switch (b.k) {
      case "h":
        out += plainRuns(b.runs).trim();
        break;
      case "p":
        out += plainRuns(b.runs);
        break;
      case "li": {
        const indent = " ".repeat(b.depth * 4);
        out += `${indent}${b.ordered ? `${b.number}. ` : "- "}${plainRuns(b.runs).replace(/\n/g, `\n${" ".repeat(b.depth * 4 + 3)}`)}`;
        break;
      }
      case "table":
        out += b.rows.map((r) => r.map((c) => plainRuns(c).replace(/\n/g, " ")).join("\t")).join("\n");
        break;
      case "code":
        out += b.text;
        break;
      case "rule":
        out += "----------";
        break;
    }
  });
  return out.trimEnd() + "\n";
}

// ---- Markdown in (TXTZ) ------------------------------------------------------------------------------------------------------------

/**
 * Markdown to HTML nodes: ATX and Setext headings, paragraphs, hard breaks, block quotes, nested lists, fenced and indented code, rules,
 * pipe tables; inline code, strong, emphasis, strikethrough, links, pictures and autolinks. Raw HTML is shown as text, never run.
 */
export function markdownToNodes(markdown: string): MNode[] {
  const lines = markdown.replace(/\r\n?/g, "\n").replace(/^\ufeff/, "").split("\n");
  return parseBlocks(lines);
}

const FENCE = /^\s{0,3}(`{3,}|~{3,})\s*([^`\s]*)?\s*$/;
const ATX = /^\s{0,3}(#{1,6})(?:\s+(.*?))?\s*#*\s*$/;
const RULE = /^\s{0,3}([-*_])(\s*\1){2,}\s*$/;
const LIST = /^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$/;
const TABLE_SEP = /^\s*\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)+\|?\s*$|^\s*\|\s*:?-{1,}:?\s*\|\s*$/;

/** Nesting guard: a hostile file with thousands of ">" or "[" must not overflow the stack; deeper levels stay text. */
const MAX_NESTING = 48;
let blockDepth = 0;
let inlineDepth = 0;

function parseBlocks(lines: string[]): MNode[] {
  if (blockDepth >= MAX_NESTING) return [el("p", [], [text(lines.join(" "))])];
  blockDepth++;
  try {
    return parseBlocksAt(lines);
  } finally {
    blockDepth--;
  }
}

function parseBlocksAt(lines: string[]): MNode[] {
  const out: MNode[] = [];
  let para: string[] = [];
  const flush = () => {
    if (para.length) out.push(el("p", [], inline(para.join("\n"))));
    para = [];
  };
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (!line.trim()) {
      flush();
      continue;
    }
    const fence = FENCE.exec(line);
    if (fence) {
      flush();
      const body: string[] = [];
      let j = i + 1;
      for (; j < lines.length; j++) {
        if (lines[j].trim().startsWith(fence[1][0].repeat(fence[1].length))) break;
        body.push(lines[j]);
      }
      out.push(el("pre", [], [el("code", [], [text(body.join("\n"))])]));
      i = j;
      continue;
    }
    if (/^( {4}|\t)/.test(line) && para.length === 0) {
      const body: string[] = [];
      let j = i;
      for (; j < lines.length && (/^( {4}|\t)/.test(lines[j]) || !lines[j].trim()); j++) body.push(lines[j].replace(/^( {4}|\t)/, ""));
      while (body.length && !body[body.length - 1].trim()) body.pop();
      out.push(el("pre", [], [el("code", [], [text(body.join("\n"))])]));
      i = j - 1;
      continue;
    }
    const atx = ATX.exec(line);
    if (atx) {
      flush();
      out.push(el(`h${atx[1].length}`, [], inline(atx[2] ?? "")));
      continue;
    }
    if (para.length && /^\s{0,3}(=+|-+)\s*$/.test(line)) {
      const level = line.trim()[0] === "=" ? 1 : 2;
      const content = para.join("\n");
      para = [];
      out.push(el(`h${level}`, [], inline(content)));
      continue;
    }
    if (RULE.test(line)) {
      flush();
      out.push(el("hr"));
      continue;
    }
    if (/^\s{0,3}>/.test(line)) {
      flush();
      const body: string[] = [];
      let j = i;
      for (; j < lines.length && /^\s{0,3}>/.test(lines[j]); j++) body.push(lines[j].replace(/^\s{0,3}> ?/, ""));
      out.push(el("blockquote", [], parseBlocks(body)));
      i = j - 1;
      continue;
    }
    const item = LIST.exec(line);
    if (item && (para.length === 0 || /^[-*+]$|^1[.)]$/.test(item[2]))) {
      flush();
      let j = i;
      const listLines: string[] = [];
      for (; j < lines.length; j++) {
        const l = lines[j];
        if (!l.trim()) {
          if (j + 1 < lines.length && (LIST.test(lines[j + 1]) || /^\s{2,}\S/.test(lines[j + 1]))) {
            listLines.push("");
            continue;
          }
          break;
        }
        if (j > i && !LIST.test(l) && !/^\s+/.test(l) && (ATX.test(l) || RULE.test(l) || /^\s{0,3}>/.test(l))) break;
        listLines.push(l);
      }
      out.push(...parseList(listLines));
      i = j - 1;
      continue;
    }
    if (line.includes("|") && i + 1 < lines.length && TABLE_SEP.test(lines[i + 1]) && para.length === 0) {
      const rows: string[][] = [splitRow(line)];
      let j = i + 2;
      for (; j < lines.length && lines[j].includes("|") && lines[j].trim(); j++) rows.push(splitRow(lines[j]));
      const head = el("thead", [], [el("tr", [], rows[0].map((c) => el("th", [], inline(c))))]);
      const body = el("tbody", [], rows.slice(1).map((r) => el("tr", [], r.map((c) => el("td", [], inline(c))))));
      out.push(el("table", [], rows.length > 1 ? [head, body] : [head]));
      i = j - 1;
      continue;
    }
    para.push(line);
  }
  flush();
  return out;
}

function splitRow(line: string): string[] {
  let s = line.trim();
  if (s.startsWith("|")) s = s.slice(1);
  if (s.endsWith("|") && !s.endsWith("\\|")) s = s.slice(0, -1);
  const cells: string[] = [];
  let cur = "";
  for (let i = 0; i < s.length; i++) {
    if (s[i] === "\\" && s[i + 1] === "|") {
      cur += "\\|";
      i++;
    } else if (s[i] === "|") {
      cells.push(cur.trim());
      cur = "";
    } else cur += s[i];
  }
  cells.push(cur.trim());
  return cells;
}

function parseList(lines: string[]): MNode[] {
  // Items at the smallest indentation; deeper lines belong to the item above them.
  const first = LIST.exec(lines[0])!;
  const baseIndent = first[1].length;
  const ordered = /\d/.test(first[2]);
  const items: { head: string; rest: string[] }[] = [];
  for (const line of lines) {
    const m = LIST.exec(line);
    if (m && m[1].length <= baseIndent + 1 && /\d/.test(m[2]) === ordered) items.push({ head: m[3], rest: [] });
    else if (items.length) items[items.length - 1].rest.push(line.replace(new RegExp(`^\\s{0,${baseIndent + 4}}`), ""));
  }
  const lis = items.map(({ head, rest }) => {
    const children: MNode[] = inline(head);
    const tail = rest.filter((l, i) => l.trim() || i < rest.length - 1);
    if (tail.length) {
      const nested = parseBlocks(tail);
      for (const n of nested) {
        if (n.type === "el" && n.name === "p") children.push(el("br"), ...n.children);
        else children.push(n);
      }
    }
    return el("li", [], children);
  });
  const start = ordered ? Number(/\d+/.exec(first[2])![0]) : 1;
  return [el(ordered ? "ol" : "ul", ordered && start !== 1 ? [["start", String(start)]] : [], lis)];
}

const ESCAPABLE = "\\`*_{}[]()#+-.!|<>~\"'";

/** Inline Markdown to nodes. */
export function inline(source: string): MNode[] {
  if (inlineDepth >= MAX_NESTING) return source ? [text(source)] : [];
  inlineDepth++;
  try {
    return inlineAt(source);
  } finally {
    inlineDepth--;
  }
}

function inlineAt(source: string): MNode[] {
  const out: MNode[] = [];
  let buf = "";
  const pushText = () => {
    if (buf) out.push(text(buf));
    buf = "";
  };
  const s = source;
  let i = 0;
  while (i < s.length) {
    const c = s[i];
    if (c === "\\" && i + 1 < s.length) {
      if (s[i + 1] === "\n") {
        pushText();
        out.push(el("br"));
        i += 2;
        continue;
      }
      if (ESCAPABLE.includes(s[i + 1])) {
        buf += s[i + 1];
        i += 2;
        continue;
      }
    }
    if (c === "\n") {
      if (/ {2,}$/.test(buf)) {
        buf = buf.replace(/ +$/, "");
        pushText();
        out.push(el("br"));
      } else buf += "\n";
      i++;
      continue;
    }
    if (c === "`") {
      const run = /^`+/.exec(s.slice(i))![0];
      const end = s.indexOf(run, i + run.length);
      if (end > 0) {
        pushText();
        let code = s.slice(i + run.length, end).replace(/\n/g, " ");
        if (/^ .* $/.test(code)) code = code.slice(1, -1);
        out.push(el("code", [], [text(code)]));
        i = end + run.length;
        continue;
      }
      buf += run;
      i += run.length;
      continue;
    }
    if (c === "!" && s[i + 1] === "[") {
      const link = readLink(s, i + 1);
      if (link) {
        pushText();
        out.push(el("img", [["src", link.href], ["alt", plainInline(link.label)]]));
        i = link.end;
        continue;
      }
    }
    if (c === "[") {
      const link = readLink(s, i);
      if (link) {
        pushText();
        out.push(el("a", [["href", link.href]], inline(link.label)));
        i = link.end;
        continue;
      }
    }
    if (c === "<") {
      const auto = /^<((?:https?|mailto|ftp):[^\s<>]+)>/i.exec(s.slice(i));
      if (auto) {
        pushText();
        out.push(el("a", [["href", auto[1]]], [text(auto[1])]));
        i += auto[0].length;
        continue;
      }
    }
    if (c === "~" && s[i + 1] === "~") {
      const end = s.indexOf("~~", i + 2);
      if (end > i + 2) {
        pushText();
        out.push(el("del", [], inline(s.slice(i + 2, end))));
        i = end + 2;
        continue;
      }
    }
    if ((c === "*" || c === "_") && !(c === "_" && /\w/.test(s[i - 1] ?? ""))) {
      const double = s[i + 1] === c;
      const marker = double ? c + c : c;
      const startContent = i + marker.length;
      if (s[startContent] && !/\s/.test(s[startContent])) {
        const end = findCloser(s, startContent, marker);
        if (end > startContent) {
          pushText();
          out.push(el(double ? "strong" : "em", [], inline(s.slice(startContent, end))));
          i = end + marker.length;
          continue;
        }
      }
    }
    buf += c;
    i++;
  }
  pushText();
  return out;
}

function findCloser(s: string, from: number, marker: string): number {
  for (let j = from; j < s.length; j++) {
    if (s[j] === "\\") {
      j++;
      continue;
    }
    if (s[j] === "`") {
      const run = /^`+/.exec(s.slice(j))![0];
      const end = s.indexOf(run, j + run.length);
      if (end > 0) j = end + run.length - 1;
      continue;
    }
    if (s.startsWith(marker, j) && !/\s/.test(s[j - 1]) && (marker.length === 2 || s[j + 1] !== marker[0]) && (marker[0] !== "_" || !/\w/.test(s[j + marker.length] ?? ""))) return j;
    if (marker.length === 1 && s.startsWith(marker + marker, j)) j++;
  }
  return -1;
}

function readLink(s: string, open: number): { label: string; href: string; end: number } | null {
  let depth = 0;
  let j = open;
  for (; j < s.length; j++) {
    if (s[j] === "\\") {
      j++;
      continue;
    }
    if (s[j] === "[") depth++;
    else if (s[j] === "]") {
      depth--;
      if (depth === 0) break;
    }
  }
  if (j >= s.length || s[j + 1] !== "(") return null;
  const m = /^\(\s*(<[^>]*>|[^\s()]*(?:\([^\s()]*\)[^\s()]*)*)(?:\s+("[^"]*"|'[^']*'))?\s*\)/.exec(s.slice(j + 1));
  if (!m) return null;
  const href = m[1].startsWith("<") ? m[1].slice(1, -1) : m[1];
  return { label: s.slice(open + 1, j), href: href.replace(/\\(.)/g, "$1"), end: j + 1 + m[0].length };
}

function plainInline(s: string): string {
  return textContent(inline(s));
}

// ---- HTML page ----------------------------------------------------------------------------------------------------------------------

/** The body as HTML with pictures inlined as data: URIs (a single self-contained file). */
export function bodyHtmlInline(book: Book): string {
  return serialize(inlinePictures(book.body, book));
}

function inlinePictures(nodes: readonly MNode[], book: Book): MNode[] {
  const cache = new Map<number, string>();
  const walk = (list: readonly MNode[]): MNode[] =>
    list.flatMap((n): MNode[] => {
      if (n.type === "text") return [n];
      if (n.name === "img") {
        const index = pictureIndex(attr(n, "src"));
        const picture = index >= 0 ? book.pictures[index] : undefined;
        if (!picture) {
          const alt = attr(n, "alt");
          return alt ? [text(alt)] : [];
        }
        if (!cache.has(index)) cache.set(index, dataUri(picture));
        return [{ ...n, attrs: n.attrs.map(([k, v]) => [k, k === "src" ? cache.get(index)! : v] as [string, string]) }];
      }
      return [{ ...n, children: walk(n.children) }];
    });
  return walk(nodes);
}

/** A complete HTML page around the book body (EpubReader.ToHtmlDocument on Windows). */
export function htmlDocument(book: Book): string {
  const language = primaryLanguage(book.language);
  // An Arabic (or Hebrew, Persian, Urdu...) book reads right to left as a whole; other books let each paragraph decide.
  const direction = isRtl(language) ? "rtl" : "auto";
  let s = `<!DOCTYPE html>\n<html lang="${escapeAttr(language)}" dir="${direction}">\n<head>\n<meta charset="utf-8">\n`;
  s += `<meta name="viewport" content="width=device-width, initial-scale=1">\n`;
  if (book.author) s += `<meta name="author" content="${escapeHtml(book.author)}">\n`;
  s += `<title>${escapeText(book.title)}</title>\n<style>\n`;
  s += "body{font-family:Georgia,'Times New Roman',serif;line-height:1.6;max-width:46rem;margin:2rem auto;padding:0 1rem;color:#1a1a1a}\n";
  s += "p,li,h1,h2,h3,h4,h5,h6,blockquote,td,th{unicode-bidi:plaintext;text-align:start}img{max-width:100%;height:auto}\n";
  s += "table{border-collapse:collapse}td,th{border:1px solid #999;padding:.2rem .5rem}section{margin-bottom:2rem}\n";
  s += `</style>\n</head>\n<body>\n${bodyHtmlInline(book)}</body>\n</html>\n`;
  return s;
}

/** Drops every picture (keeps its alt text): what the Markdown and plain-text writers get. */
export function withoutPictures(nodes: readonly MNode[]): MNode[] {
  return nodes.flatMap((n): MNode[] => {
    if (n.type === "text") return [n];
    if (n.name === "img") {
      const alt = attr(n, "alt");
      return alt && alt.trim() ? [text(alt)] : [];
    }
    return [{ ...n, children: withoutPictures(n.children) }];
  });
}
