import { describe, expect, it } from "vitest";
import {
  cleanCueText,
  cuesToSrt,
  cuesToTxt,
  cuesToVtt,
  decodeEntities,
  detectFormat,
  escapeVtt,
  formatTimestamp,
  normalizeNewlines,
  parseAss,
  parseSrt,
  parseSubtitles,
  parseTimestamp,
  parseVtt,
  type Cue,
} from "../lib/subtitles";

const AR_LINE = "مرحباً بالعالم ‏(اختبار)‌ نهاية";

const SRT = [
  "1",
  "00:00:01,000 --> 00:00:02,500",
  "Hello, world",
  "",
  "2",
  "00:00:03,000 --> 00:00:05,250",
  AR_LINE,
  "سطر ثانٍ",
  "",
  "3",
  "01:02:03,004 --> 01:02:04,999",
  "<i>Italic</i> and <b>bold</b>",
  "",
].join("\n");

describe("parseTimestamp / formatTimestamp", () => {
  it("parses SRT and VTT styles exactly to the millisecond", () => {
    expect(parseTimestamp("00:00:01,000")).toBe(1000);
    expect(parseTimestamp("01:02:03,456")).toBe(3_723_456);
    expect(parseTimestamp("01:02:03.456")).toBe(3_723_456);
    expect(parseTimestamp("02:03.456")).toBe(123_456);
    expect(parseTimestamp("1:02:03")).toBe(3_723_000);
    expect(parseTimestamp("00:00:00,001")).toBe(1);
    expect(parseTimestamp("100:00:00.000")).toBe(360_000_000);
  });

  it("treats 1-3 digit fractions as fractions of a second", () => {
    expect(parseTimestamp("00:00:01,5")).toBe(1500);
    expect(parseTimestamp("00:00:01,50")).toBe(1500);
    expect(parseTimestamp("00:00:01,05")).toBe(1050);
    expect(parseTimestamp("00:00:01.050")).toBe(1050);
    expect(parseTimestamp("00:00:01.0005")).toBe(1001); // 4th digit rounds
    expect(parseTimestamp("00:00:01.0004")).toBe(1000);
    expect(parseTimestamp("00:00:01.9995")).toBe(2000);
  });

  it("rejects garbage", () => {
    for (const bad of ["", "abc", "00:00:", "00:61:00", "00:00:61", "1:2:3:4", "00:00:01,", "-00:00:01", "00:00:01 ,000"]) {
      expect(parseTimestamp(bad), bad).toBeNull();
    }
  });

  it("formats with the requested separator and pads", () => {
    expect(formatTimestamp(0, ",")).toBe("00:00:00,000");
    expect(formatTimestamp(3_723_456, ".")).toBe("01:02:03.456");
    expect(formatTimestamp(360_000_001, ",")).toBe("100:00:00,001");
    expect(formatTimestamp(-5, ",")).toBe("00:00:00,000");
    expect(formatTimestamp(1234.6, ",")).toBe("00:00:01,235");
  });

  it("round-trips every millisecond value in a range", () => {
    for (let ms = 0; ms < 200_000; ms += 997) {
      expect(parseTimestamp(formatTimestamp(ms, ","))).toBe(ms);
      expect(parseTimestamp(formatTimestamp(ms, "."))).toBe(ms);
    }
  });
});

describe("normalizeNewlines", () => {
  it("strips BOM and unifies line endings", () => {
    expect(normalizeNewlines("﻿a\r\nb\rc\nd")).toBe("a\nb\nc\nd");
  });
});

