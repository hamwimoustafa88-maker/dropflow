// SPDX-License-Identifier: GPL-3.0-or-later
// Readers for SAMI (.smi), LRC lyrics, TTML/DFXP, MicroDVD (.sub; SubViewer 2 .sub files go through the SBV reader) and YouTube SBV,
// plus LRC and SBV writers. Pure functions, no DOM (they run in a Web Worker, where DOMParser does not exist). The rules match the
// desktop SubtitleParser.Formats.cs so both apps read a file the same way.

import { cleanCueText, decodeEntities, detectFormat, normalizeNewlines, parseSubtitles, type Cue, type ParseResult } from "./subtitles";

/** Every format the web app reads. */
export type AnySubtitleFormat = "srt" | "vtt" | "ass" | "smi" | "lrc" | "ttml" | "sub" | "sbv";

/** How long a last line that never says when it ends stays on screen (SAMI, LRC, MicroDVD with an empty end). */
export const LAST_CUE_MS = 3000;

/** The frame rate assumed for MicroDVD files that do not declare one. */
export const DEFAULT_MICRODVD_FPS = 23.976;

const KEEP = /<\/?[biu]>/gi;

function tidy(text: string): string {
  return text
    .split("\n")
    .map((l) => l.trim())
    .filter((l) => l !== "")
    .join("\n");
}

function sortCues(cues: Cue[]): Cue[] {
  return cues.map((c, i) => ({ c, i })).sort((a, b) => a.c.startMs - b.c.startMs || a.i - b.i).map((x) => x.c);
}

/** Ends each timed text at the next later time in `events` (or after LAST_CUE_MS); empty texts only end the one before. */
function fromEvents(events: { ms: number; text: string; endMs?: number | null }[]): Cue[] {
  const ordered = events.map((e, i) => ({ e, i })).sort((a, b) => a.e.ms - b.e.ms || a.i - b.i).map((x) => x.e);
  const cues: Cue[] = [];
  ordered.forEach((e, i) => {
    if (!e.text) return;
    const next = ordered.slice(i + 1).find((x) => x.ms > e.ms);
    const end = e.endMs != null && e.endMs > e.ms ? e.endMs : next ? next.ms : e.ms + LAST_CUE_MS;
    cues.push({ startMs: e.ms, endMs: end, text: e.text });
  });
  return cues;
}

// ---------------------------------------------------------------------------------------------------------------------
// SAMI

