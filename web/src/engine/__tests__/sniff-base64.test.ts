import { randomBytes } from "node:crypto";
import { describe, expect, it } from "vitest";
import { base64Parts, base64ToBytes, bytesToBase64 } from "../lib/sniff-base64";
import { ConvertError } from "../types";

describe("bytesToBase64", () => {
  it("matches Buffer for every length 0..40 (standard and URL-safe)", () => {
    for (let n = 0; n <= 40; n++) {
      const data = randomBytes(n);
      expect(bytesToBase64(data), `std ${n}`).toBe(Buffer.from(data).toString("base64"));
      expect(bytesToBase64(data, { urlSafe: true }), `url ${n}`).toBe(Buffer.from(data).toString("base64url"));
    }
  });

  it("uses the URL-safe alphabet without padding", () => {
    const data = Uint8Array.from([0xfb, 0xff, 0xfe]);
    expect(bytesToBase64(data)).toBe("+//+");
    expect(bytesToBase64(data, { urlSafe: true })).toBe("-__-");
    expect(bytesToBase64(Uint8Array.from([0xfb]), { urlSafe: true })).toBe("-w");
    expect(bytesToBase64(Uint8Array.from([0xfb]))).toBe("+w==");
  });

  it("is independent of the chunk size (parts only break on 3-byte groups)", () => {
    const data = randomBytes(10_001);
    const expected = Buffer.from(data).toString("base64");
    for (const chunk of [3, 4, 30, 300, 4096, 100_000]) {
      const parts = [...base64Parts(data, {}, chunk)];
      const text = parts.map((p) => new TextDecoder().decode(p)).join("");
      expect(text, `chunk ${chunk}`).toBe(expected);
      // every part except the last is a whole number of 4-char groups without padding
      for (const p of parts.slice(0, -1)) expect(p.length % 4).toBe(0);
    }
  });
});

describe("base64ToBytes", () => {
  it("round-trips random binary data of many lengths", () => {
    for (let n = 1; n <= 70; n++) {
      const data = randomBytes(n);
      expect(Buffer.from(base64ToBytes(Buffer.from(data).toString("base64")))).toEqual(data);
      expect(Buffer.from(base64ToBytes(Buffer.from(data).toString("base64url")))).toEqual(data);
    }
    const big = randomBytes(2_000_000);
    expect(Buffer.from(base64ToBytes(bytesToBase64(big))).equals(big)).toBe(true);
  });

  it("ignores whitespace and line breaks of every kind", () => {
    const text = "SGVs\r\nbG8g\td29y\n  bGQh \u000b\f";
    expect(new TextDecoder().decode(base64ToBytes(text))).toBe("Hello world!");
  });

  it("accepts a data URL prefix", () => {
    expect(new TextDecoder().decode(base64ToBytes("data:text/plain;base64,SGVsbG8="))).toBe("Hello");
    expect(new TextDecoder().decode(base64ToBytes("  DATA:image/png;charset=x;BASE64,SGVsbG8="))).toBe("Hello");
  });

  it("accepts missing padding and the URL-safe alphabet", () => {
    expect(new TextDecoder().decode(base64ToBytes("SGVsbG8"))).toBe("Hello");
    expect(new TextDecoder().decode(base64ToBytes("SGk"))).toBe("Hi");
    expect(Array.from(base64ToBytes("-__-"))).toEqual([0xfb, 0xff, 0xfe]);
    expect(Array.from(base64ToBytes("+//+"))).toEqual([0xfb, 0xff, 0xfe]);
  });

  it("decodes UTF-8 Arabic text", () => {
    const b64 = Buffer.from("مرحبا", "utf8").toString("base64");
    expect(new TextDecoder().decode(base64ToBytes(b64))).toBe("مرحبا");
  });

  it("rejects invalid input with ConvertError('invalidBase64')", () => {
    const bad = ["", "   ", "====", "A", "SGVs*G8=", "SGVsbG8=x", "SG=VsbG8", "AAAAA", "data:text/plain,hello", "مرحبا", "SGVsbG8===="];
    for (const input of bad) {
      let err: unknown;
      try {
        base64ToBytes(input);
      } catch (e) {
        err = e;
      }
      expect(err, JSON.stringify(input)).toBeInstanceOf(ConvertError);
      expect((err as ConvertError).code).toBe("invalidBase64");
    }
  });
});
