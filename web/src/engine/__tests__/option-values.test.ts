import { describe, expect, it } from "vitest";
import { defaultValues, initialDraft, readValues } from "../option-values";
import type { ToolOption } from "../types";

const OPTIONS: ToolOption[] = [
  { key: "width", kind: "number", default: 0, min: 0, max: 100 },
  { key: "quality", kind: "range", default: 75, min: 10, max: 100 },
  { key: "mode", kind: "choice", default: "a", choices: ["a", "b"] },
  { key: "keep", kind: "bool", default: true },
  { key: "text", kind: "text", default: "", required: true },
  { key: "pw", kind: "password", default: "" },
];

describe("option drafts", () => {
  it("start from the defaults, typed correctly", () => {
    expect(initialDraft(OPTIONS)).toEqual({ width: "0", quality: "75", mode: "a", keep: true, text: "", pw: "" });
    expect(defaultValues(OPTIONS)).toEqual({ width: 0, quality: 75, mode: "a", keep: true, text: "", pw: "" });
  });

  it("converts a valid draft to typed values", () => {
    const v = readValues(OPTIONS, { width: "32", quality: "50", mode: "b", keep: false, text: "hi", pw: "" });
    expect(v).toEqual({ width: 32, quality: 50, mode: "b", keep: false, text: "hi", pw: "" });
  });

  it("rejects a missing required text, junk numbers and out-of-range values (Run stays disabled)", () => {
    const ok = { width: "1", quality: "50", mode: "a", keep: true, text: "x", pw: "" };
    expect(readValues(OPTIONS, { ...ok, text: "   " })).toBeNull();
    expect(readValues(OPTIONS, { ...ok, width: "" })).toBeNull();
    expect(readValues(OPTIONS, { ...ok, width: "abc" })).toBeNull();
    expect(readValues(OPTIONS, { ...ok, width: "101" })).toBeNull();
    expect(readValues(OPTIONS, { ...ok, quality: "9" })).toBeNull();
    expect(readValues(OPTIONS, { ...ok, quality: "-1" })).toBeNull();
  });

  it("an unknown choice value falls back to the default instead of reaching the converter", () => {
    const v = readValues(OPTIONS, { width: "1", quality: "50", mode: "zzz", keep: true, text: "x", pw: "" });
    expect(v?.mode).toBe("a");
  });

  it("accepts decimals and boundaries", () => {
    const dec: ToolOption[] = [{ key: "n", kind: "number", default: 1, min: 0.5, max: 2 }];
    expect(readValues(dec, { n: "0.5" })).toEqual({ n: 0.5 });
    expect(readValues(dec, { n: "2" })).toEqual({ n: 2 });
    expect(readValues(dec, { n: "2.01" })).toBeNull();
  });
});