describe("parseSrt", () => {
  it("parses a normal file (Arabic, RTL marks, ZWNJ, tags)", () => {
    const { cues, skipped } = parseSrt(SRT);
    expect(skipped).toBe(0);
    expect(cues).toHaveLength(3);
    expect(cues[0]).toEqual({ startMs: 1000, endMs: 2500, text: "Hello, world" });
    expect(cues[1].text).toBe(`${AR_LINE}\nسطر ثانٍ`);
    expect(cues[1].text).toContain("‏");
    expect(cues[1].text).toContain("‌");
    expect(cues[2]).toEqual({ startMs: 3_723_004, endMs: 3_724_999, text: "<i>Italic</i> and <b>bold</b>" });
  });

  it("handles CRLF, CR and mixed line endings and a UTF-8 BOM", () => {
    const crlf = parseSrt("﻿" + SRT.replace(/\n/g, "\r\n"));
    const cr = parseSrt(SRT.replace(/\n/g, "\r"));
    const mixed = parseSrt(SRT.replace(/\n(?=\d{2}:)/g, "\r\n"));
    const base = parseSrt(SRT);
    expect(crlf).toEqual(base);
    expect(cr).toEqual(base);
    expect(mixed).toEqual(base);
  });

  it("works without index lines and with '.' separators and extra spaces", () => {
    const text = "00:00:01.000   -->   00:00:02.000\nOne\n\n00:00:03.5 --> 00:00:04.25\nTwo\nlines\n";
    const { cues, skipped } = parseSrt(text);
    expect(skipped).toBe(0);
    expect(cues).toEqual([
      { startMs: 1000, endMs: 2000, text: "One" },
      { startMs: 3500, endMs: 4250, text: "Two\nlines" },
    ]);
  });

  it("accepts '-->' without spaces and trailing coordinates", () => {
    const { cues } = parseSrt("1\n00:00:01,000-->00:00:02,000 X1:100 X2:200 Y1:10 Y2:20\nhi\n");
    expect(cues).toEqual([{ startMs: 1000, endMs: 2000, text: "hi" }]);
  });

  it("skips blocks with a bad timestamp or no timestamp and counts them", () => {
    const text = [
      "1",
      "00:00:01,000 --> 00:00:02,000",
      "good",
      "",
      "2",
      "garbage --> 00:00:04,000",
      "bad start",
      "",
      "3",
      "00:00:05,000 --> ",
      "bad end",
      "",
      "just a stray paragraph",
      "",
      "4",
      "00:00:07,000 --> 00:00:08,000",
      "good again",
    ].join("\n");
    const { cues, skipped } = parseSrt(text);
    expect(cues.map((c) => c.text)).toEqual(["good", "good again"]);
    expect(skipped).toBe(3);
  });

  it("clamps end < start and keeps overlapping cues in order", () => {
    const { cues } = parseSrt("1\n00:00:05,000 --> 00:00:03,000\nbackwards\n\n2\n00:00:04,000 --> 00:00:09,000\noverlap\n");
    expect(cues).toEqual([
      { startMs: 5000, endMs: 5000, text: "backwards" },
      { startMs: 4000, endMs: 9000, text: "overlap" },
    ]);
  });

  it("drops cues with empty text silently", () => {
    const { cues, skipped } = parseSrt("1\n00:00:01,000 --> 00:00:02,000\n\n\n2\n00:00:03,000 --> 00:00:04,000\nreal\n\n3\n00:00:05,000 --> 00:00:06,000\n<i> </i>\n");
    expect(cues.map((c) => c.text)).toEqual(["real", "<i> </i>"]);
    expect(skipped).toBe(0);
  });

  it("returns zero cues for empty or non-subtitle input", () => {
    expect(parseSrt("").cues).toEqual([]);
    expect(parseSrt("﻿").cues).toEqual([]);
    expect(parseSrt("hello\nworld").cues).toEqual([]);
  });

  it("strips ASS override blocks and font tags but keeps b/i/u, <br> becomes a newline", () => {
    const { cues } = parseSrt('1\n00:00:01,000 --> 00:00:02,000\n{\\an8}<font color="#ff0000">Top</font><br>line <I>two</I>\n');
    expect(cues[0].text).toBe("Top\nline <i>two</i>");
  });

  it("does not decode entities (SRT has none) and keeps a literal less-than sign", () => {
    const { cues } = parseSrt("1\n00:00:01,000 --> 00:00:02,000\nTom &amp; Jerry, 1 < 2\n");
    expect(cues[0].text).toBe("Tom &amp; Jerry, 1 < 2");
  });

  it("keeps the speaker dash and does not mangle numbers that look like indexes", () => {
    const { cues } = parseSrt("12\n00:00:01,000 --> 00:00:02,000\n- 5\n- 6\n");
    expect(cues[0].text).toBe("- 5\n- 6");
  });
});

