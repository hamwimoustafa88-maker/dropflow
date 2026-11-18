import { existsSync, readFileSync } from "node:fs";
import { inflateSync } from "node:zlib";
import { describe, expect, it } from "vitest";
import { fontsConverter } from "../converters/fonts";
import { convertFontBytes } from "../converters/fonts.impl";
import { CFF, TRUETYPE, checksum, decodeWoff, encodeWoff, readSfnt, writeSfnt, type Sfnt } from "../lib/sfnt";
import { registry } from "../registry";
import { ConvertError } from "../types";

// A small sfnt written here byte by byte (independently of lib/sfnt.ts): a head table plus tables of compressible and random bytes.
function be32(v: number): number[] {
  return [(v >>> 24) & 0xff, (v >>> 16) & 0xff, (v >>> 8) & 0xff, v & 0xff];
}
function sum(bytes: number[]): number {
  let s = 0;
  for (let i = 0; i < bytes.length; i += 4) s = (s + (((bytes[i] ?? 0) << 24) | ((bytes[i + 1] ?? 0) << 16) | ((bytes[i + 2] ?? 0) << 8) | (bytes[i + 3] ?? 0))) >>> 0;
  return s;
}
function testFont(flavor: number, outlineTag: "glyf" | "CFF "): Uint8Array {
  const head = [...be32(0x00010000), ...be32(0x00028000), ...be32(0), ...be32(0x5f0f3cf5), 0, 11, 0x03, 0xe8, ...new Array(36).fill(0)];
  const tables: [string, number[]][] = [
    ["head", head],
    [outlineTag, Array.from({ length: 3001 }, (_, i) => (i * 7) % 13)], // compressible, odd length
    ["name", Array.from({ length: 37 }, (_, i) => (i * 131 + 17) & 0xff)], // short: stays uncompressed
    ["OS/2", Array.from({ length: 96 }, () => 0)],
  ];
  tables.sort((a, b) => (a[0] < b[0] ? -1 : 1));
  const n = tables.length;
  const out: number[] = [...be32(flavor), 0, n, 0, 64, 0, 2, 0, 0];
  let offset = 12 + 16 * n;
  for (const [tag, data] of tables) {
    out.push(...[...tag].map((c) => c.charCodeAt(0)), ...be32(sum(data)), ...be32(offset), ...be32(data.length));
    offset += (data.length + 3) & ~3;
  }
  let headOffset = 0;
  for (const [tag, data] of tables) {
    if (tag === "head") headOffset = out.length;
    out.push(...data);
    while (out.length % 4) out.push(0);
  }
  out.splice(headOffset + 8, 4, ...be32((0xb1b0afba - sum(out)) >>> 0));
  return new Uint8Array(out);
}

function assertChecksums(file: Uint8Array) {
  const n = (file[4] << 8) | file[5];
  let headOffset = -1;
  for (let i = 0; i < n; i++) {
    const r = 12 + i * 16;
    const tag = String.fromCharCode(...file.subarray(r, r + 4));
    const rd = (o: number) => ((file[o] << 24) | (file[o + 1] << 16) | (file[o + 2] << 8) | file[o + 3]) >>> 0;
    const offset = rd(r + 8);
    const data = [...file.subarray(offset, offset + rd(r + 12))];
    expect(offset % 4, tag).toBe(0);
    if (tag === "head") {
      headOffset = offset;
      data.splice(8, 4, 0, 0, 0, 0);
    }
    expect(sum(data), `checksum of ${tag}`).toBe(rd(r + 4));
  }
  const copy = [...file];
  const stored = ((copy[headOffset + 8] << 24) | (copy[headOffset + 9] << 16) | (copy[headOffset + 10] << 8) | copy[headOffset + 11]) >>> 0;
  copy.splice(headOffset + 8, 4, 0, 0, 0, 0);
  expect(stored).toBe((0xb1b0afba - sum(copy)) >>> 0);
}

const tablesOf = (f: Sfnt) => [...f.tables.entries()].filter(([t]) => t !== "head").map(([t, d]) => [t, [...d]]);

describe("fonts converter manifest", () => {
  it("offers TTF/OTF to WOFF and WOFF back, nothing else", () => {
    expect(fontsConverter.supports("convert:woff", "ttf")).toBe(true);
    expect(fontsConverter.supports("convert:woff", "otf")).toBe(true);
    expect(fontsConverter.supports("convert:ttf", "woff")).toBe(true);
    expect(fontsConverter.supports("convert:otf", "woff")).toBe(true);
    for (const [op, ext] of [["convert:woff", "woff"], ["convert:ttf", "otf"], ["convert:otf", "ttf"], ["convert:woff2", "ttf"], ["convert:eot", "ttf"], ["convert:ttf", "woff2"], ["convert:woff", "png"]])
      expect(fontsConverter.supports(op, ext), `${op} ${ext}`).toBe(false);
    expect(registry.find("convert:woff", "ttf")?.id).toBe("fonts");
    expect(fontsConverter.options("convert:woff")).toEqual([]);
    expect(fontsConverter.runtime("convert:woff", "ttf")).toBe("worker");
  });
});

