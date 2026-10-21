import { describe, expect, it } from "vitest";
import { ConvertError } from "../types";
import { formatFfmpegTime, normalizeDigits, parseTime, parseTimeRange } from "../lib/media-time";

describe("normalizeDigits", () => {
  it("maps Arabic-Indic and Persian digits, Arabic decimal separator and full-width colon", () => {
    expect(normalizeDigits("٠١٢٣٤٥٦٧٨٩")).toBe("0123456789");
    expect(normalizeDigits("۰۱۲۳۴۵۶۷۸۹")).toBe("0123456789");
    expect(normalizeDigits("١٫٥")).toBe("1.5");
    expect(normalizeDigits("١：٣٠")).toBe("1:30");
    expect(normalizeDigits("abc 12")).toBe("abc 12");
  });
});

describe("parseTime", () => {
  it.each([
    ["90", 90],
    ["0", 0],
    ["90.5", 90.5],
    [".5", 0.5],
    ["1:30", 90],
    ["1:30.25", 90.25],
    ["01:02:03", 3723],
    ["01:02:03.5", 3723.5],
    ["0:00", 0],
    ["120:00", 7200], // the leading unit may exceed 59
    ["  1:05  ", 65],
    ["1,5", 1.5], // decimal comma
    ["٩٠", 90],
    ["١:٣٠", 90],
    ["٠١:٠٢:٠٣٫٥", 3723.5],
    ["۱:۳۰", 90],
  ])("accepts %j -> %d s", (text, seconds) => {
    expect(parseTime(text)).toBeCloseTo(seconds, 6);
  });

  it.each(["", "   ", "abc", "1:75", "1:60", "1:2:3:4", "-5", "1::2", ":30", "1:", "1.2.3", "1e3", "0x10", "1:61:00", "٣:٦٠", "∞"])(
    "rejects %j",
    (text) => {
      expect(parseTime(text)).toBeNull();
    },
  );
});

describe("parseTimeRange", () => {
  it("empty start is 0 and empty end means to the end", () => {
    expect(parseTimeRange("", "")).toEqual({ start: 0, end: null });
    expect(parseTimeRange("0", "")).toEqual({ start: 0, end: null });
    expect(parseTimeRange("1:00", "")).toEqual({ start: 60, end: null });
  });

  it("parses both ends", () => {
    expect(parseTimeRange("0:10", "0:20.5")).toEqual({ start: 10, end: 20.5 });
    expect(parseTimeRange("٠:١٠", "٠:٢٠")).toEqual({ start: 10, end: 20 });
  });

  it.each([
    ["abc", ""],
    ["0", "abc"],
    ["10", "10"], // end == start
    ["10", "5"], // end < start
    ["1:99", ""],
  ])("throws ConvertError(invalidTime) for (%j, %j)", (a, b) => {
    try {
      parseTimeRange(a, b);
      expect.unreachable();
    } catch (e) {
      expect(e).toBeInstanceOf(ConvertError);
      expect((e as ConvertError).code).toBe("invalidTime");
    }
  });
});

describe("formatFfmpegTime", () => {
  it("prints the shortest decimal ffmpeg accepts", () => {
    expect(formatFfmpegTime(0)).toBe("0");
    expect(formatFfmpegTime(1.5)).toBe("1.5");
    expect(formatFfmpegTime(90)).toBe("90");
    expect(formatFfmpegTime(0.25)).toBe("0.25");
    expect(formatFfmpegTime(1 / 3)).toBe("0.333");
    expect(formatFfmpegTime(3723.5)).toBe("3723.5");
    expect(formatFfmpegTime(-4)).toBe("0");
  });
});
