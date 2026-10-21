import { describe, expect, it } from "vitest";
import { ConvertError } from "../types";
import { parseExif } from "../lib/exif";
import { ascii as readAscii, sniffKind } from "../lib/image-formats";
import {
  findMetadata,
  hasMetadata,
  hasSecondJpeg,
  needsReencode,
  needsReencodeForOrientation,
  readPngText,
  stripJpeg,
  stripLossless,
  stripPng,
  stripWebp,
} from "../lib/strip-meta";
import { ascii, buildJpeg, buildPng, buildWebp, concat, fullTiff, pngChunk, u32le } from "./images-fixtures";

/** Marker list of a JPEG's header segments (up to SOS), independent of the code under test. */
function jpegMarkers(b: Uint8Array): number[] {
  const out: number[] = [];
  let pos = 2;
  while (pos + 4 <= b.length && b[pos] === 0xff) {
    const m = b[pos + 1];
    out.push(m);
    if (m === 0xda || m === 0xd9) break;
    pos += 2 + ((b[pos + 2] << 8) | b[pos + 3]);
  }
  return out;
}

describe("JPEG", () => {
  const full = buildJpeg({ exifTiff: fullTiff(true), xmp: true, icc: true, iptc: true, com: true });

  it("source has everything we expect to remove", () => {
    expect(jpegMarkers(full.bytes)).toEqual([0xe0, 0xe1, 0xe1, 0xe2, 0xed, 0xfe, 0xdb, 0xc0, 0xc4, 0xda]);
    expect(findMetadata(full.bytes).sort()).toEqual(["comment", "exif", "iptc", "xmp"]);
    expect(parseExif(full.bytes).gps).toBeDefined();
  });

  it("removes APP1 (Exif+XMP), APP13 and COM, keeps APP0/APP2/coding segments and the scan byte for byte", () => {
    const out = stripJpeg(full.bytes);
    expect(jpegMarkers(out)).toEqual([0xe0, 0xe2, 0xdb, 0xc0, 0xc4, 0xda]);
    expect(out.subarray(out.length - full.scan.length)).toEqual(full.scan); // entropy data untouched
    expect(out.subarray(0, 2)).toEqual(new Uint8Array([0xff, 0xd8]));
    expect(hasMetadata(out)).toBe(false);
    expect(parseExif(out)).toEqual({});
    expect(out.length).toBeLessThan(full.bytes.length);
  });

  it("is idempotent and leaves a clean JPEG unchanged", () => {
    const clean = buildJpeg({ icc: true }).bytes;
    expect(stripJpeg(clean)).toEqual(clean);
    const once = stripJpeg(full.bytes);
    expect(stripJpeg(once)).toEqual(once);
  });

  it("handles fill bytes before a marker", () => {
    const b = buildJpeg({ com: true }).bytes;
    const withFill = concat(b.subarray(0, 2), new Uint8Array([0xff, 0xff]), b.subarray(2));
    expect(jpegMarkers(stripJpeg(withFill))).not.toContain(0xfe);
  });

  it("rejects non-JPEG, truncated and length-lying files with ConvertError('corrupt')", () => {
    expect(() => stripJpeg(ascii("not a jpeg at all"))).toThrow(ConvertError);
    expect(() => stripJpeg(full.bytes.subarray(0, 30))).toThrow(ConvertError);
    const lying = full.bytes.slice();
    lying[2 + 2 + 2 + 14 + 2 + 1] = 0xff; // corrupt APP1 length
    lying[2 + 2 + 2 + 14 + 2] = 0xff;
    expect(() => stripJpeg(lying)).toThrow(ConvertError);
    expect(() => findMetadata(new Uint8Array([1, 2, 3]))).toThrow(ConvertError);
  });
});

describe("PNG", () => {
  const text = pngChunk("tEXt", concat(ascii("Author"), new Uint8Array([0]), ascii("Layla")));
  const itxt = pngChunk("iTXt", concat(ascii("Comment"), new Uint8Array([0, 0, 0]), ascii("ar"), new Uint8Array([0]), new Uint8Array([0]), new TextEncoder().encode("مرحبا")));
  const ztxt = pngChunk("zTXt", concat(ascii("Zip"), new Uint8Array([0, 0, 1, 2, 3])));
  const exif = pngChunk("eXIf", fullTiff(false));
  const time = pngChunk("tIME", new Uint8Array([7, 234, 10, 21, 12, 0, 0]));
  const phys = pngChunk("pHYs", new Uint8Array([0, 0, 11, 19, 0, 0, 11, 19, 1]));
  const src = buildPng(3, 2, [10, 20, 30, 255], [text, itxt, ztxt, exif, time, phys]);

  it("detects and reads the text chunks", () => {
    expect(findMetadata(src).sort()).toEqual(["exif", "text", "text", "text", "time"]);
    expect(readPngText(src)).toEqual([
      { keyword: "Author", text: "Layla" },
      { keyword: "Comment", text: "مرحبا" },
      { keyword: "Zip", text: "(compressed)" },
    ]);
  });

  it("drops eXIf/tEXt/iTXt/zTXt/tIME and keeps the picture chunks intact", () => {
    const out = stripPng(src);
    expect(findMetadata(out)).toEqual([]);
    expect(sniffKind(out)).toBe("png");
    // pHYs survives; IHDR/IDAT/IEND are byte-identical to a plain PNG of the same pixels
    const plain = buildPng(3, 2, [10, 20, 30, 255], [phys]);
    expect(out).toEqual(plain);
  });

  it("rejects truncated PNGs", () => {
    expect(() => stripPng(src.subarray(0, src.length - 5))).toThrow(ConvertError);
    expect(() => stripPng(ascii("PNG?"))).toThrow(ConvertError);
  });
});