describe("sfnt and WOFF 1.0", () => {
  it("rewrites an sfnt with correct checksums and head.checkSumAdjustment", () => {
    const original = testFont(TRUETYPE, "glyf");
    assertChecksums(original); // the test's own writer
    const font = readSfnt(original);
    const written = writeSfnt(font);
    assertChecksums(written);
    expect(tablesOf(readSfnt(written))).toEqual(tablesOf(readSfnt(original)));
  });

  it("writes a WOFF whose header, directory and zlib streams follow the specification", () => {
    const font = readSfnt(testFont(TRUETYPE, "glyf"));
    const woff = encodeWoff(font);
    writeSfnt(font); // the tables the WOFF stores: head with its checkSumAdjustment filled in
    const v = new DataView(woff.buffer, woff.byteOffset, woff.byteLength);
    expect(String.fromCharCode(...woff.subarray(0, 4))).toBe("wOFF");
    expect(v.getUint32(4)).toBe(TRUETYPE);
    expect(v.getUint32(8)).toBe(woff.length);
    expect(woff.length % 4).toBe(0);
    const n = v.getUint16(12);
    expect(n).toBe(4);
    expect(v.getUint32(16)).toBe(12 + 16 * 4 + [...font.tables.values()].reduce((a, t) => a + ((t.length + 3) & ~3), 0));
    let previous = "";
    let compressed = 0;
    for (let i = 0; i < n; i++) {
      const e = 44 + i * 20;
      const tag = String.fromCharCode(...woff.subarray(e, e + 4));
      expect(tag > previous).toBe(true);
      previous = tag;
      const offset = v.getUint32(e + 4);
      const compLength = v.getUint32(e + 8);
      const origLength = v.getUint32(e + 12);
      expect(offset % 4).toBe(0);
      const table = font.tables.get(tag)!;
      expect(origLength).toBe(table.length);
      if (compLength < origLength) {
        compressed++;
        expect([...inflateSync(woff.subarray(offset, offset + compLength))]).toEqual([...table]); // Node's own zlib reads it
      }
      if (tag !== "head") expect(v.getUint32(e + 16)).toBe(checksum(table));
    }
    expect(compressed).toBeGreaterThan(0);
  });

  it("round-trips TTF -> WOFF -> TTF and OTF -> WOFF -> OTF", () => {
    for (const [flavor, outline, target] of [[TRUETYPE, "glyf", "ttf"], [CFF, "CFF ", "otf"]] as const) {
      const original = testFont(flavor, outline);
      const woff = convertFontBytes(original, "woff");
      const back = convertFontBytes(woff, target);
      assertChecksums(back);
      const font = readSfnt(back);
      expect(font.flavor).toBe(flavor);
      expect(tablesOf(font)).toEqual(tablesOf(readSfnt(original)));
    }
  });

  it("reads a font by its content and refuses what it cannot do", () => {
    const ttf = testFont(TRUETYPE, "glyf");
    const woff = convertFontBytes(ttf, "woff");
    expect(convertFontBytes(woff, "woff")[0]).toBe(0x77); // a "ttf" that is really a WOFF is still read
    const code = (f: () => unknown) => {
      try {
        f();
      } catch (e) {
        return e instanceof ConvertError ? e.code : String(e);
      }
      return "none";
    };
    expect(code(() => convertFontBytes(woff, "otf"))).toBe("fontOutlines"); // TrueType outlines: OTF needs the Windows app
    expect(code(() => convertFontBytes(convertFontBytes(testFont(CFF, "CFF "), "woff"), "ttf"))).toBe("fontOutlines");
    expect(code(() => convertFontBytes(new Uint8Array([0x74, 0x74, 0x63, 0x66, ...new Array(20).fill(0)]), "woff"))).toBe("fontCollection");
    expect(code(() => convertFontBytes(new Uint8Array([0x77, 0x4f, 0x46, 0x32, ...new Array(60).fill(0)]), "ttf"))).toBe("unsupported");
    expect(code(() => convertFontBytes(new TextEncoder().encode("this is not a font at all"), "woff"))).toBe("notAFont");
    const damaged = woff.slice(0, 60);
    expect(code(() => convertFontBytes(damaged, "ttf"))).toBe("corrupt");
  });

  it("runs through the converter contract and names the result", async () => {
    const file = new File([testFont(TRUETYPE, "glyf") as BlobPart], "خط.ttf");
    const [out] = await fontsConverter.run({ id: "1", operation: "convert:woff", files: [file], options: {} }, () => {}, new AbortController().signal);
    expect(out.name).toBe("خط.woff");
    expect(out.type).toBe("font/woff");
    expect(decodeWoff(new Uint8Array(await out.arrayBuffer())).tables.size).toBe(4);
  });
});

// Third-party web fonts (Font Awesome) installed by another program on the developer's PC: .woff made by another tool must decode to
// the tables of the .ttf next to it. Skipped where the files are absent (CI).
const fa = "C:/Program Files/Maxon Cinema 4D 2024/resource/modules/licensing.module/html/assets/css/libs/fontawesome/webfonts/fa-regular-400";
describe.skipIf(!existsSync(`${fa}.woff`) || !existsSync(`${fa}.ttf`))("a WOFF made by another tool", () => {
  it("decodes to the same tables as its TTF", () => {
    const fromWoff = decodeWoff(new Uint8Array(readFileSync(`${fa}.woff`)));
    const ttf = readSfnt(new Uint8Array(readFileSync(`${fa}.ttf`)));
    for (const [tag, data] of ttf.tables) {
      if (tag === "head") continue;
      expect([...(fromWoff.tables.get(tag) ?? [])], tag).toEqual([...data]);
    }
    assertChecksums(writeSfnt(fromWoff));
  });
});
