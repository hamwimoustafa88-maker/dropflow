import { describe, expect, it } from "vitest";
import { decodeText, isValidUtf8 } from "../lib/text-decode";

const enc = (s: string) => new TextEncoder().encode(s);

function utf16(s: string, bigEndian: boolean, bom: boolean): Uint8Array {
  const out: number[] = bom ? (bigEndian ? [0xfe, 0xff] : [0xff, 0xfe]) : [];
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (bigEndian) out.push(c >> 8, c & 0xff);
    else out.push(c & 0xff, c >> 8);
  }
  return Uint8Array.from(out);
}

describe("decodeText", () => {
  it("decodes plain UTF-8 (Arabic + Latin)", () => {
    const r = decodeText(enc("مرحبا بالعالم - Hello"));
    expect(r.text).toBe("مرحبا بالعالم - Hello");
    expect(r.encoding).toBe("utf-8");
  });

  it("strips the UTF-8 BOM", () => {
    const bytes = Uint8Array.from([0xef, 0xbb, 0xbf, ...enc("1\r\nنص")]);
    const r = decodeText(bytes);
    expect(r.text).toBe("1\r\nنص");
    expect(r.encoding).toBe("utf-8");
    expect(r.text.charCodeAt(0)).not.toBe(0xfeff);
  });

  it("decodes UTF-16 LE and BE with BOM", () => {
    expect(decodeText(utf16("عربي abc", false, true))).toEqual({ text: "عربي abc", encoding: "utf-16le" });
    expect(decodeText(utf16("عربي abc", true, true))).toEqual({ text: "عربي abc", encoding: "utf-16be" });
  });

  it("detects BOM-less UTF-16 for ASCII-heavy text", () => {
    expect(decodeText(utf16("1\n00:00:01,000 --> 00:00:02,000\nHello", false, false))).toMatchObject({ encoding: "utf-16le" });
    expect(decodeText(utf16("1\n00:00:01,000 --> 00:00:02,000\nHello", true, false))).toMatchObject({ encoding: "utf-16be" });
  });

  it("falls back to windows-1256 for legacy Arabic bytes", () => {
    // "مرحبا" = E3 D1 CD C8 C7 in windows-1256 (invalid as UTF-8).
    const marhaba = Uint8Array.from([0xe3, 0xd1, 0xcd, 0xc8, 0xc7]);
    expect(isValidUtf8(marhaba)).toBe(false);
    expect(decodeText(marhaba)).toEqual({ text: "مرحبا", encoding: "windows-1256" });
    // "شكرا" = D4 DF D1 C7, mixed with ASCII and CRLF
    const mixed = Uint8Array.from([0x31, 0x0d, 0x0a, 0xd4, 0xdf, 0xd1, 0xc7, 0x20, 0x4f, 0x4b]);
    expect(decodeText(mixed)).toEqual({ text: "1\r\nشكرا OK", encoding: "windows-1256" });
  });

  it("treats pure ASCII as UTF-8 and empty input as empty text", () => {
    expect(decodeText(enc("plain"))).toEqual({ text: "plain", encoding: "utf-8" });
    expect(decodeText(new Uint8Array(0))).toEqual({ text: "", encoding: "utf-8" });
  });

  it("isValidUtf8 is strict", () => {
    expect(isValidUtf8(enc("نص"))).toBe(true);
    expect(isValidUtf8(Uint8Array.from([0xc3, 0x28]))).toBe(false);
    expect(isValidUtf8(Uint8Array.from([0xff]))).toBe(false);
  });
});
