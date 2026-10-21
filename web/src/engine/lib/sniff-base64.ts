// Base64 codec that works on big inputs (no btoa/atob on huge strings, no spread of large arrays) and accepts the
// messy text people paste: whitespace/newlines, a data: URL prefix, the URL-safe alphabet, missing padding.

import { ConvertError } from "../types";

const STD = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
const URLSAFE = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

const ENC_STD = Uint8Array.from(STD, (c) => c.charCodeAt(0));
const ENC_URL = Uint8Array.from(URLSAFE, (c) => c.charCodeAt(0));

const DEC = new Int8Array(256).fill(-1);
for (let i = 0; i < 64; i++) {
  DEC[STD.charCodeAt(i)] = i;
  DEC[URLSAFE.charCodeAt(i)] = i;
}

export interface Base64EncodeOptions {
  /** Use the URL-safe alphabet (-_) and omit the "=" padding. */
  urlSafe?: boolean;
}

/**
 * Encodes in pieces of ASCII bytes (each piece is an independent run of whole 3-byte groups, except the last), so a
 * 300 MB input never needs one 400 MB JS string. Concatenating the pieces gives the full base64 text.
 */
export function* base64Parts(bytes: Uint8Array, opts: Base64EncodeOptions = {}, chunkBytes = 3 * 1024 * 1024): Generator<Uint8Array> {
  const table = opts.urlSafe ? ENC_URL : ENC_STD;
  const step = Math.max(3, chunkBytes - (chunkBytes % 3));
  for (let start = 0; start < bytes.length; start += step) {
    const end = Math.min(bytes.length, start + step);
    const len = end - start;
    const full = len - (len % 3);
    const rest = len - full;
    const outLen = (full / 3) * 4 + (rest ? (opts.urlSafe ? rest + 1 : 4) : 0);
    const out = new Uint8Array(outLen);
    let o = 0;
    let i = start;
    for (; i < start + full; i += 3) {
      const n = (bytes[i] << 16) | (bytes[i + 1] << 8) | bytes[i + 2];
      out[o++] = table[n >> 18];
      out[o++] = table[(n >> 12) & 63];
      out[o++] = table[(n >> 6) & 63];
      out[o++] = table[n & 63];
    }
    if (rest === 1) {
      const n = bytes[i] << 16;
      out[o++] = table[n >> 18];
      out[o++] = table[(n >> 12) & 63];
      if (!opts.urlSafe) {
        out[o++] = 61;
        out[o++] = 61;
      }
    } else if (rest === 2) {
      const n = (bytes[i] << 16) | (bytes[i + 1] << 8);
      out[o++] = table[n >> 18];
      out[o++] = table[(n >> 12) & 63];
      out[o++] = table[(n >> 6) & 63];
      if (!opts.urlSafe) out[o++] = 61;
    }
    yield out;
  }
}

/** Base64 text of the bytes (for small inputs and tests; big inputs should use base64Parts). */
export function bytesToBase64(bytes: Uint8Array, opts: Base64EncodeOptions = {}): string {
  const dec = new TextDecoder("ascii");
  let out = "";
  for (const part of base64Parts(bytes, opts)) out += dec.decode(part);
  return out;
}

/**
 * Decodes base64 text. Ignores whitespace, strips a leading `data:...;base64,` prefix, accepts both alphabets and
 * missing padding. Throws ConvertError("invalidBase64") for anything else.
 */
export function base64ToBytes(input: string): Uint8Array {
  let start = 0;
  const head = input.slice(0, 300);
  const dataUrl = /^\s*data:[^,]*,/i.exec(head);
  if (dataUrl) {
    if (!/;\s*base64\s*,$/i.test(dataUrl[0])) throw new ConvertError("invalidBase64");
    start = dataUrl[0].length;
  }
  const out = new Uint8Array(Math.ceil(((input.length - start) * 3) / 4) + 3);
  let o = 0;
  let acc = 0;
  let bits = 0;
  let count = 0;
  let pads = 0;
  for (let i = start; i < input.length; i++) {
    const c = input.charCodeAt(i);
    if (c === 32 || (c >= 9 && c <= 13)) continue;
    if (c === 61) {
      pads++;
      continue;
    }
    const v = c < 256 ? DEC[c] : -1;
    if (v < 0 || pads > 0) throw new ConvertError("invalidBase64");
    acc = (acc << 6) | v;
    bits += 6;
    count++;
    if (bits >= 8) {
      bits -= 8;
      out[o++] = (acc >> bits) & 0xff;
      acc &= (1 << bits) - 1;
    }
  }
  if (count === 0 || count % 4 === 1 || pads > 2) throw new ConvertError("invalidBase64");
  return out.subarray(0, o);
}
