import { describe, expect, it } from "vitest";
import { findExifBlock, hasExifData, hasNonTrivialOrientation, parseExif, parseTiff, readOrientation } from "../lib/exif";
import { formatExposure, formatReport } from "../lib/image-report";
import { ascii, buildJpeg, buildPng, buildTiff, buildWebp, concat, fullTiff, pngChunk, riffChunk, u32le } from "./images-fixtures";

describe.each([
  ["little-endian", true],
  ["big-endian", false],
])("parseExif (%s TIFF)", (_name, le) => {
  it("reads camera, exposure and lens tags", () => {
    const d = parseTiff(fullTiff(le, { orientation: 6 }));
    expect(d.make).toBe("DropFlow Cam");
    expect(d.model).toBe("Model X");
    expect(d.software).toBe("Soft 1.0");
    expect(d.dateTime).toBe("2026:10:21 12:00:00");
    expect(d.dateTimeOriginal).toBe("2026:10:20 08:15:30");
    expect(d.orientation).toBe(6);
    expect(d.exposureTime).toBeCloseTo(1 / 250, 6);
    expect(d.fNumber).toBeCloseTo(2.8, 6);
    expect(d.iso).toBe(200);
    expect(d.focalLength).toBe(35);
    expect(d.lensModel).toBe("Lens 35mm");
  });

  it("converts GPS degrees/minutes/seconds to signed decimals", () => {
    const north = parseTiff(fullTiff(le)).gps!;
    expect(north.latitude).toBeCloseTo(48 + 51 / 60 + 30 / 3600, 6);
    expect(north.longitude).toBeCloseTo(2 + 17 / 60 + 40.2 / 3600, 6);
    expect(north.altitude).toBe(35);

    const south = parseTiff(fullTiff(le, { latRef: "S", lonRef: "W", altRef: 1 })).gps!;
    expect(south.latitude).toBeCloseTo(-(48 + 51 / 60 + 30 / 3600), 6);
    expect(south.longitude).toBeCloseTo(-(2 + 17 / 60 + 40.2 / 3600), 6);
    expect(south.altitude).toBe(-35);
  });
});

describe("EXIF containers", () => {
  const tiff = fullTiff(true, { orientation: 3 });

  it("finds the block in JPEG APP1", () => {
    const { bytes } = buildJpeg({ exifTiff: tiff });
    expect(parseExif(bytes).orientation).toBe(3);
    expect(readOrientation(bytes)).toBe(3);
    expect(hasNonTrivialOrientation(bytes)).toBe(true);
  });

  it("finds the block in PNG eXIf and WebP EXIF chunks, and a bare TIFF / Exif payload", () => {
    expect(parseExif(buildPng(2, 2, [1, 2, 3, 255], [pngChunk("eXIf", tiff)])).make).toBe("DropFlow Cam");
    expect(parseExif(buildWebp({ exif: true })).make).toBe("DropFlow Cam");
    expect(parseExif(tiff).model).toBe("Model X");
    expect(parseExif(concat(ascii("Exif\0\0"), tiff)).model).toBe("Model X");
    expect(findExifBlock(tiff)).toBe(tiff);
  });

  it("reports nothing for files without EXIF, and orientation 1 is trivial", () => {
    const { bytes } = buildJpeg();
    expect(parseExif(bytes)).toEqual({});
    expect(hasExifData(parseExif(bytes))).toBe(false);
    const one = buildJpeg({ exifTiff: buildTiff(true, [{ tag: 0x0112, type: 3, data: [1] }]) }).bytes;
    expect(readOrientation(one)).toBe(1);
    expect(hasNonTrivialOrientation(one)).toBe(false);
  });

  it("ignores out-of-range orientation values", () => {
    expect(parseTiff(buildTiff(true, [{ tag: 0x0112, type: 3, data: [9] }])).orientation).toBeUndefined();
  });
});

