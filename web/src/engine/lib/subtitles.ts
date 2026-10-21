// Pure subtitle parsers/writers (SRT, WebVTT, ASS/SSA) shared by the subtitles converter. No DOM, no I/O.
//
// Cue.text is "plain text with optional <b>/<i>/<u> tags": entities are decoded, VTT-only tags (<c>, <v>, <lang>,
// timestamps), <font> and ASS override blocks are gone, lines are trimmed and never blank. Arabic text, RTL marks
// (U+200F/U+200E) and ZWNJ are never touched.

export interface Cue {
  startMs: number;
  endMs: number;
  text: string;
}

export interface ParseResult {
  cues: Cue[];
  /** Blocks/lines that looked like cues but had an unusable timestamp (or no timestamp at all). */
  skipped: number;
}

export type SubtitleFormat = "srt" | "vtt" | "ass";

/** Strips a leading BOM and unifies CRLF / CR / LF to "\n". */
export function normalizeNewlines(input: string): string {
  const s = input.charCodeAt(0) === 0xfeff ? input.slice(1) : input;
  return s.replace(/\r\n?/g, "\n");
}

const TS = /^(?:(\d+):)?(\d{1,2}):(\d{1,2})(?:[.,](\d+))?$/;

/** "01:02:03,456", "02:03.4", "1:02:03" ... -> milliseconds, or null when it is not a valid timestamp. */
export function parseTimestamp(raw: string): number | null {
  const m = TS.exec(raw.trim());
  if (!m) return null;
  const hours = m[1] ? Number(m[1]) : 0;
  const minutes = Number(m[2]);
  const seconds = Number(m[3]);
  if (minutes > 59 || seconds > 59) return null;
  let ms = 0;
  if (m[4]) {
    const f = m[4];
    ms = Number(f.slice(0, 3).padEnd(3, "0"));
    if (f.length > 3 && f.charCodeAt(3) >= 53 /* '5' */) ms += 1;
  }
  return ((hours * 60 + minutes) * 60 + seconds) * 1000 + ms;
}

export function formatTimestamp(ms: number, separator: "," | "."): string {
  const total = Math.max(0, Math.round(ms));
  const h = Math.floor(total / 3_600_000);
  const m = Math.floor(total / 60_000) % 60;
  const s = Math.floor(total / 1000) % 60;
  const f = total % 1000;
  const p2 = (n: number) => String(n).padStart(2, "0");
  return `${p2(h)}:${p2(m)}:${p2(s)}${separator}${String(f).padStart(3, "0")}`;
}

/** "vtt" / "ass" are certain (header / section markers); "srt" means "has cue timing lines". */
export function detectFormat(text: string): SubtitleFormat | "unknown" {
  const t = normalizeNewlines(text).trimStart();
  if (/^WEBVTT/.test(t)) return "vtt";
  if (/^\[(?:Script Info|V4\+? Styles|Events)\]/im.test(t) || /^Dialogue:\s*\d+\s*,\s*\d+:\d{2}:\d{2}[.,]\d{1,3}\s*,/m.test(t)) return "ass";
  if (/\d{1,2}:\d{2}(?::\d{2})?[.,]\d{1,3}\s*-->\s*\d/.test(t) || /\d{1,2}:\d{2}:\d{2}\s*-->\s*\d/.test(t)) return "srt";
  return "unknown";
}

// ---------------------------------------------------------------------------------------------------------------------
// Text cleaning

const NAMED_ENTITIES: Record<string, string> = {
  amp: "&",
  lt: "<",
  gt: ">",
  quot: '"',
  apos: "'",
  nbsp: " ",
  lrm: "‎",
  rlm: "‏",
};

