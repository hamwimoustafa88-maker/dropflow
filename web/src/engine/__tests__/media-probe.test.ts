import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import {
  CORE_VERSION,
  buildMediaCaps,
  classifyFfmpegLog,
  isValidMediaCaps,
  parseDecoders,
  parseDemuxers,
  parseEncoders,
  parseFilters,
  parseMuxers,
} from "../lib/ffmpeg-probe";

// Trimmed captures of the REAL output of `ffmpeg -encoders/-decoders/-muxers/-demuxers/-filters` from @ffmpeg/core 0.12.10.
const fx = (name: string) => readFileSync(new URL(`./fixtures/ffmpeg-${name}.txt`, import.meta.url), "utf8");

describe("probe parsers (real core output)", () => {
  it("parses encoders and ignores the legend rows", () => {
    const e = parseEncoders(fx("encoders"));
    for (const n of ["libx264", "libx265", "libvpx", "libvpx-vp9", "libmp3lame", "libopus", "libvorbis", "aac", "flac", "pcm_s16le", "pcm_s16be", "wmav2", "wmv2", "mpeg4", "gif", "png"]) {
      expect(e, n).toContain(n);
    }
    expect(e).not.toContain("=");
    expect(e).not.toContain("Video");
    expect(e.length).toBeGreaterThan(25);
  });

  it("parses decoders", () => {
    const d = parseDecoders(fx("decoders"));
    for (const n of ["h264", "vp8", "vp9", "aac", "mp3", "mp3float", "flac", "opus", "vorbis", "wmav2", "gif", "pcm_s16le"]) {
      expect(d, n).toContain(n);
    }
    expect(d).not.toContain("=");
  });

  it("parses muxers (E flag) and does not list demux-only rows", () => {
    const m = parseMuxers(fx("muxers"));
    for (const n of ["mp3", "ipod", "mp4", "wav", "flac", "ogg", "opus", "aiff", "asf", "adts", "matroska", "webm", "avi", "gif", "mov", "image2"]) {
      expect(m, n).toContain(n);
    }
    expect(m).not.toContain("Muxing");
    expect(m).not.toContain("=");
  });

  it("parses demuxers and flattens comma lists (mov,mp4,m4a,... / matroska,webm)", () => {
    const d = parseDemuxers(fx("demuxers"));
    for (const n of ["mp3", "mov", "mp4", "m4a", "3gp", "matroska", "webm", "wav", "flac", "ogg", "aiff", "asf", "aac", "avi", "gif", "flv"]) {
      expect(d, n).toContain(n);
    }
    expect(d.some((n) => n.includes(","))).toBe(false);
  });

  it("parses filters (name followed by an io signature)", () => {
    const f = parseFilters(fx("filters"));
    for (const n of ["loudnorm", "atempo", "setpts", "palettegen", "paletteuse", "scale", "fps", "split", "aresample"]) {
      expect(f, n).toContain(n);
    }
    expect(f).not.toContain("=");
    expect(f).not.toContain("Timeline");
  });

  it("builds MediaCaps and the result validates", () => {
    const caps = buildMediaCaps(CORE_VERSION, {
      encoders: fx("encoders"),
      decoders: fx("decoders"),
      muxers: fx("muxers"),
      demuxers: fx("demuxers"),
      filters: fx("filters"),
    });
    expect(caps.coreVersion).toBe(CORE_VERSION);
    expect(isValidMediaCaps(caps)).toBe(true);
    expect(isValidMediaCaps(JSON.parse(JSON.stringify(caps)))).toBe(true);
  });

  it("handles CRLF text and empty text", () => {
    expect(parseEncoders(fx("encoders").replace(/\n/g, "\r\n"))).toContain("libx264");
    expect(parseEncoders("")).toEqual([]);
    expect(parseMuxers("garbage\nmore garbage")).toEqual([]);
  });
});

describe("isValidMediaCaps (never trust localStorage)", () => {
  const ok = { coreVersion: "1", encoders: ["a"], decoders: ["a"], muxers: ["a"], demuxers: ["a"], filters: ["a"] };
  it("accepts a complete object", () => expect(isValidMediaCaps(ok)).toBe(true));
  it.each([
    [null],
    ["x"],
    [{}],
    [{ ...ok, coreVersion: 1 }],
    [{ ...ok, encoders: [] }],
    [{ ...ok, decoders: [1] }],
    [{ ...ok, filters: "a" }],
    [{ coreVersion: "1" }],
  ])("rejects %j", (v) => expect(isValidMediaCaps(v)).toBe(false));
});

describe("CORE_VERSION", () => {
  it("equals the installed @ffmpeg/core version (so the cache key and /vendor stay in sync)", () => {
    const pkg = JSON.parse(readFileSync(new URL("../../../node_modules/@ffmpeg/core/package.json", import.meta.url), "utf8")) as { version: string };
    expect(CORE_VERSION).toBe(pkg.version);
  });
});

describe("classifyFfmpegLog", () => {
  it("noAudio", () => {
    expect(classifyFfmpegLog(["Stream map '0:a:0' matches no streams.", "To ignore this, add a trailing '?' to the map."])).toBe("noAudio");
    expect(classifyFfmpegLog(["Output file #0 does not contain any stream"])).toBe("noAudio");
  });
  it("decodeFailed", () => {
    expect(classifyFfmpegLog(["[mov,mp4,m4a,3gp,3g2,mj2 @ 0xdeecf0] moov atom not found", "in0.mp4: Invalid data found when processing input"])).toBe("decodeFailed");
    expect(classifyFfmpegLog(["[mp3 @ 0x1] Failed to read frame size: Could not seek to 5119.", "in0.mp3: Invalid argument"])).toBe("decodeFailed");
    expect(classifyFfmpegLog(["in0.wav: End of file"])).toBe("decodeFailed");
  });
  it("outOfMemory", () => {
    expect(classifyFfmpegLog(["Cannot enlarge memory arrays"])).toBe("outOfMemory");
    expect(classifyFfmpegLog(["RuntimeError: memory access out of bounds"])).toBe("outOfMemory");
  });
  it("falls back to encodeFailed", () => {
    expect(classifyFfmpegLog(["Error initializing output stream 0:0 -- Error while opening encoder"])).toBe("encodeFailed");
    expect(classifyFfmpegLog([])).toBe("encodeFailed");
  });
});