describe("parseVtt", () => {
  const VTT = [
    "WEBVTT - some title",
    "",
    "NOTE this is a comment",
    "with --> arrow inside",
    "",
    "STYLE",
    "::cue { color: red }",
    "",
    "REGION",
    "id:r1",
    "",
    "intro",
    "00:01.000 --> 00:02.500 position:10% align:start",
    "<v Speaker>Hello &amp; welcome</v>",
    "",
    "00:00:03.000 --> 00:00:04.000",
    "<c.yellow>colour</c> <00:00:03.500>timed &lt;b&gt; &nbsp;x",
    "",
  ].join("\r\n");

  it("skips NOTE/STYLE/REGION blocks and the header, accepts ids, settings, short times", () => {
    const { cues, skipped } = parseVtt(VTT);
    expect(skipped).toBe(0);
    expect(cues).toEqual([
      { startMs: 1000, endMs: 2500, text: "Hello & welcome" },
      { startMs: 3000, endMs: 4000, text: "colour timed <b>  x" },
    ]);
  });

  it("decodes named and numeric entities", () => {
    expect(decodeEntities("&amp; &lt; &gt; &quot; &#39; &#x41; &nbsp;|&rlm;&lrm;")).toBe("& < > \" ' A  |‏‎");
    expect(decodeEntities("&unknown; &#0; &#xD800;")).toBe("&unknown; &#0; &#xD800;");
    expect(decodeEntities("&amp;lt;")).toBe("&lt;");
  });

  it("handles a header glued to the first cue", () => {
    const { cues, skipped } = parseVtt("WEBVTT\n00:00:01.000 --> 00:00:02.000\nglued\n");
    expect(cues).toEqual([{ startMs: 1000, endMs: 2000, text: "glued" }]);
    expect(skipped).toBe(0);
  });

  it("counts unusable blocks as skipped", () => {
    const { cues, skipped } = parseVtt("WEBVTT\n\nnot a cue\n\n00:00:01.000 --> nope\ntext\n\n00:00:02.000 --> 00:00:03.000\nok\n");
    expect(cues).toHaveLength(1);
    expect(skipped).toBe(2);
  });

  it("keeps Arabic exactly, including RTL marks", () => {
    const { cues } = parseVtt(`WEBVTT\n\n00:00.000 --> 00:01.000\n${AR_LINE}\n`);
    expect(cues[0].text).toBe(AR_LINE);
  });
});

describe("parseAss", () => {
  const ASS = [
    "[Script Info]",
    "Title: test",
    "",
    "[V4+ Styles]",
    "Format: Name, Fontname",
    "Style: Default,Arial",
    "",
    "[Events]",
    "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text",
    "Dialogue: 0,0:00:05.00,0:00:06.50,Default,,0,0,0,,Second {\\i1}line{\\i0}, with comma",
    "Comment: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,ignored",
    "Dialogue: 0,0:00:01.00,0:00:02.50,Default,,0,0,0,,{\\an8}First\\Nline two\\nthree",
    "Dialogue: 0,bad,0:00:03.00,Default,,0,0,0,,broken",
    `Dialogue: 0,0:00:07.00,0:00:08.00,Default,,0,0,0,,${AR_LINE}`,
    "",
  ].join("\r\n");

  it("parses Dialogue lines by the Format column order, sorts by time, strips tags", () => {
    const { cues, skipped } = parseAss(ASS);
    expect(skipped).toBe(1);
    expect(cues).toEqual([
      { startMs: 1000, endMs: 2500, text: "First\nline two\nthree" },
      { startMs: 5000, endMs: 6500, text: "Second line, with comma" },
      { startMs: 7000, endMs: 8000, text: AR_LINE },
    ]);
  });

  it("honours a different column order and works without a Format line", () => {
    const reordered = "[Events]\nFormat: Start, End, Text\nDialogue: 0:00:01.00,0:00:02.00,hi, there\n";
    expect(parseAss(reordered).cues).toEqual([{ startMs: 1000, endMs: 2000, text: "hi, there" }]);
    const noFormat = "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,plain\n";
    expect(parseAss(noFormat).cues).toEqual([{ startMs: 1000, endMs: 2000, text: "plain" }]);
  });
});