describe("parseExif never throws on garbage or truncated input", () => {
  const full = buildJpeg({ exifTiff: fullTiff(true, { orientation: 6 }) }).bytes;

  it("handles every truncation of a valid file", () => {
    for (let n = 0; n <= full.length; n += 3) expect(() => parseExif(full.subarray(0, n))).not.toThrow();
  });

  it("handles random bytes, empty input and absurd IFD pointers", () => {
    let seed = 12345;
    const rnd = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff) & 255;
    for (let i = 0; i < 200; i++) {
      const junk = new Uint8Array(64 + (i % 50));
      for (let j = 0; j < junk.length; j++) junk[j] = rnd();
      expect(() => parseExif(junk)).not.toThrow();
      // a TIFF-looking header followed by junk
      const tiffish = concat(new Uint8Array([0x49, 0x49, 0x2a, 0, 8, 0, 0, 0]), junk);
      expect(() => parseTiff(tiffish)).not.toThrow();
    }
    expect(parseExif(new Uint8Array())).toEqual({});
    const loop = new Uint8Array([0x49, 0x49, 0x2a, 0, 8, 0, 0, 0, 1, 0, 0x69, 0x87, 4, 0, 1, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0, 0]);
    expect(() => parseTiff(loop)).not.toThrow(); // ExifIFD pointer pointing back at IFD0
  });

  it("returns partial data when only part of the block is intact", () => {
    const tiff = fullTiff(true, { orientation: 8 });
    const partial = tiff.subarray(0, 80); // IFD0 entries are there, values/sub-IFDs are cut off
    const d = parseTiff(partial);
    expect(d.orientation).toBe(8);
    expect(d.gps).toBeUndefined();
  });

  it("drops impossible GPS values", () => {
    const bad = buildTiff(true, [], undefined, [
      { tag: 1, type: 2, data: "N" },
      { tag: 2, type: 5, data: [200, 1, 0, 1, 0, 1] }, // 200 degrees north
      { tag: 3, type: 2, data: "E" },
      { tag: 4, type: 5, data: [10, 1, 0, 0, 0, 1] }, // denominator 0
    ]);
    expect(parseTiff(bad).gps).toBeUndefined();
  });
});

describe("formatReport", () => {
  it("lists the interesting fields, with decimal GPS", () => {
    const report = formatReport({
      name: "a.jpg",
      mime: "image/jpeg",
      size: 1234,
      width: 8,
      height: 6,
      exif: parseTiff(fullTiff(true, { orientation: 6, latRef: "S" })),
      pngText: [],
      hasXmp: true,
    });
    expect(report).toContain("File: a.jpg");
    expect(report).toContain("Dimensions: 8 x 6");
    expect(report).toContain("Make: DropFlow Cam");
    expect(report).toContain("ExposureTime: 1/250 s");
    expect(report).toContain("FNumber: f/2.8");
    expect(report).toContain("FocalLength: 35 mm");
    expect(report).toContain("GPS latitude: -48.858333");
    expect(report).toContain("GPS longitude: 2.294500");
    expect(report).toContain("GPS altitude: 35 m");
    expect(report).toContain("XMP: present");
    expect(report).not.toContain("No metadata");
  });

  it("says so when there is no metadata and formats exposures", () => {
    const report = formatReport({ name: "b.png", mime: "image/png", size: 10, exif: {}, pngText: [], hasXmp: false });
    expect(report).toContain("No metadata");
    expect(formatExposure(0.5)).toBe("1/2 s");
    expect(formatExposure(2)).toBe("2 s");
    expect(formatExposure(1 / 4000)).toBe("1/4000 s");
  });

  it("includes PNG text entries", () => {
    const report = formatReport({
      name: "c.png",
      mime: "image/png",
      size: 10,
      exif: {},
      pngText: [{ keyword: "Author", text: "ليلى  Layla" }],
      hasXmp: false,
    });
    expect(report).toContain("Text Author: ليلى Layla");
  });

  it("webp EXIF chunk with a riff wrapper parses too", () => {
    const body = concat(ascii("WEBP"), riffChunk("EXIF", fullTiff(false)));
    const webp = concat(ascii("RIFF"), u32le(body.length), body);
    expect(parseExif(webp).iso).toBe(200);
  });
});
