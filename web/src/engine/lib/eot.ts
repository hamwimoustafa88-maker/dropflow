// SPDX-License-Identifier: GPL-3.0-or-later
// Embedded OpenType (W3C Member Submission "Embedded OpenType (EOT) File Format", version 0x00020001), ported from the Windows app
// (src/DropFlow.Engines.Fonts/Web/Eot.cs): a little-endian header that repeats the font's OS/2 classification and its names, then the
// font file itself. Written uncompressed (no MicroType Express, no XOR obfuscation, no subsetting), which every EOT reader accepts.
// EOT is output only. Pure functions: usable in the worker and in Node tests.

import type { Sfnt } from "./sfnt";

export const EOT_VERSION = 0x00020001;
export const EOT_MAGIC = 0x504c;
const DEFAULT_CHARSET = 1;

export const NAME_FAMILY = 1;
export const NAME_SUBFAMILY = 2;
export const NAME_FULL = 4;
export const NAME_VERSION = 5;

const u16 = (b: Uint8Array, o: number) => (b[o] << 8) | b[o + 1];
const u32 = (b: Uint8Array, o: number) => ((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]) >>> 0;

/**
 * The string with this name id from the font's 'name' table: Windows Unicode US English first, then any Windows Unicode record, then
 * Unicode platform records, then Macintosh Roman (read as Latin-1, like the Windows app). Undefined when the font has none.
 */
export function fontName(font: Sfnt, nameId: number): string | undefined {
  const table = font.tables.get("name");
  if (!table || table.length < 6) return undefined;
  const count = u16(table, 2);
  const storage = u16(table, 4);
  let best: { score: number; value: string } | undefined;
  for (let i = 0; i < count && 6 + i * 12 + 12 <= table.length; i++) {
    const r = 6 + i * 12;
    const platform = u16(table, r);
    const encoding = u16(table, r + 2);
    const language = u16(table, r + 4);
    const id = u16(table, r + 6);
    const length = u16(table, r + 8);
    const offset = u16(table, r + 10);
    if (id !== nameId || storage + offset + length > table.length) continue;
    const score =
      platform === 3 && (encoding === 1 || encoding === 10) ? (language === 0x0409 ? 4 : 3) : platform === 0 ? 2 : platform === 1 && encoding === 0 ? 1 : 0;
    if (score === 0 || (best && best.score >= score)) continue;
    const bytes = table.subarray(storage + offset, storage + offset + length);
    best = { score, value: platform === 1 ? latin1(bytes) : utf16be(bytes) };
  }
  return best?.value;
}

function latin1(bytes: Uint8Array): string {
  let s = "";
  for (const b of bytes) s += String.fromCharCode(b);
  return s;
}

function utf16be(bytes: Uint8Array): string {
  let s = "";
  for (let i = 0; i + 1 < bytes.length; i += 2) s += String.fromCharCode((bytes[i] << 8) | bytes[i + 1]);
  return s;
}

class LittleEndianWriter {
  private parts: number[] = [];
  private chunks: Uint8Array[] = [];
  private length = 0;
  u8(v: number) {
    this.parts.push(v & 0xff);
    this.length++;
  }
  u16(v: number) {
    this.u8(v);
    this.u8(v >>> 8);
  }
  u32(v: number) {
    this.u16(v & 0xffff);
    this.u16(v >>> 16);
  }
  bytes(b: Uint8Array) {
    this.flush();
    this.chunks.push(b);
    this.length += b.length;
  }
  private flush() {
    if (this.parts.length) this.chunks.push(Uint8Array.from(this.parts));
    this.parts = [];
  }
  toArray(): Uint8Array {
    this.flush();
    const out = new Uint8Array(this.length);
    let o = 0;
    for (const c of this.chunks) {
      out.set(c, o);
      o += c.length;
    }
    return out;
  }
}

/** UTF-16LE bytes of a name, preceded by their byte length. */
function writeName(w: LittleEndianWriter, value: string | undefined) {
  const text = value ?? "";
  const bytes = new Uint8Array(text.length * 2);
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    bytes[i * 2] = c & 0xff;
    bytes[i * 2 + 1] = c >>> 8;
  }
  w.u16(bytes.length);
  w.bytes(bytes);
}

/**
 * Writes an EOT. `font` gives the OS/2, head and name tables; `fontData` is the font file stored in the EOT, exactly as given (the
 * original TTF/OTF bytes when the input was one).
 */
export function writeEot(font: Sfnt, fontData: Uint8Array): Uint8Array {
  const os2 = font.tables.get("OS/2");
  const head = font.tables.get("head");
  const w = new LittleEndianWriter();
  w.u32(0); // EOTSize, filled in below
  w.u32(fontData.length);
  w.u32(EOT_VERSION);
  w.u32(0); // Flags: not subset, not compressed, not obfuscated
  const panose = new Uint8Array(10);
  if (os2 && os2.length >= 42) panose.set(os2.subarray(32, 42));
  w.bytes(panose);
  w.u8(DEFAULT_CHARSET);
  w.u8(os2 && os2.length >= 64 && (u16(os2, 62) & 1) !== 0 ? 1 : 0); // fsSelection bit 0: italic
  w.u32(os2 && os2.length >= 6 ? u16(os2, 4) : 400); // usWeightClass
  w.u16(os2 && os2.length >= 10 ? u16(os2, 8) : 0); // fsType (embedding permissions)
  w.u16(EOT_MAGIC);
  for (let i = 0; i < 4; i++) w.u32(os2 && os2.length >= 58 ? u32(os2, 42 + i * 4) : 0); // ulUnicodeRange1-4
  const version = os2 && os2.length >= 2 ? u16(os2, 0) : 0;
  for (let i = 0; i < 2; i++) w.u32(version >= 1 && os2 && os2.length >= 86 ? u32(os2, 78 + i * 4) : 0); // ulCodePageRange1-2
  w.u32(head && head.length >= 12 ? u32(head, 8) : 0); // head.checkSumAdjustment
  for (let i = 0; i < 4; i++) w.u32(0); // Reserved1-4
  w.u16(0); // Padding1
  writeName(w, fontName(font, NAME_FAMILY));
  w.u16(0); // Padding2
  writeName(w, fontName(font, NAME_SUBFAMILY));
  w.u16(0); // Padding3
  writeName(w, fontName(font, NAME_VERSION));
  w.u16(0); // Padding4
  writeName(w, fontName(font, NAME_FULL));
  w.u16(0); // Padding5
  w.u16(0); // RootStringSize: no root string, the font is not restricted to sites
  w.bytes(fontData);
  const out = w.toArray();
  const size = out.length;
  out[0] = size & 0xff;
  out[1] = (size >>> 8) & 0xff;
  out[2] = (size >>> 16) & 0xff;
  out[3] = (size >>> 24) & 0xff;
  return out;
}