/** SAMI: `<SYNC Start=ms><P Class=X>text`; source line breaks are spaces, `<br>` is a break; the first language (class) is used. */
export function parseSami(input: string): ParseResult & { languages: string[] } {
  let text = normalizeNewlines(input).replace(/<!--[\s\S]*?-->/g, "");
  const body = text.search(/<body/i);
  if (body >= 0) text = text.slice(body);
  const syncRe = /<sync\b[^>]*?\bstart\s*=\s*["']?(\d+)["']?(?:[^>]*?\bend\s*=\s*["']?(\d+)["']?)?[^>]*>/gi;
  const syncs = [...text.matchAll(syncRe)];
  const events: { cls: string; ms: number; endMs: number | null; text: string }[] = [];
  syncs.forEach((m, i) => {
    const start = Number(m[1]);
    const endMs = m[2] ? Number(m[2]) : null;
    const from = (m.index ?? 0) + m[0].length;
    const to = i + 1 < syncs.length ? (syncs[i + 1].index ?? text.length) : text.length;
    const content = text.slice(from, to).replace(/<\/?(?:body|sami|html)\b[^>]*>/gi, "");
    const ps = [...content.matchAll(/<p\b(?:[^>]*?\bclass\s*=\s*["']?([A-Za-z0-9_-]+)["']?)?[^>]*>/gi)];
    if (ps.length === 0) {
      events.push({ cls: "", ms: start, endMs, text: samiText(content) });
      return;
    }
    ps.forEach((p, k) => {
      const a = (p.index ?? 0) + p[0].length;
      const b = k + 1 < ps.length ? (ps[k + 1].index ?? content.length) : content.length;
      events.push({ cls: (p[1] ?? "").toLowerCase(), ms: start, endMs, text: samiText(content.slice(a, b)) });
    });
  });
  const languages = [...new Set(events.filter((e) => e.text).map((e) => e.cls))];
  if (languages.length === 0) return { cues: [], skipped: 0, languages };
  const chosen = languages[0];
  const cues = fromEvents(events.filter((e) => e.cls === chosen));
  return { cues, skipped: 0, languages };
}

function samiText(html: string): string {
  return tidy(cleanCueText(html.replace(/\s+/g, " "), true).replace(/ /g, " "));
}

// ---------------------------------------------------------------------------------------------------------------------
// LRC

const LRC_TAGS = /^(?:\[(\d+:\d{1,2}(?:[.:]\d{1,3})?)\]\s*)+/;

function lrcTime(tag: string): number | null {
  const m = /^(\d+):(\d{1,2})(?:[.:](\d{1,3}))?$/.exec(tag);
  if (!m || Number(m[2]) > 59) return null;
  const frac = m[3] ? Number(m[3].padEnd(3, "0").slice(0, 3)) : 0;
  return (Number(m[1]) * 60 + Number(m[2])) * 1000 + frac;
}

/** LRC: `[mm:ss.xx]line`, several tags per line, `[offset:±ms]` (positive = earlier), `<mm:ss.xx>` word times removed. */
export function parseLrc(input: string): ParseResult & { title?: string } {
  const events: { ms: number; text: string }[] = [];
  let offset = 0;
  let title: string | undefined;
  for (const raw of normalizeNewlines(input).split("\n")) {
    const line = raw.trim();
    if (!line) continue;
    const tags = LRC_TAGS.exec(line);
    if (tags) {
      const body = tidy(cleanCueText(line.slice(tags[0].length), false));
      for (const t of tags[0].matchAll(/\[([^\]]+)\]/g)) {
        const ms = lrcTime(t[1]);
        if (ms !== null) events.push({ ms, text: body });
      }
      continue;
    }
    const meta = /^\[([A-Za-z#]+)\s*:([^\]]*)\]$/.exec(line);
    if (!meta) continue;
    const key = meta[1].toLowerCase();
    const value = meta[2].trim();
    if (key === "offset" && /^[+-]?\d+$/.test(value)) offset = Number(value);
    else if (key === "ti" && value) title = value;
  }
  const shift = (ms: number) => Math.max(0, ms - offset);
  const cues = fromEvents(events).map((c) => ({ ...c, startMs: shift(c.startMs), endMs: shift(c.endMs) }));
  return { cues, skipped: 0, title };
}

function lrcClock(ms: number): string {
  const cs = Math.max(0, Math.round(ms / 10));
  const p2 = (n: number) => String(n).padStart(2, "0");
  return `${p2(Math.floor(cs / 6000))}:${p2(Math.floor(cs / 100) % 60)}.${p2(cs % 100)}`;
}

/** LRC: one `[mm:ss.xx]` line per cue (lines joined by a space, no tags) and an empty tag where a gap follows. */
export function cuesToLrc(cues: readonly Cue[], title?: string): string {
  let out = title ? `[ti:${title.replace(/[\r\n\]]/g, " ")}]\n` : "";
  const list = cues.filter((c) => c.text.replace(KEEP, "").trim() !== "");
  list.forEach((c, i) => {
    out += `[${lrcClock(c.startMs)}]${c.text.replace(KEEP, "").replace(/\s*\n\s*/g, " ").trim()}\n`;
    const next = list[i + 1];
    if (!next || (lrcClock(next.startMs) !== lrcClock(c.endMs) && next.startMs > c.endMs)) out += `[${lrcClock(c.endMs)}]\n`;
  });
  return out;
}

// ---------------------------------------------------------------------------------------------------------------------
// SBV (and SubViewer 2)

const SBV_TIMING = /^\s*(\d+):(\d{1,2}):(\d{1,2})[.,](\d{1,3})\s*,\s*(\d+):(\d{1,2}):(\d{1,2})[.,](\d{1,3})\s*$/;

function sbvMs(h: string, m: string, s: string, f: string): number {
  return ((Number(h) * 60 + Number(m)) * 60 + Number(s)) * 1000 + Number(f.padEnd(3, "0"));
}

/** SBV: `H:MM:SS.mmm,H:MM:SS.mmm`, text lines, blank line. SubViewer 2 (centiseconds, `[br]`) is read too. */
export function parseSbv(input: string): ParseResult {
  const lines = normalizeNewlines(input).split("\n");
  const cues: Cue[] = [];
  for (let i = 0; i < lines.length; i++) {
    const m = SBV_TIMING.exec(lines[i]);
    if (!m) continue;
    const body: string[] = [];
    let k = i + 1;
    for (; k < lines.length && lines[k].trim() !== "" && !SBV_TIMING.test(lines[k]); k++) body.push(lines[k]);
    i = k - 1;
    const start = sbvMs(m[1], m[2], m[3], m[4]);
    const end = sbvMs(m[5], m[6], m[7], m[8]);
    const text = tidy(cleanCueText(body.join("\n").replace(/\[br\]/gi, "\n"), false));
    if (text) cues.push({ startMs: start, endMs: Math.max(start, end), text });
  }
  return { cues, skipped: 0 };
}

function sbvClock(ms: number): string {
  const t = Math.max(0, Math.round(ms));
  const p2 = (n: number) => String(n).padStart(2, "0");
  return `${Math.floor(t / 3_600_000)}:${p2(Math.floor(t / 60_000) % 60)}:${p2(Math.floor(t / 1000) % 60)}.${String(t % 1000).padStart(3, "0")}`;
}

/** YouTube SBV: plain text, LF line endings, a blank line after every cue. */
export function cuesToSbv(cues: readonly Cue[]): string {
  let out = "";
  for (const c of cues) {
    const text = tidy(c.text.replace(KEEP, ""));
    if (text) out += `${sbvClock(c.startMs)},${sbvClock(c.endMs)}\n${text}\n\n`;
  }
  return out;
}

// ---------------------------------------------------------------------------------------------------------------------
// MicroDVD

/** MicroDVD: `{start}{end}text` in frames, `|` between lines, `{1}{1}25` declares the rate; `{y:i}` / `{Y:b}` / leading `/` styles. */
export function parseMicroDvd(input: string): ParseResult & { fps: number; declared: boolean } {
  const raw: { start: number; end: number | null; text: string }[] = [];
  for (const line of normalizeNewlines(input).split("\n")) {
    const m = /^\{(\d+)\}\{(\d*)\}(.*)$/.exec(line.trim());
    if (m) raw.push({ start: Number(m[1]), end: m[2] ? Number(m[2]) : null, text: m[3] });
  }
  let fps = DEFAULT_MICRODVD_FPS;
  let declared = false;
  if (raw.length > 0 && raw[0].start <= 1 && (raw[0].end === null || raw[0].end <= 1)) {
    const rate = Number(raw[0].text.trim().replace(",", "."));
    if (Number.isFinite(rate) && rate > 1 && rate < 200 && /^\s*\d+(?:[.,]\d+)?\s*$/.test(raw[0].text)) {
      fps = rate;
      declared = true;
      raw.shift();
    }
  }
  const at = (frame: number) => Math.round((frame * 1000) / fps);
  const cues: Cue[] = [];
  raw.forEach((r, i) => {
    const text = tidy(microDvdText(r.text));
    if (!text) return;
    const start = at(r.start);
    const next = raw[i + 1];
    const end = r.end !== null ? at(r.end) : next && next.start > r.start ? at(next.start) : start + LAST_CUE_MS;
    cues.push({ startMs: start, endMs: Math.max(start, end), text });
  });
  return { cues: sortCues(cues), skipped: 0, fps, declared };
}

const MICRODVD_CODE = /\{([A-Za-z]):([^}]*)\}/g;

function microDvdText(body: string): string {
  const styles = (value: string, into: Set<string>) => {
    for (const v of value.split(",")) {
      const s = v.trim().toLowerCase();
      if (s === "b" || s === "i" || s === "u") into.add(s);
    }
  };
  const all = new Set<string>();
  for (const m of body.matchAll(MICRODVD_CODE)) if (m[1] === "Y") styles(m[2], all);
  const lines: string[] = [];
  for (const part of body.split("|")) {
    const mine = new Set(all);
    for (const m of part.matchAll(MICRODVD_CODE)) if (m[1] === "y") styles(m[2], mine);
    let line = part.replace(MICRODVD_CODE, "").trim();
    if (line.startsWith("/")) {
      mine.add("i");
      line = line.slice(1).trim();
    }
    line = cleanCueText(line, false);
    if (!line) continue;
    const open = ["b", "i", "u"].filter((s) => mine.has(s));
    lines.push(open.map((s) => `<${s}>`).join("") + line + [...open].reverse().map((s) => `</${s}>`).join(""));
  }
  return lines.join("\n");
}

// ---------------------------------------------------------------------------------------------------------------------
// TTML / DFXP

interface TtmlRates {
  frameRate: number;
  subFrameRate: number;
  tickRate: number;
}

/** A TTML time expression in milliseconds, or null. Clock (`01:02:03.5`, `00:00:07:12` frames) or offset (`1.5s`, `50f`, `40000000t`). */
export function ttmlTime(expression: string | undefined, rates: TtmlRates): number | null {
  if (!expression) return null;
  const s = expression.trim();
  const clock = /^(\d+):(\d{2}):(\d{2})(?:[.,](\d+)|:(\d+)(?:\.(\d+))?)?$/.exec(s);
  if (clock) {
    let seconds = (Number(clock[1]) * 60 + Number(clock[2])) * 60 + Number(clock[3]);
    if (clock[4]) seconds += Number(`0.${clock[4]}`);
    if (clock[5]) seconds += (Number(clock[5]) + (clock[6] ? Number(clock[6]) / rates.subFrameRate : 0)) / rates.frameRate;
    return Math.round(seconds * 1000);
  }
  const offset = /^(\d+(?:\.\d+)?)(h|ms|m|s|f|t)$/.exec(s);
  if (!offset) return null;
  const v = Number(offset[1]);
  const seconds = { h: v * 3600, m: v * 60, s: v, ms: v / 1000, f: v / rates.frameRate, t: v / rates.tickRate }[offset[2] as "h"];
  return Math.round(seconds * 1000);
}

function attributes(source: string): Record<string, string> {
  const out: Record<string, string> = {};
  for (const m of source.matchAll(/([\w:.-]+)\s*=\s*(?:"([^"]*)"|'([^']*)')/g)) {
    const local = m[1].includes(":") ? m[1].slice(m[1].indexOf(":") + 1) : m[1];
    if (!(local in out) || !m[1].includes(":")) out[local] = decodeEntities(m[2] ?? m[3] ?? "");
  }
  return out;
}

function styleTags(attrs: Record<string, string>, styles: Map<string, Record<string, string>>): string[] {
  const sources = [attrs, ...(attrs.style ?? "").split(/\s+/).filter(Boolean).map((id) => styles.get(id) ?? {})];
  const tags: string[] = [];
  for (const a of sources) {
    if (a.fontWeight === "bold" && !tags.includes("b")) tags.push("b");
    if ((a.fontStyle === "italic" || a.fontStyle === "oblique") && !tags.includes("i")) tags.push("i");
    const deco = a.textDecoration ?? "";
    if (/underline/.test(deco) && !/noUnderline/.test(deco) && !tags.includes("u")) tags.push("u");
  }
  return tags;
}

/** TTML/DFXP: every timed `<p>` is a cue; times are relative to the enclosing `<div>`/`<body>`; `dur` stands in for `end`. */
export function parseTtml(input: string): ParseResult & { invalid?: boolean } {
  const text = normalizeNewlines(input)
    .replace(/<!--[\s\S]*?-->/g, "")
    .replace(/<\?[\s\S]*?\?>/g, "")
    .replace(/<!DOCTYPE(?:[^[>]|\[[\s\S]*?\])*>/gi, "")
    .replace(/<!\[CDATA\[([\s\S]*?)\]\]>/g, (_m, c: string) => c.replace(/&/g, "&amp;").replace(/</g, "&lt;"));
  const rootMatch = /<(?:[\w.-]+:)?tt\b([^>]*)>/.exec(text);
  if (!rootMatch) return { cues: [], skipped: 0 };
  const root = attributes(rootMatch[1]);
  const declaredFps = Number(root.frameRate) > 0 ? Number(root.frameRate) : null;
  let fps = declaredFps ?? 30;
  const mult = (root.frameRateMultiplier ?? "").split(/\s+/).map(Number);
  if (mult.length === 2 && mult[0] > 0 && mult[1] > 0) fps = (fps * mult[0]) / mult[1];
  const sub = Number(root.subFrameRate) > 0 ? Number(root.subFrameRate) : 1;
  const tick = Number(root.tickRate) > 0 ? Number(root.tickRate) : declaredFps !== null ? fps * sub : 1;
  const rates: TtmlRates = { frameRate: fps, subFrameRate: sub, tickRate: tick };

  const styles = new Map<string, Record<string, string>>();
  for (const m of text.matchAll(/<(?:[\w.-]+:)?style\b([^>]*?)\/?>/g)) {
    const a = attributes(m[1]);
    if (a.id) styles.set(a.id, a);
  }

  type Frame = { name: string; begin: number; end: number | null };
  const stack: Frame[] = [];
  const found: { start: number; end: number | null; text: string }[] = [];
  let inBody = false;
  let current: { start: number; end: number | null; parts: string[]; open: string[][]; depth: number } | null = null;
  let depth = 0;

  for (const m of text.matchAll(/<(\/?)(?:[\w.-]+:)?([\w.-]+)([^>]*?)(\/?)>|([^<]+)/g)) {
    if (m[5] !== undefined) {
      if (current) current.parts.push(decodeEntities(m[5]).replace(/\s+/g, " "));
      continue;
    }
    const closing = m[1] === "/";
    const name = m[2];
    const selfClosing = m[4] === "/";
    if (closing) {
      depth--;
      if (current && name === "p" && depth === current.depth) {
        for (const tags of [...current.open].reverse()) for (const tag of [...tags].reverse()) current.parts.push(`</${tag}>`);
        const t = tidy(current.parts.join(""));
        if (t) found.push({ start: current.start, end: current.end, text: t });
        current = null;
      } else if (current && name === "span") {
        const tags = current.open.pop() ?? [];
        for (const tag of [...tags].reverse()) current.parts.push(`</${tag}>`);
      }
      if (name === "body") inBody = false;
      if (name === "div" || name === "body") stack.pop();
      continue;
    }
    const attrs = attributes(m[3]);
    if (name === "br") {
      if (current) current.parts.push("\n");
      if (!selfClosing) depth++;
      continue;
    }
    if (name === "body" || name === "div" || name === "p") {
      const parent = stack.at(-1) ?? { name: "", begin: 0, end: null };
      const begin = parent.begin + (ttmlTime(attrs.begin, rates) ?? 0);
      const endRel = ttmlTime(attrs.end, rates);
      const dur = ttmlTime(attrs.dur, rates);
      const end = endRel !== null ? parent.begin + endRel : dur !== null ? begin + dur : parent.end;
      if (name === "body") inBody = true;
      if (name === "p" && inBody && !selfClosing) {
        const timed = attrs.begin !== undefined || attrs.end !== undefined || attrs.dur !== undefined || parent.begin > 0 || parent.end !== null;
        if (timed) {
          const tags = styleTags(attrs, styles);
          current = { start: begin, end, parts: tags.map((t) => `<${t}>`), open: [tags], depth };
        }
      } else if (name !== "p" && !selfClosing) {
        stack.push({ name, begin, end });
      }
      if (!selfClosing) depth++;
      continue;
    }
    if (name === "span" && current && !selfClosing) {
      const tags = styleTags(attrs, styles);
      for (const t of tags) current.parts.push(`<${t}>`);
      current.open.push(tags);
    }
    if (!selfClosing) depth++;
  }

  const sorted = found.map((c, i) => ({ c, i })).sort((a, b) => a.c.start - b.c.start || a.i - b.i).map((x) => x.c);
  const cues: Cue[] = sorted.map((c, i) => {
    const next = sorted[i + 1];
    const end = c.end ?? (next && next.start > c.start ? next.start : c.start + LAST_CUE_MS);
    return { startMs: c.start, endMs: Math.max(c.start, end), text: c.text };
  });
  return { cues, skipped: 0 };
}

// ---------------------------------------------------------------------------------------------------------------------
// Entry point

/** Content-based detection of the new formats (null when none of them fits). */
export function detectMoreFormat(text: string): AnySubtitleFormat | null {
  const t = normalizeNewlines(text);
  if (/<sync\b[^>]*\bstart\s*=/i.test(t)) return "smi";
  if (/<(?:[\w.-]+:)?tt\b[^>]*>/.test(t) && /<(?:[\w.-]+:)?p\b[^>]*\b(?:begin|end|dur)\s*=/.test(t)) return "ttml";
  if (/^\s*\d+:\d{1,2}:\d{1,2}[.,]\d{1,3}\s*,\s*\d+:\d{1,2}:\d{1,2}[.,]\d{1,3}\s*$/m.test(t)) return "sbv";
  if (/^\{\d+\}\{\d*\}/m.test(t)) return "sub";
  if (/^\s*\[\d+:\d{1,2}(?:[.:]\d{1,3})?\]/m.test(t)) return "lrc";
  return null;
}

function parseAs(format: AnySubtitleFormat, text: string): ParseResult {
  switch (format) {
    case "smi":
      return parseSami(text);
    case "lrc":
      return parseLrc(text);
    case "ttml":
      return parseTtml(text);
    case "sub": {
      const r = parseMicroDvd(text);
      return r.cues.length ? r : parseSbv(text); // SubViewer 2 shares the .sub extension
    }
    case "sbv":
      return parseSbv(text);
    default:
      return parseSubtitles(text, format);
  }
}

/**
 * Parses any supported subtitle text. The content decides when it clearly is one format (a WEBVTT header, ASS sections, SAMI SYNC
 * tags, TTML, SBV/MicroDVD/LRC timing); otherwise `hint` (from the file extension) does, and every other reader is tried before
 * giving up, like the desktop parser.
 */
export function parseAnySubtitles(text: string, hint: AnySubtitleFormat): ParseResult {
  const classic = detectFormat(text);
  const order: AnySubtitleFormat[] = [];
  if (classic === "vtt" || classic === "ass") order.push(classic);
  const more = detectMoreFormat(text);
  if (more) order.push(more);
  if (classic === "srt") order.push(hint === "vtt" ? "vtt" : "srt");
  order.push(hint, "srt", "vtt", "ass", "sbv", "ttml", "smi", "lrc", "sub");
  let first: ParseResult | null = null;
  for (const f of [...new Set(order)]) {
    const r = parseAs(f, text);
    first ??= r;
    if (r.cues.length) return r;
  }
  return first ?? { cues: [], skipped: 0 };
}

