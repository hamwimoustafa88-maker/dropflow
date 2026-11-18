import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  cuesToLrc,
  cuesToSbv,
  parseAnySubtitles,
  parseLrc,
  parseMicroDvd,
  parseSami,
  parseSbv,
  parseTtml,
  ttmlTime,
  type AnySubtitleFormat,
} from "../lib/subtitle-formats";
import { decodeText, legacyEncodingFor } from "../lib/text-decode";
import { subtitlesConverter } from "../converters/subtitles";
import { convertSubtitleBytes } from "../converters/subtitles.impl";
import type { Job } from "../types";

// The same sample files the desktop tests read (tests/samples/subtitles), so both apps are held to one reading of each format.
const sample = (name: string) =>
  new Uint8Array(readFileSync(fileURLToPath(new URL(`../../../../tests/samples/subtitles/${name}`, import.meta.url))));
const text = (name: string) => decodeText(sample(name)).text;
const cue = (startMs: number, endMs: number, t: string) => ({ startMs, endMs, text: t });

describe("SAMI", () => {
  it("reads the first language with breaks, entities and clears", () => {
    const r = parseSami(text("sample.smi"));
    expect(r.cues).toEqual([cue(1000, 3500, "Hello <i>world</i>\nsecond line"), cue(4000, 6000, "Fish & chips")]);
    expect(r.languages).toEqual(["enuscc", "arsacc"]);
  });

  it("gives the last caption an end even without a clearing SYNC", () => {
    const r = parseSami("<SAMI><BODY><SYNC Start=500><P>One<SYNC Start=2000><P>مرحبا</BODY></SAMI>");
    expect(r.cues[0]).toEqual(cue(500, 2000, "One"));
    expect(r.cues[1].text).toBe("مرحبا");
    expect(r.cues[1].endMs).toBeGreaterThan(2000);
  });
});

describe("LRC", () => {
  it("ends lines at the next tag, repeats multi-tag lines and strips word times", () => {
    const r = parseLrc(text("sample.lrc"));
    expect(r.title).toBe("DropFlow sample");
    expect(r.cues.slice(0, 3)).toEqual([cue(1000, 3500, "Hello world"), cue(4000, 6000, "Chorus مرحبا"), cue(10000, 12000, "Chorus مرحبا")]);
    expect(r.cues[3].text).toBe("Last line");
  });

  it("applies the offset (positive = earlier) and never goes below zero", () => {
    expect(parseLrc("[offset:+500]\n[00:02.00]A\n[00:04.00]B\n").cues[0]).toEqual(cue(1500, 3500, "A"));
    expect(parseLrc("[offset:-250]\n[00:02.00]A\n[00:04.00]B\n").cues[0]).toEqual(cue(2250, 4250, "A"));
    expect(parseLrc("[offset:5000]\n[00:01.00]A\n[00:03.00]B\n").cues[0].startMs).toBe(0);
  });

  it("writes one line per cue with a clear tag where a gap follows, and reads it back", () => {
    const out = cuesToLrc([cue(1000, 3000, "Hello <i>world</i>\nagain"), cue(3000, 4000, "مرحبا"), cue(125_500, 127_000, "late")]);
    expect(out).toBe("[00:01.00]Hello world again\n[00:03.00]مرحبا\n[00:04.00]\n[02:05.50]late\n[02:07.00]\n");
    expect(parseLrc(out).cues.slice(1)).toEqual([cue(3000, 4000, "مرحبا"), cue(125_500, 127_000, "late")]);
  });
});

describe("TTML / DFXP", () => {
  it("reads clock, tick and frame times and head styles", () => {
    expect(parseTtml(text("sample.ttml")).cues).toEqual([
      cue(1000, 3500, "Hello <b>world</b>\nsecond line"),
      cue(4000, 6000, "<i>مرحبا بالعالم</i>"),
      cue(7480, 9000, "Frames & ticks"),
    ]);
  });

  it("makes times relative to the enclosing div", () => {
    expect(parseTtml(text("sample.dfxp")).cues).toEqual([
      cue(10000, 11500, "First, inside a div that starts at ten seconds"),
      cue(12000, 14000, "<i>مرحبا</i> again"),
    ]);
  });

  it("ignores a DOCTYPE with entities instead of expanding it", () => {
    const xml = '<?xml version="1.0"?><!DOCTYPE tt [ <!ENTITY e SYSTEM "file:///c:/windows/win.ini"> ]><tt xmlns="http://www.w3.org/ns/ttml"><body><div><p begin="1s" end="2s">ok &amp; safe &e;</p></div></body></tt>';
    expect(parseTtml(xml).cues).toEqual([cue(1000, 2000, "ok & safe &e;")]);
  });

  it.each([
    ["01:02:03.5", 3723500],
    ["00:00:01,250", 1250],
    ["1.5s", 1500],
    ["2m", 120000],
    ["1h", 3600000],
    ["750ms", 750],
    ["50f", 2000],
    ["00:00:02:05", 2200],
    ["5000000t", 500],
  ])("time %s is %d ms", (expression, ms) => {
    expect(ttmlTime(expression, { frameRate: 25, subFrameRate: 1, tickRate: 10_000_000 })).toBe(ms);
  });
});