export function decodeEntities(text: string): string {
  return text.replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (whole, body: string) => {
    if (body[0] === "#") {
      const code = body[1] === "x" || body[1] === "X" ? parseInt(body.slice(2), 16) : parseInt(body.slice(1), 10);
      if (!Number.isFinite(code) || code <= 0 || code > 0x10ffff || (code >= 0xd800 && code <= 0xdfff)) return whole;
      return String.fromCodePoint(code);
    }
    return NAMED_ENTITIES[body.toLowerCase()] ?? whole;
  });
}

const KEEP_TAG = /^<\/?[biu]>$/i;
const ANY_TAG = /<\/?[A-Za-z][^<>\n]*>|<\d{1,3}:\d{2}(?::\d{2})?[.,]\d{1,3}>/g;

function tidyLines(text: string): string {
  return text
    .split("\n")
    .map((l) => l.trim())
    .filter((l) => l !== "")
    .join("\n");
}

/** Cleans the text of one SRT/VTT cue. Keeps <b>/<i>/<u> (lower-cased), drops every other tag, optionally decodes entities. */
export function cleanCueText(raw: string, decode: boolean): string {
  let t = raw.replace(/\{\\[^}]*\}/g, "").replace(/<br\s*\/?>/gi, "\n");
  t = t.replace(ANY_TAG, (tag) => (KEEP_TAG.test(tag) ? tag.toLowerCase() : ""));
  if (decode) t = decodeEntities(t);
  return tidyLines(t);
}

function cleanAssText(raw: string): string {
  const t = raw
    .replace(/\{[^}]*\}/g, "")
    .replace(/\\[Nn]/g, "\n")
    .replace(/\\h/g, " ");
  return tidyLines(t);
}

// ---------------------------------------------------------------------------------------------------------------------
// SRT / VTT

function splitBlocks(normalized: string): string[][] {
  const blocks: string[][] = [];
  let cur: string[] = [];
  for (const line of normalized.split("\n")) {
    if (line.trim() === "") {
      if (cur.length) blocks.push(cur);
      cur = [];
    } else {
      cur.push(line);
    }
  }
  if (cur.length) blocks.push(cur);
  return blocks;
}

function parseCueBlocks(text: string, vtt: boolean): ParseResult {
  const cues: Cue[] = [];
  let skipped = 0;
  for (const block of splitBlocks(normalizeNewlines(text))) {
    if (vtt && /^(?:NOTE|STYLE|REGION)(?:\s|$)/.test(block[0])) continue;
    const idx = block.findIndex((l) => l.includes("-->"));
    if (idx < 0) {
      if (vtt && /^WEBVTT/.test(block[0])) continue;
      skipped++;
      continue;
    }
    const line = block[idx];
    const arrow = line.indexOf("-->");
    const start = parseTimestamp(line.slice(0, arrow).trim().split(/\s+/).pop() ?? "");
    const end = parseTimestamp(line.slice(arrow + 3).trim().split(/\s+/)[0] ?? "");
    if (start === null || end === null) {
      skipped++;
      continue;
    }
    const body = cleanCueText(block.slice(idx + 1).join("\n"), vtt);
    if (!body) continue;
    cues.push({ startMs: start, endMs: Math.max(start, end), text: body });
  }
  return { cues, skipped };
}

export function parseSrt(text: string): ParseResult {
  return parseCueBlocks(text, false);
}

export function parseVtt(text: string): ParseResult {
  return parseCueBlocks(text, true);
}

// ---------------------------------------------------------------------------------------------------------------------
// ASS / SSA

const DEFAULT_ASS_COLUMNS = ["layer", "start", "end", "style", "name", "marginl", "marginr", "marginv", "effect", "text"];

function splitFields(s: string, n: number): string[] {
  const out: string[] = [];
  let start = 0;
  for (let i = 0; i < n - 1; i++) {
    const j = s.indexOf(",", start);
    if (j < 0) break;
    out.push(s.slice(start, j));
    start = j + 1;
  }
  out.push(s.slice(start));
  return out;
}

