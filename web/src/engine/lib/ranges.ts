// Page-range specs ("1-3, 5, 7-", "last", "5-3") shared by split / rotate / organize / to-images.
// Pure and Node-compatible. Pages are 1-based in the spec and 0-based in the result.

import { ConvertError } from "../types";

const ARABIC_INDIC = "٠١٢٣٤٥٦٧٨٩";
const EXT_ARABIC_INDIC = "۰۱۲۳۴۵۶۷۸۹";

/** Arabic-Indic / Persian digits -> ASCII, Arabic comma/semicolon -> ",", unicode dashes -> "-", spaces trimmed. */
export function normalizeSpec(spec: string): string {
  let out = "";
  for (const ch of spec) {
    const a = ARABIC_INDIC.indexOf(ch);
    if (a >= 0) {
      out += String(a);
      continue;
    }
    const b = EXT_ARABIC_INDIC.indexOf(ch);
    if (b >= 0) {
      out += String(b);
      continue;
    }
    if (ch === "،" || ch === "؛" || ch === ";" || ch === "\n" || ch === "\r") out += ",";
    else if (ch === "–" || ch === "—" || ch === "−" || ch === "‑" || ch === "‐" || ch === "ـ") out += "-";
    else if (ch === "‏" || ch === "‎" || ch === "‪" || ch === "‫" || ch === "‬") continue;
    else out += ch;
  }
  return out.trim();
}

const LAST = new Set(["last", "end", "آخر", "الأخير", "نهاية"]);

function bad(spec: string): never {
  throw new ConvertError("invalidRange", { spec });
}

function parseBound(token: string, pageCount: number, spec: string): number {
  const t = token.trim().toLowerCase();
  if (LAST.has(t)) {
    if (pageCount < 1) bad(spec);
    return pageCount;
  }
  if (!/^\d+$/.test(t)) bad(spec);
  const n = Number(t);
  if (!Number.isSafeInteger(n) || n < 1 || n > pageCount) bad(spec);
  return n;
}

/** One item: "n", "a-b" (descending allowed), "a-" (to the last page). Returns 1-based page numbers in order. */
function parseItem(item: string, pageCount: number, spec: string): number[] {
  const parts = item.split("-").map((p) => p.trim());
  if (parts.length === 1) {
    return [parseBound(parts[0], pageCount, spec)];
  }
  if (parts.length !== 2 || parts[0] === "") bad(spec);
  const a = parseBound(parts[0], pageCount, spec);
  const b = parts[1] === "" ? pageCount : parseBound(parts[1], pageCount, spec);
  const out: number[] = [];
  if (a <= b) for (let p = a; p <= b; p++) out.push(p);
  else for (let p = a; p >= b; p--) out.push(p);
  return out;
}

function items(spec: string): string[] {
  return normalizeSpec(spec)
    .split(",")
    .map((s) => s.trim())
    .filter((s) => s.length > 0);
}

/**
 * Parses a page spec into 0-based indices, order preserved (duplicates kept). Empty spec: all pages when
 * `allowEmpty`, otherwise an "invalidRange" error. Junk, zero or out-of-range -> ConvertError("invalidRange", {spec}).
 */
export function parsePageSpec(spec: string, pageCount: number, opts: { allowEmpty?: boolean } = {}): number[] {
  const parts = items(spec);
  if (parts.length === 0) {
    if (!opts.allowEmpty) bad(spec);
    return Array.from({ length: pageCount }, (_, i) => i);
  }
  const out: number[] = [];
  for (const part of parts) for (const p of parseItem(part, pageCount, spec)) out.push(p - 1);
  return out;
}

/** Split groups: empty spec -> one group per page; otherwise each comma-separated item is one group. */
export function parseSplitGroups(spec: string, pageCount: number): number[][] {
  const parts = items(spec);
  if (parts.length === 0) return Array.from({ length: pageCount }, (_, i) => [i]);
  return parts.map((part) => parseItem(part, pageCount, spec).map((p) => p - 1));
}