describe("MicroDVD and SBV", () => {
  it("uses the declared frame rate and the style codes", () => {
    const r = parseMicroDvd(text("sample.sub"));
    expect(r.declared).toBe(true);
    expect(r.cues).toEqual([
      cue(1000, 3000, "Hello world\nsecond line"),
      cue(4000, 6000, "<i>مرحبا بالعالم</i>"),
      cue(7000, 9000, "<b>Bold</b>\n<b>both lines</b>"),
      cue(10000, 12000, "<i>Slash italic</i>\nplain"),
    ]);
  });

  it("assumes 23.976 fps without a rate line", () => {
    const r = parseMicroDvd("{24}{48}One\n{1000}{}Two\n");
    expect(r.declared).toBe(false);
    expect(r.cues[0]).toEqual(cue(1001, 2002, "One"));
    expect(r.cues[1].startMs).toBe(41708);
  });

  it("reads YouTube SBV and SubViewer files named .sub", () => {
    expect(parseSbv(text("sample.sbv")).cues).toEqual([
      cue(1000, 3500, "Hello world\nsecond line"),
      cue(4000, 6250, "مرحبا بالعالم"),
      cue(62500, 64000, "Fish & chips"),
    ]);
    const sub = parseAnySubtitles("[INFORMATION]\n[TITLE]x\n[END INFORMATION]\n00:00:01.00,00:00:02.50\nHello[br]there\n\n", "sub");
    expect(sub.cues).toEqual([cue(1000, 2500, "Hello\nthere")]);
  });

  it("writes SBV that reads back", () => {
    const out = cuesToSbv([cue(1000, 2500, "<b>Two</b>\nlines"), cue(3_723_004, 3_725_000, "مرحبا")]);
    expect(out).toBe("0:00:01.000,0:00:02.500\nTwo\nlines\n\n1:02:03.004,1:02:05.000\nمرحبا\n\n");
    expect(parseSbv(out).cues).toEqual([cue(1000, 2500, "Two\nlines"), cue(3_723_004, 3_725_000, "مرحبا")]);
  });
});

describe("encoding detection", () => {
  it("decodes the Arabic windows-1256 sample", () => {
    const r = decodeText(sample("arabic-cp1256.srt"));
    expect(r.encoding).toBe("windows-1256");
    expect(r.text).toContain("مرحباً بكم في الفيلم");
  });

  it("keeps Western windows-1252 text Western", () => {
    // "Ça va très bien" with Ç = C7, è = E8
    const bytes = Uint8Array.from([0xc7, 0x61, 0x20, 0x76, 0x61, 0x20, 0x74, 0x72, 0xe8, 0x73, 0x20, 0x62, 0x69, 0x65, 0x6e]);
    expect(decodeText(bytes)).toEqual({ text: "Ça va très bien", encoding: "windows-1252" });
  });

  it("falls back to the legacy code page of the language for double-byte text", () => {
    // "안녕하세요" in EUC-KR
    const korean = Uint8Array.from([0xbe, 0xc8, 0xb3, 0xe7, 0xc7, 0xcf, 0xbc, 0xbc, 0xbf, 0xe4]);
    expect(decodeText(korean, { language: "ko-KR" })).toEqual({ text: "안녕하세요", encoding: "euc-kr" });
    expect(decodeText(korean, { language: "en-US" }).encoding).toBe("windows-1252");
    expect(legacyEncodingFor("zh-TW")).toBe("big5");
    expect(legacyEncodingFor("ar-SA")).toBe("windows-1256");
    expect(legacyEncodingFor("en")).toBeNull();
  });
});

describe("subtitles converter with the new formats", () => {
  const job = (op: string, file: File): Job => ({ id: "j", operation: op, files: [file], options: {} });
  const signal = () => new AbortController().signal;

  it("advertises every new input for srt, vtt, txt, lrc and sbv but never itself", () => {
    expect(subtitlesConverter.operations).toEqual(["convert:srt", "convert:vtt", "convert:txt", "convert:lrc", "convert:sbv"]);
    for (const ext of ["smi", "sami", "lrc", "ttml", "dfxp", "sub", "sbv"])
      for (const t of ["srt", "vtt", "txt", "lrc", "sbv"])
        expect(subtitlesConverter.supports(`convert:${t}`, ext)).toBe(t !== ext);
    expect(subtitlesConverter.supports("convert:smi", "srt")).toBe(false);
  });

  it.each(["sample.smi", "sample.lrc", "sample.ttml", "sample.dfxp", "sample.sub", "sample.sbv", "arabic-cp1256.srt"])(
    "%s converts to every output as UTF-8",
    async (name) => {
      for (const target of ["srt", "vtt", "txt", "lrc", "sbv"]) {
        const ext = name.slice(name.lastIndexOf(".") + 1);
        if (target === ext) continue;
        const [out] = await subtitlesConverter.run(job(`convert:${target}`, new File([sample(name)], name)), () => {}, signal());
        const bytes = new Uint8Array(await out.arrayBuffer());
        expect(() => new TextDecoder("utf-8", { fatal: true }).decode(bytes)).not.toThrow();
        if (target !== "txt") expect(parseAnySubtitles(await out.text(), target as AnySubtitleFormat).cues.length).toBeGreaterThan(0);
        if (name.includes("cp1256")) expect(await out.text()).toContain("شكراً لكم");
      }
    },
  );

  it("reports the detected encoding", () => {
    expect(convertSubtitleBytes(sample("arabic-cp1256.srt"), "a.srt", "vtt").encoding).toBe("windows-1256");
  });
});