export function parseAss(text: string): ParseResult {
  const cues: Cue[] = [];
  let skipped = 0;
  let columns = DEFAULT_ASS_COLUMNS;
  let inEvents = false;
  let sawEvents = false;
  for (const raw of normalizeNewlines(text).split("\n")) {
    const line = raw.trim();
    if (/^\[.*\]$/.test(line)) {
      inEvents = /^\[events\]$/i.test(line);
      if (inEvents) sawEvents = true;
      continue;
    }
    if (sawEvents && !inEvents) continue;
    if (/^format:/i.test(line) && inEvents) {
      columns = line
        .slice(7)
        .split(",")
        .map((c) => c.trim().toLowerCase());
      continue;
    }
    if (!/^dialogue:/i.test(line)) continue; // Comment: lines, styles, blank lines...
    const fields = splitFields(line.slice(9).replace(/^\s+/, ""), columns.length);
    const iStart = columns.indexOf("start");
    const iEnd = columns.indexOf("end");
    const iText = columns.indexOf("text") >= 0 ? columns.indexOf("text") : columns.length - 1;
    const start = iStart >= 0 ? parseTimestamp(fields[iStart] ?? "") : null;
    const end = iEnd >= 0 ? parseTimestamp(fields[iEnd] ?? "") : null;
    if (start === null || end === null) {
      skipped++;
      continue;
    }
    const body = cleanAssText(fields[iText] ?? "");
    if (!body) continue;
    cues.push({ startMs: start, endMs: Math.max(start, end), text: body });
  }
  // ASS dialogue lines are not required to be in time order; SRT/VTT players expect them to be.
  const sorted = cues.map((c, i) => ({ c, i })).sort((a, b) => a.c.startMs - b.c.startMs || a.i - b.i).map((x) => x.c);
  return { cues: sorted, skipped };
}

/** Parses with the format detected from the content; `hint` (usually the file extension) decides when it is ambiguous. */
export function parseSubtitles(text: string, hint: SubtitleFormat): ParseResult {
  const detected = detectFormat(text);
  const format: SubtitleFormat = detected === "vtt" || detected === "ass" ? detected : detected === "srt" ? (hint === "vtt" ? "vtt" : "srt") : hint;
  return format === "vtt" ? parseVtt(text) : format === "ass" ? parseAss(text) : parseSrt(text);
}

// ---------------------------------------------------------------------------------------------------------------------
// Writers

/** SRT: 1-based index, `HH:MM:SS,mmm`, CRLF line endings, blank line after every cue. */
export function cuesToSrt(cues: readonly Cue[]): string {
  let out = "";
  cues.forEach((c, i) => {
    out += `${i + 1}\r\n${formatTimestamp(c.startMs, ",")} --> ${formatTimestamp(c.endMs, ",")}\r\n${c.text.replace(/\n/g, "\r\n")}\r\n\r\n`;
  });
  return out;
}

const KEEP_TAG_SPLIT = /(<\/?[biu]>)/i;

/** Escapes cue text for WebVTT while keeping <b>/<i>/<u>; "-->" cannot appear inside a cue. */
export function escapeVtt(text: string): string {
  return text
    .split(KEEP_TAG_SPLIT)
    .map((part, i) => (i % 2 === 1 ? part : part.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")))
    .join("");
}

/** WebVTT: `WEBVTT` header, `HH:MM:SS.mmm`, LF line endings. */
export function cuesToVtt(cues: readonly Cue[]): string {
  let out = "WEBVTT\n\n";
  for (const c of cues) {
    out += `${formatTimestamp(c.startMs, ".")} --> ${formatTimestamp(c.endMs, ".")}\n${escapeVtt(c.text)}\n\n`;
  }
  return out;
}

/** Plain text: cue texts without tags or timing, separated by a blank line. */
export function cuesToTxt(cues: readonly Cue[]): string {
  if (cues.length === 0) return "";
  return cues.map((c) => c.text.replace(/<\/?[biu]>/gi, "")).join("\n\n") + "\n";
}
