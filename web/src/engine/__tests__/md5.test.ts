import { createHash, randomBytes } from "node:crypto";
import { describe, expect, it } from "vitest";
import { Md5, md5Hex } from "../lib/md5";

const enc = (s: string) => new TextEncoder().encode(s);

describe("Md5 (RFC 1321 test suite)", () => {
  const vectors: [string, string][] = [
    ["", "d41d8cd98f00b204e9800998ecf8427e"],
    ["a", "0cc175b9c0f1b6a831c399e269772661"],
    ["abc", "900150983cd24fb0d6963f7d28e17f72"],
    ["message digest", "f96b697d7cb7938d525a2f31aaf161d0"],
    ["abcdefghijklmnopqrstuvwxyz", "c3fcd3d76192e4007dfb496cca67e13b"],
    ["ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", "d174ab98d277d9f5a5611c2c9f419d9f"],
    ["12345678901234567890123456789012345678901234567890123456789012345678901234567890", "57edf4a22be3c955ac49da2e2107b67a"],
  ];
  for (const [input, expected] of vectors) {
    it(`md5(${JSON.stringify(input.slice(0, 20))}...) = ${expected}`, () => {
      expect(md5Hex(enc(input))).toBe(expected);
    });
  }

  it("hashes one million 'a' characters", () => {
    expect(md5Hex(new Uint8Array(1_000_000).fill(97))).toBe("7707d6ae4e027c70eea2a935c2296f21");
  });

  it("handles the padding boundaries (lengths 55, 56, 57, 63, 64, 65, 119, 120)", () => {
    for (const n of [0, 1, 55, 56, 57, 63, 64, 65, 119, 120, 121, 127, 128, 129]) {
      const data = randomBytes(n);
      expect(md5Hex(data), `length ${n}`).toBe(createHash("md5").update(data).digest("hex"));
    }
  });

  it("is chunk-size independent (odd slice sizes crossing the 64-byte block boundary)", () => {
    const data = randomBytes(10_000);
    const expected = createHash("md5").update(data).digest("hex");
    for (const size of [1, 3, 7, 63, 64, 65, 100, 1000, 4096, 9999]) {
      const h = new Md5();
      for (let i = 0; i < data.length; i += size) h.update(data.subarray(i, i + size));
      expect(h.digestHex(), `slice ${size}`).toBe(expected);
    }
  });

  it("matches node:crypto on a 12 MiB buffer fed in 4 MiB slices", () => {
    const data = randomBytes(12 * 1024 * 1024);
    const h = new Md5();
    for (let i = 0; i < data.length; i += 4 * 1024 * 1024) h.update(data.subarray(i, i + 4 * 1024 * 1024));
    expect(h.digestHex()).toBe(createHash("md5").update(data).digest("hex"));
  });

  it("digestHex is idempotent and update afterwards throws", () => {
    const h = new Md5().update(enc("abc"));
    expect(h.digestHex()).toBe("900150983cd24fb0d6963f7d28e17f72");
    expect(h.digestHex()).toBe("900150983cd24fb0d6963f7d28e17f72");
    expect(() => h.update(enc("x"))).toThrow();
  });

  it("hashes Arabic UTF-8 text", () => {
    const data = enc("مرحبا بالعالم");
    expect(md5Hex(data)).toBe(createHash("md5").update(data).digest("hex"));
  });
});