describe("WebP", () => {
  const src = buildWebp({ exif: true, xmp: true });

  it("source reports EXIF/XMP chunks and flags", () => {
    expect(findMetadata(src).sort()).toEqual(["exif", "exif-flag", "xmp", "xmp-flag"]);
  });

  it("drops EXIF/XMP chunks, clears only those VP8X flags and fixes the RIFF size", () => {
    const out = stripWebp(src);
    expect(findMetadata(out)).toEqual([]);
    const dv = new DataView(out.buffer, out.byteOffset, out.byteLength);
    expect(dv.getUint32(4, true)).toBe(out.length - 8);
    expect(readAscii(out, 8, 12)).toBe("WEBP");
    // VP8X flags: ICC (0x20) must survive, EXIF/XMP bits must be gone
    expect(readAscii(out, 12, 16)).toBe("VP8X");
    expect(out[20]).toBe(0x20);
    // image and ICC chunks are still there
    const s = Array.from(out).map((c) => String.fromCharCode(c)).join("");
    expect(s).toContain("ICCP");
    expect(s).toContain("VP8 ");
    expect(s).not.toContain("EXIF");
    expect(s).not.toContain("XMP ");
  });

  it("leaves a clean WebP unchanged and survives odd chunk sizes", () => {
    const clean = buildWebp();
    expect(stripWebp(clean)).toEqual(clean);
    expect(clean.length % 2).toBe(0);
  });

  it("rejects truncated chunks", () => {
    const bad = src.slice();
    new DataView(bad.buffer).setUint32(16, 9999, true); // VP8X chunk claims a huge size
    expect(() => stripWebp(bad)).toThrow(ConvertError);
  });

  it("rejects non-WebP data", () => {
    expect(() => stripWebp(concat(ascii("RIFF"), u32le(4), ascii("WAVE")))).toThrow(ConvertError);
  });
});

describe("verifier + dispatch", () => {
  it("detects residue the stripper did not remove (sanity: the verifier is not a rubber stamp)", () => {
    const dirty = buildJpeg({ exifTiff: fullTiff(true) }).bytes;
    expect(hasMetadata(dirty, "jpeg")).toBe(true);
    expect(hasMetadata(stripLossless(dirty).bytes, "jpeg")).toBe(false);
    // a "bad stripper" that only removes COM leaves EXIF behind -> verifier flags it
    const half = buildJpeg({ exifTiff: fullTiff(true), com: true }).bytes;
    expect(findMetadata(half)).toContain("exif");
  });

  it("stripLossless picks the container", () => {
    expect(stripLossless(buildJpeg({ com: true }).bytes).kind).toBe("jpeg");
    expect(stripLossless(buildPng(1, 1)).kind).toBe("png");
    expect(stripLossless(buildWebp()).kind).toBe("webp");
    expect(() => stripLossless(ascii("GIF89a....."))).toThrow(ConvertError);
  });

  it("decides to re-encode only when a non-trivial orientation would be lost", () => {
    const rotated = buildJpeg({ exifTiff: fullTiff(true, { orientation: 6 }) }).bytes;
    const normal = buildJpeg({ exifTiff: fullTiff(true, { orientation: 1 }) }).bytes;
    const none = buildJpeg({ exifTiff: fullTiff(true) }).bytes;
    expect(needsReencodeForOrientation(rotated)).toBe(true);
    expect(needsReencodeForOrientation(normal)).toBe(false);
    expect(needsReencodeForOrientation(none)).toBe(false);
    expect(needsReencodeForOrientation(buildPng(1, 1, [0, 0, 0, 255], [pngChunk("eXIf", fullTiff(true, { orientation: 8 }))]))).toBe(true);
  });

  it("an MPO / Ultra HDR style file (second JPEG after EOI) cannot be stripped losslessly", () => {
    const primary = buildJpeg({ icc: true }).bytes;
    const gainMap = buildJpeg({ xmp: true, exifTiff: fullTiff(true) }).bytes;
    const mpo = concat(primary, gainMap);
    expect(hasSecondJpeg(primary)).toBe(false);
    expect(hasSecondJpeg(mpo)).toBe(true);
    expect(needsReencode(mpo)).toBe(true);
    expect(needsReencode(primary)).toBe(false);
    expect(needsReencode(buildPng(1, 1))).toBe(false);
    // the verifier refuses a lossless-stripped MPO: the second image still carries its metadata
    expect(findMetadata(stripJpeg(mpo))).toContain("embedded-image");
  });
});
