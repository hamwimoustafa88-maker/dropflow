// Decodes the bytes of a text file (subtitles, base64 text...) without knowing its encoding up front.
// Order: BOM (UTF-8 / UTF-16 LE / BE) -> BOM-less UTF-16 heuristic -> strict UTF-8 -> windows-1256 when the text reads as Arabic ->
// windows-1252 when it reads as Western European -> the legacy code page of the browser's language (the web has no "system code
// page"; the language is the closest thing) -> windows-1252. Same rules as the desktop SubtitleDecoder.

export interface DecodedText {
  text: string;
  /** "utf-8", "utf-16le", "utf-16be", "windows-1256", "windows-1252" or the legacy encoding of the language ("euc-kr", ...). */
  encoding: string;
}

export interface DecodeOptions {
  /** BCP 47 language whose legacy code page is the last guess (default: the browser's language). */
  language?: string;
}

/** True when `bytes` is valid UTF-8 (strict: invalid sequences make it false). */
export function isValidUtf8(bytes: Uint8Array): boolean {
  try {
    new TextDecoder("utf-8", { fatal: true }).decode(bytes);
    return true;
  } catch {
    return false;
  }
}

function decodeUtf16(bytes: Uint8Array, bigEndian: boolean): string {
  const even = bytes.length - (bytes.length % 2);
  const view = new Uint16Array(even / 2);
  for (let i = 0; i < even; i += 2) {
    view[i / 2] = bigEndian ? (bytes[i] << 8) | bytes[i + 1] : bytes[i] | (bytes[i + 1] << 8);
  }
  const chunk = 0x8000;
  let out = "";
  for (let i = 0; i < view.length; i += chunk) {
    out += String.fromCharCode(...view.subarray(i, i + chunk));
  }
  return out;
}

/** BOM-less UTF-16 shows up as a lot of NUL bytes in every other position (ASCII-heavy text). */
function looksLikeUtf16(bytes: Uint8Array): "le" | "be" | null {
  const n = Math.min(bytes.length, 4096) & ~1;
  if (n < 4) return null;
  let evenZeros = 0;
  let oddZeros = 0;
  for (let i = 0; i < n; i += 2) {
    if (bytes[i] === 0) evenZeros++;
    if (bytes[i + 1] === 0) oddZeros++;
  }
  const pairs = n / 2;
  if (oddZeros > pairs * 0.3 && evenZeros === 0) return "le";
  if (evenZeros > pairs * 0.3 && oddZeros === 0) return "be";
  return null;
}

function stripBom(text: string): string {
  return text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
}

function tryDecode(label: string, bytes: Uint8Array): string | null {
  try {
    return new TextDecoder(label).decode(bytes);
  } catch {
    return null; // runtime without this encoding
  }
}

/** Most high bytes decode to Arabic letters (windows-1256 is one byte per character, so positions line up). */
function looksArabic(bytes: Uint8Array, decoded: string): boolean {
  let high = 0;
  let arabic = 0;
  for (let i = 0; i < bytes.length && i < decoded.length; i++) {
    if (bytes[i] < 0x80) continue;
    high++;
    const c = decoded.charCodeAt(i);
    if (c >= 0x0600 && c <= 0x06ff) arabic++;
  }
  return high > 0 && arabic >= high * 0.6;
}

const WESTERN_PUNCTUATION = new Set("’‘“”–—…«»°€£·¿¡ ");

/** Most high bytes are Latin letters or typographic punctuation, and few of them sit next to another high byte. */
function looksWestern(bytes: Uint8Array, decoded: string): boolean {
  let high = 0;
  let plausible = 0;
  let paired = 0;
  for (let i = 0; i < bytes.length && i < decoded.length; i++) {
    if (bytes[i] < 0x80) continue;
    high++;
    const ch = decoded[i];
    if (/\p{L}/u.test(ch) || WESTERN_PUNCTUATION.has(ch)) plausible++;
    if (i + 1 < bytes.length && bytes[i + 1] >= 0x80) paired++;
  }
  return high > 0 && plausible >= high * 0.8 && paired <= high * 0.3;
}

/** The legacy (pre-Unicode) encoding Windows uses for a language, as a WHATWG encoding label; null when it is windows-1252. */
export function legacyEncodingFor(language: string | undefined): string | null {
  const lang = (language ?? "").toLowerCase();
  const primary = lang.split("-")[0];
  if (primary === "zh") return /-(tw|hk|mo|hant)/.test(lang) ? "big5" : "gbk";
  const table: Record<string, string> = {
    ko: "euc-kr",
    ja: "shift_jis",
    ru: "windows-1251", uk: "windows-1251", bg: "windows-1251", sr: "windows-1251", mk: "windows-1251", be: "windows-1251",
    el: "windows-1253",
    tr: "windows-1254", az: "windows-1254",
    he: "windows-1255", yi: "windows-1255",
    ar: "windows-1256", fa: "windows-1256", ur: "windows-1256",
    et: "windows-1257", lv: "windows-1257", lt: "windows-1257",
    vi: "windows-1258",
    th: "windows-874",
    pl: "windows-1250", cs: "windows-1250", sk: "windows-1250", hu: "windows-1250", hr: "windows-1250", sl: "windows-1250", ro: "windows-1250",
  };
  return table[primary] ?? null;
}

function browserLanguage(): string | undefined {
  try {
    return typeof navigator !== "undefined" ? navigator.language : undefined;
  } catch {
    return undefined;
  }
}

function decodeLegacy(bytes: Uint8Array, language: string | undefined): DecodedText {
  const arabic = tryDecode("windows-1256", bytes);
  if (arabic !== null && looksArabic(bytes, arabic)) return { text: stripBom(arabic), encoding: "windows-1256" };

  const western = tryDecode("windows-1252", bytes) ?? new TextDecoder("latin1").decode(bytes);
  if (looksWestern(bytes, western)) return { text: stripBom(western), encoding: "windows-1252" };

  const legacy = legacyEncodingFor(language);
  if (legacy) {
    const text = tryDecode(legacy, bytes);
    if (text !== null) return { text: stripBom(text), encoding: legacy };
  }
  return { text: stripBom(western), encoding: "windows-1252" };
}

export function decodeText(bytes: Uint8Array, options: DecodeOptions = {}): DecodedText {
  if (bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
    return { text: stripBom(new TextDecoder("utf-8").decode(bytes.subarray(3))), encoding: "utf-8" };
  }
  if (bytes.length >= 2 && bytes[0] === 0xff && bytes[1] === 0xfe) {
    return { text: stripBom(decodeUtf16(bytes.subarray(2), false)), encoding: "utf-16le" };
  }
  if (bytes.length >= 2 && bytes[0] === 0xfe && bytes[1] === 0xff) {
    return { text: stripBom(decodeUtf16(bytes.subarray(2), true)), encoding: "utf-16be" };
  }
  const guess = looksLikeUtf16(bytes);
  if (guess) return { text: stripBom(decodeUtf16(bytes, guess === "be")), encoding: guess === "be" ? "utf-16be" : "utf-16le" };

  try {
    return { text: stripBom(new TextDecoder("utf-8", { fatal: true }).decode(bytes)), encoding: "utf-8" };
  } catch {
    // Not UTF-8: a legacy code page.
  }
  return decodeLegacy(bytes, options.language ?? browserLanguage());
}
