import { describe, expect, it } from "vitest";
import catalog from "../../data/catalog.json";
import { subtitlesConverter } from "../converters/subtitles";
import { convertSubtitleBytes } from "../converters/subtitles.impl";
import { ConvertError, type Job } from "../types";

const ar = "مرحباً بالعالم ‏(اختبار)";
const srt = `1\r\n00:00:01,000 --> 00:00:02,500\r\nHello\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\n${ar}\r\n\r\n`;

function job(op: string, file: File): Job {
  return { id: "j1", operation: op, files: [file], options: {} };
}
const noop = () => {};
const signal = () => new AbortController().signal;

describe("subtitlesConverter manifest", () => {
  it("only offers srt, vtt and txt targets (no ass, no self-conversion)", () => {
    expect(subtitlesConverter.operations).toEqual(["convert:srt", "convert:vtt", "convert:txt"]);
    for (const op of subtitlesConverter.operations) {
      expect(subtitlesConverter.options(op)).toEqual([]);
      expect(subtitlesConverter.suffix(op)).toBe("converted");
      expect(subtitlesConverter.runtime(op, "srt")).toBe("worker");
    }
    expect(subtitlesConverter.supports("convert:srt", "srt")).toBe(false);
    expect(subtitlesConverter.supports("convert:vtt", "vtt")).toBe(false);
    expect(subtitlesConverter.supports("convert:vtt", "srt")).toBe(true);
    expect(subtitlesConverter.supports("convert:txt", "srt")).toBe(true);
    expect(subtitlesConverter.supports("convert:srt", "vtt")).toBe(true);
    expect(subtitlesConverter.supports("convert:txt", "vtt")).toBe(true);
    expect(subtitlesConverter.supports("convert:srt", "ass")).toBe(true);
    expect(subtitlesConverter.supports("convert:vtt", "ssa")).toBe(true);
    expect(subtitlesConverter.supports("convert:txt", "ASS")).toBe(true);
    expect(subtitlesConverter.supports("convert:ass", "srt")).toBe(false);
    expect(subtitlesConverter.supports("convert:srt", "txt")).toBe(false);
    expect(subtitlesConverter.supports("convert:srt", "pdf")).toBe(false);
    expect(subtitlesConverter.supports("convert:srt", "")).toBe(false);
    expect(subtitlesConverter.supports("sub.something", "srt")).toBe(false);
  });

  it("every convert target is an output of the catalog's subtitle category and the category is a web one", () => {
    const category = catalog.categories.find((c) => c.id === "subtitle")!;
    expect(category.platforms).toContain("web");
    for (const op of subtitlesConverter.operations) expect(category.outputs).toContain(op.slice("convert:".length));
    for (const ext of ["srt", "vtt", "ass"]) expect(category.inputs).toContain(ext);
  });
});

describe("subtitlesConverter.run", () => {
  it("srt -> vtt keeps Arabic exactly and names the output after the input", async () => {
    const input = new File([srt], "فيلم.srt", { type: "application/x-subrip" });
    const [out] = await subtitlesConverter.run(job("convert:vtt", input), noop, signal());
    expect(out.name).toBe("فيلم.vtt");
    expect(out.type).toBe("text/vtt;charset=utf-8");
    const text = await out.text();
    expect(text.startsWith("WEBVTT\n\n00:00:01.000 --> 00:00:02.500\nHello")).toBe(true);
    expect(text).toContain(ar);
  });

  it("vtt -> srt -> txt chain, UTF-8 BOM and CRLF input", async () => {
    const vtt = new File(["﻿WEBVTT\r\n\r\n00:01.000 --> 00:02.000\r\nمرحباً\r\n"], "a.vtt");
    const [asSrt] = await subtitlesConverter.run(job("convert:srt", vtt), noop, signal());
    expect(await asSrt.text()).toBe("1\r\n00:00:01,000 --> 00:00:02,000\r\nمرحباً\r\n\r\n");
    expect(asSrt.type).toBe("text/plain;charset=utf-8");
    const [asTxt] = await subtitlesConverter.run(job("convert:txt", asSrt), noop, signal());
    expect(asTxt.name).toBe("a.txt");
    expect(await asTxt.text()).toBe("مرحباً\n");
  });

  it("decodes legacy windows-1256 input", async () => {
    // "مرحبا" in windows-1256 on a subtitle line
    const header = new TextEncoder().encode("1\r\n00:00:01,000 --> 00:00:02,000\r\n");
    const body = Uint8Array.from([0xe3, 0xd1, 0xcd, 0xc8, 0xc7, 0x0d, 0x0a]);
    const bytes = new Uint8Array(header.length + body.length);
    bytes.set(header);
    bytes.set(body, header.length);
    const [out] = await subtitlesConverter.run(job("convert:txt", new File([bytes], "old.srt")), noop, signal());
    expect(await out.text()).toBe("مرحبا\n");
  });

  it("ASS input", async () => {
    const ass = "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,D,,0,0,0,,{\\an8}Top\\Nline\n";
    const [out] = await subtitlesConverter.run(job("convert:srt", new File([ass], "x.ass")), noop, signal());
    expect(await out.text()).toBe("1\r\n00:00:01,000 --> 00:00:02,000\r\nTop\r\nline\r\n\r\n");
  });

  it("content beats the extension (a VTT saved as .srt)", async () => {
    const [out] = await subtitlesConverter.run(job("convert:txt", new File(["WEBVTT\n\n00:00.000 --> 00:01.000\nTom &amp; Jerry\n"], "mislabelled.srt")), noop, signal());
    expect(await out.text()).toBe("Tom & Jerry\n");
  });

  it("throws ConvertError('noCues') for empty or non-subtitle files", async () => {
    for (const content of ["", "just some prose\nwith lines\n", "﻿", "1\n00:00:01,000 --> garbage\ntext\n"]) {
      const p = subtitlesConverter.run(job("convert:vtt", new File([content], "bad.srt")), noop, signal());
      await expect(p).rejects.toBeInstanceOf(ConvertError);
      await expect(p).rejects.toMatchObject({ code: "noCues" });
    }
  });

  it("reports progress and honours cancellation", async () => {
    const seen: number[] = [];
    await subtitlesConverter.run(job("convert:vtt", new File([srt], "a.srt")), (f) => seen.push(f), signal());
    expect(seen.at(-1)).toBe(1);
    const ac = new AbortController();
    ac.abort();
    await expect(subtitlesConverter.run(job("convert:vtt", new File([srt], "a.srt")), noop, ac.signal)).rejects.toMatchObject({ name: "AbortError" });
  });

  it("does not modify the input file", async () => {
    const input = new File([srt], "keep.srt");
    const before = await input.text();
    await subtitlesConverter.run(job("convert:vtt", input), noop, signal());
    expect(await input.text()).toBe(before);
    expect(input.name).toBe("keep.srt");
  });

  it("convertSubtitleBytes reports how many cues and skipped blocks", () => {
    const bytes = new TextEncoder().encode(`${srt}\r\nbroken --> block\r\ntext\r\n`);
    const r = convertSubtitleBytes(bytes, "a.srt", "vtt");
    expect(r.cues).toBe(2);
    expect(r.skipped).toBe(1);
  });
});
