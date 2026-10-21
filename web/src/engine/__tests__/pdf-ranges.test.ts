import { describe, expect, it } from "vitest";
import { normalizeSpec, parsePageSpec, parseSplitGroups } from "../lib/ranges";
import { ConvertError } from "../types";

function code(fn: () => unknown): string | undefined {
  try {
    fn();
  } catch (e) {
    return e instanceof ConvertError ? e.code : `other:${String(e)}`;
  }
  return undefined;
}

describe("parsePageSpec", () => {
  it("parses single pages and ranges (0-based, order preserved)", () => {
    expect(parsePageSpec("3", 10)).toEqual([2]);
    expect(parsePageSpec("1-3", 10)).toEqual([0, 1, 2]);
    expect(parsePageSpec("1-3, 5, 7-9", 10)).toEqual([0, 1, 2, 4, 6, 7, 8]);
    expect(parsePageSpec("3,1,2", 3)).toEqual([2, 0, 1]);
  });

  it("keeps duplicates and supports descending ranges", () => {
    expect(parsePageSpec("2,2,1", 3)).toEqual([1, 1, 0]);
    expect(parsePageSpec("5-3", 6)).toEqual([4, 3, 2]);
    expect(parsePageSpec("1, 3-1, 1", 3)).toEqual([0, 2, 1, 0, 0]);
  });

  it("supports open ends and the last/end keywords", () => {
    expect(parsePageSpec("4-", 6)).toEqual([3, 4, 5]);
    expect(parsePageSpec("last", 6)).toEqual([5]);
    expect(parsePageSpec("END", 6)).toEqual([5]);
    expect(parsePageSpec("1, last", 4)).toEqual([0, 3]);
    expect(parsePageSpec("last-1", 3)).toEqual([2, 1, 0]);
    expect(parsePageSpec("2-end", 4)).toEqual([1, 2, 3]);
    expect(parsePageSpec("last-", 3)).toEqual([2]);
  });

  it("tolerates whitespace, Arabic comma, semicolon, newline, Arabic-Indic and Persian digits, unicode dashes", () => {
    expect(parsePageSpec("  1 - 2 ,  4  ", 5)).toEqual([0, 1, 3]);
    expect(parsePageSpec("١، ٣-٥", 6)).toEqual([0, 2, 3, 4]);
    expect(parsePageSpec("۱؛۲", 3)).toEqual([0, 1]);
    expect(parsePageSpec("1;2\n3", 3)).toEqual([0, 1, 2]);
    expect(parsePageSpec("2–4", 5)).toEqual([1, 2, 3]);
    expect(parsePageSpec("2—3", 5)).toEqual([1, 2]);
    expect(parsePageSpec("آخر", 5)).toEqual([4]);
    expect(parsePageSpec("1,‏3", 5)).toEqual([0, 2]);
    expect(parsePageSpec("1,,2,", 3)).toEqual([0, 1]);
  });

  it("empty spec means all pages only when allowed", () => {
    expect(parsePageSpec("", 3, { allowEmpty: true })).toEqual([0, 1, 2]);
    expect(parsePageSpec("  ", 2, { allowEmpty: true })).toEqual([0, 1]);
    expect(code(() => parsePageSpec("", 3))).toBe("invalidRange");
    expect(code(() => parsePageSpec(",", 3))).toBe("invalidRange");
  });

  it("rejects zero, out-of-range and junk with invalidRange carrying the spec", () => {
    for (const spec of ["0", "4", "1-4", "abc", "1-b", "--", "1--2", "-3", "1.5", "1 2", "1-2-3", "x-1", "٠"]) {
      expect(code(() => parsePageSpec(spec, 3)), spec).toBe("invalidRange");
    }
    try {
      parsePageSpec("9", 3);
    } catch (e) {
      expect((e as ConvertError).params).toEqual({ spec: "9" });
    }
  });

  it("fails on an empty document", () => {
    expect(code(() => parsePageSpec("1", 0))).toBe("invalidRange");
    expect(code(() => parsePageSpec("last", 0))).toBe("invalidRange");
    expect(parsePageSpec("", 0, { allowEmpty: true })).toEqual([]);
  });
});

describe("parseSplitGroups", () => {
  it("empty spec splits into single pages", () => {
    expect(parseSplitGroups("", 3)).toEqual([[0], [1], [2]]);
  });

  it("each comma item is one output", () => {
    expect(parseSplitGroups("1-3, 5, 7-9", 9)).toEqual([[0, 1, 2], [4], [6, 7, 8]]);
    expect(parseSplitGroups("2-", 4)).toEqual([[1, 2, 3]]);
    expect(parseSplitGroups("١-٢،٤", 4)).toEqual([[0, 1], [3]]);
  });

  it("rejects bad items", () => {
    expect(code(() => parseSplitGroups("1-9", 3))).toBe("invalidRange");
    expect(code(() => parseSplitGroups("x", 3))).toBe("invalidRange");
  });
});

describe("normalizeSpec", () => {
  it("maps digits and separators", () => {
    expect(normalizeSpec("١٢،٣")).toBe("12,3");
    expect(normalizeSpec("۴–۵")).toBe("4-5");
  });
});
