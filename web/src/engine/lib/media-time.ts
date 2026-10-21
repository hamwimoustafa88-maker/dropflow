// Time parsing for trim / snapshot options. Pure and Node-compatible.
import { ConvertError } from "../types";

const ARABIC_INDIC = "٠١٢٣٤٥٦٧٨٩"; // U+0660..U+0669
const EXT_ARABIC_INDIC = "۰۱۲۳۴۵۶۷۸۹"; // U+06F0..U+06F9

/** Arabic-Indic digits, Arabic decimal/thousands separators and full-width colon -> plain ASCII. */
export function normalizeDigits(input: string): string {
  let out = "";
  for (const ch of input) {
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
    if (ch === "٫" || ch === "،") out += "."; // Arabic decimal separator / Arabic comma
    else if (ch === "：") out += ":"; // full-width colon
    else out += ch;
  }
  return out;
}

const PART = /^\d+$/;
const LAST = /^(?:\d+(?:\.\d+)?|\.\d+)$/;

/**
 * "90", "90.5", "1:30", "01:02:03.5", ",5" style input -> seconds. Returns null when the text is not a time.
 * Minutes and seconds must be < 60 whenever a larger unit is present (so "1:75" is invalid, "75" is fine).
 */
export function parseTime(text: string): number | null {
  let t = normalizeDigits(text).trim();
  if (!t) return null;
  // "1,5" -> 1.5 (decimal comma) when there is no dot already.
  if (!t.includes(".") && /^[\d:]*,\d+$/.test(t)) t = t.replace(",", ".");
  const parts = t.split(":");
  if (parts.length > 3) return null;
  for (let i = 0; i < parts.length; i++) {
    const last = i === parts.length - 1;
    if (!(last ? LAST : PART).test(parts[i])) return null;
  }
  const nums = parts.map(Number);
  if (nums.some((n) => !Number.isFinite(n))) return null;
  if (parts.length === 1) return nums[0];
  // seconds (last) and minutes (middle, when hours are present) must be < 60
  if (nums[nums.length - 1] >= 60) return null;
  if (parts.length === 3 && nums[1] >= 60) return null;
  return nums.reduce((acc, n) => acc * 60 + n, 0);
}

export interface TimeRange {
  start: number;
  /** null = until the end of the file */
  end: number | null;
}

/**
 * Empty start means 0, empty end means "to the end". Throws ConvertError("invalidTime") for unparsable text
 * or when end <= start.
 */
export function parseTimeRange(startText: string, endText: string): TimeRange {
  const start = startText.trim() === "" ? 0 : parseTime(startText);
  if (start === null) throw new ConvertError("invalidTime");
  const hasEnd = endText.trim() !== "";
  const end = hasEnd ? parseTime(endText) : null;
  if (hasEnd && end === null) throw new ConvertError("invalidTime");
  if (end !== null && end <= start) throw new ConvertError("invalidTime");
  return { start, end };
}

/** Seconds -> the shortest decimal string ffmpeg accepts ("1.5", "90", "0.25"). */
export function formatFfmpegTime(seconds: number): string {
  const s = Math.max(0, seconds);
  return String(Number(s.toFixed(3)));
}
