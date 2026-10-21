// Decodes the bytes of a text file (subtitles, base64 text...) without knowing its encoding up front.
// Order: BOM (UTF-8 / UTF-16 LE / BE) -> BOM-less UTF-16 heuristic -> strict UTF-8 -> windows-1256 (legacy Arabic).

export interface DecodedText {
  text: string;
  /** "utf-8", "utf-16le", "utf-16be" or "windows-1256". */
  encoding: string;
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

export function decodeText(bytes: Uint8Array): DecodedText {
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
    // Not UTF-8: old Arabic subtitle files are usually windows-1256.
  }
  try {
    return { text: stripBom(new TextDecoder("windows-1256").decode(bytes)), encoding: "windows-1256" };
  } catch {
    // Runtime without windows-1256 support: fall back to a lossless byte-per-char mapping.
    return { text: stripBom(new TextDecoder("latin1").decode(bytes)), encoding: "windows-1252" };
  }
}