describe("detectFormat / parseSubtitles", () => {
  it("detects by content", () => {
    expect(detectFormat("﻿WEBVTT\n")).toBe("vtt");
    expect(detectFormat("[Script Info]\nTitle: x")).toBe("ass");
    expect(detectFormat("1\n00:00:01,000 --> 00:00:02,000\nhi")).toBe("srt");
    expect(detectFormat("00:01.000 --> 00:02.000\nhi")).toBe("srt");
    expect(detectFormat("just text")).toBe("unknown");
  });

  it("content wins over the extension hint when the signature is certain", () => {
    expect(parseSubtitles("WEBVTT\n\n00:00.000 --> 00:01.000\nx &amp; y\n", "srt").cues[0].text).toBe("x & y");
    expect(parseSubtitles("1\n00:00:01,000 --> 00:00:02,000\nx &amp; y\n", "srt").cues[0].text).toBe("x &amp; y");
    expect(parseSubtitles("00:00.000 --> 00:01.000\nx &amp; y\n", "vtt").cues[0].text).toBe("x & y");
    expect(parseSubtitles("Dialogue: 0,0:00:01.00,0:00:02.00,D,,0,0,0,,x\n", "srt").cues[0].text).toBe("x");
  });
});

describe("writers", () => {
  const cues: Cue[] = [
    { startMs: 1000, endMs: 2500, text: "Hello, world" },
    { startMs: 3000, endMs: 5250, text: `${AR_LINE}\nسطر ثانٍ` },
    { startMs: 3_723_004, endMs: 3_724_999, text: "<i>a</i> & <b>b</b> 1 < 2 -->" },
  ];

  it("cuesToSrt uses CRLF, 1-based indexes and comma times", () => {
    const srt = cuesToSrt(cues);
    expect(srt.startsWith("1\r\n00:00:01,000 --> 00:00:02,500\r\nHello, world\r\n\r\n2\r\n")).toBe(true);
    expect(srt.endsWith("\r\n\r\n")).toBe(true);
    expect(srt).not.toMatch(/[^\r]\n/);
    expect(srt).toContain("01:02:03,004 --> 01:02:04,999");
  });

  it("cuesToVtt uses a header, LF and dot times and escapes text", () => {
    const vtt = cuesToVtt(cues);
    expect(vtt.startsWith("WEBVTT\n\n00:00:01.000 --> 00:00:02.500\nHello, world\n\n")).toBe(true);
    expect(vtt).not.toContain("\r");
    expect(vtt).toContain("<i>a</i> &amp; <b>b</b> 1 &lt; 2 --&gt;");
    expect(escapeVtt("<b>x</b> <u>y</u> <c>z</c>")).toBe("<b>x</b> <u>y</u> &lt;c&gt;z&lt;/c&gt;");
  });

  it("cuesToTxt drops tags and timing, separates cues with a blank line", () => {
    expect(cuesToTxt(cues)).toBe(`Hello, world\n\n${AR_LINE}\nسطر ثانٍ\n\na & b 1 < 2 -->\n`);
    expect(cuesToTxt([])).toBe("");
  });

  it("srt -> vtt -> srt is lossless (Arabic + English + tags + special characters)", () => {
    const srt = cuesToSrt(cues);
    const viaVtt = cuesToSrt(parseVtt(cuesToVtt(parseSrt(srt).cues)).cues);
    expect(viaVtt).toBe(srt);
    expect(parseVtt(cuesToVtt(cues)).cues).toEqual(cues);
  });

  it("vtt -> srt -> vtt is lossless for canonical files", () => {
    const vtt = cuesToVtt(cues);
    expect(cuesToVtt(parseSrt(cuesToSrt(parseVtt(vtt).cues)).cues)).toBe(vtt);
  });

  it("round-trips a long generated Arabic/English file", () => {
    const many: Cue[] = Array.from({ length: 500 }, (_, i) => ({
      startMs: i * 1700 + 3,
      endMs: i * 1700 + 1500 + (i % 7),
      text: i % 2 ? `السطر رقم ${i}‏\nline ${i}` : `Line ${i} - ${AR_LINE}`,
    }));
    const srt = cuesToSrt(many);
    expect(parseSrt(srt).cues).toEqual(many);
    expect(parseVtt(cuesToVtt(parseSrt(srt).cues)).cues).toEqual(many);
  });
});

describe("cleanCueText", () => {
  it("never leaves blank lines inside a cue", () => {
    expect(cleanCueText("a<br><br>b\n\n\nc", false)).toBe("a\nb\nc");
    expect(cleanCueText("  \n x \n  ", false)).toBe("x");
  });
});
