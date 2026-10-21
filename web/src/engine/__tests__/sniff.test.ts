import { describe, expect, it } from "vitest";
import { mimeOfExt, sniffExtension } from "../lib/sniff";

const b = (...v: number[]) => Uint8Array.from(v);
const ascii = (s: string) => Uint8Array.from(s, (c) => c.charCodeAt(0));
function cat(...parts: Uint8Array[]): Uint8Array {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let o = 0;
  for (const p of parts) {
    out.set(p, o);
    o += p.length;
  }
  return out;
}
const zeros = (n: number) => new Uint8Array(n);

describe("sniffExtension", () => {
  it("recognises images", () => {
    expect(sniffExtension(cat(b(0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a), zeros(20)))).toBe("png");
    expect(sniffExtension(b(0xff, 0xd8, 0xff, 0xe0, 0, 16))).toBe("jpg");
    expect(sniffExtension(ascii("GIF89a\x01\x00\x01\x00"))).toBe("gif");
    expect(sniffExtension(ascii("GIF87a\x01\x00"))).toBe("gif");
    expect(sniffExtension(cat(ascii("RIFF"), zeros(4), ascii("WEBPVP8 ")))).toBe("webp");
    // BMP: "BM", size, 4 reserved zero bytes, data offset, DIB header size 40
    expect(sniffExtension(cat(ascii("BM"), b(54, 0, 0, 0), zeros(4), b(54, 0, 0, 0), b(40, 0, 0, 0), zeros(8)))).toBe("bmp");
    // ICO: reserved 0, type 1, count 2
    expect(sniffExtension(b(0, 0, 1, 0, 2, 0, 16, 16, 0, 0, 1, 0, 32, 0))).toBe("ico");
  });

  it("does not mistake plain text starting with 'BM' for a bitmap", () => {
    expect(sniffExtension(ascii("BMW makes cars and this is only text\n"))).toBe("txt");
  });

  it("recognises documents and archives", () => {
    expect(sniffExtension(ascii("%PDF-1.7\n%\xe2\xe3"))).toBe("pdf");
    expect(sniffExtension(b(0x50, 0x4b, 0x03, 0x04, 20, 0))).toBe("zip");
    expect(sniffExtension(b(0x50, 0x4b, 0x05, 0x06, 0, 0))).toBe("zip");
    expect(sniffExtension(b(0x1f, 0x8b, 0x08, 0, 0, 0))).toBe("gz");
    expect(sniffExtension(b(0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c, 0, 4))).toBe("7z");
    expect(sniffExtension(ascii("Rar!\x1a\x07\x00abc"))).toBe("rar");
    expect(sniffExtension(cat(zeros(257), ascii("ustar\x0000"), zeros(300)))).toBe("tar");
  });

  it("recognises audio", () => {
    expect(sniffExtension(ascii("ID3\x03\x00\x00\x00"))).toBe("mp3");
    expect(sniffExtension(b(0xff, 0xfb, 0x90, 0x00))).toBe("mp3");
    expect(sniffExtension(b(0xff, 0xf3, 0x90, 0x00))).toBe("mp3");
    expect(sniffExtension(cat(ascii("RIFF"), zeros(4), ascii("WAVEfmt ")))).toBe("wav");
    expect(sniffExtension(ascii("OggS\x00\x02"))).toBe("ogg");
    expect(sniffExtension(ascii("fLaC\x00\x00"))).toBe("flac");
  });

  it("does not call an ADTS AAC header an mp3 (layer bits 00)", () => {
    expect(sniffExtension(b(0xff, 0xf1, 0x50, 0x80, 0x01, 0x1f, 0xfc))).toBe("bin");
  });

  it("recognises video containers", () => {
    expect(sniffExtension(cat(b(0, 0, 0, 0x20), ascii("ftypisom"), zeros(8)))).toBe("mp4");
    expect(sniffExtension(cat(b(0, 0, 0, 0x14), ascii("ftypqt  "), zeros(8)))).toBe("mov");
    expect(sniffExtension(cat(b(0, 0, 0, 0x20), ascii("ftypM4A "), zeros(8)))).toBe("m4a");
    expect(sniffExtension(cat(b(0x1a, 0x45, 0xdf, 0xa3, 0x9f, 0x42, 0x86, 0x81, 0x01), ascii("B\xf7\x81\x01B\x82\x84webm"), zeros(8)))).toBe("webm");
    expect(sniffExtension(cat(b(0x1a, 0x45, 0xdf, 0xa3, 0x9f, 0x42, 0x86, 0x81, 0x01), ascii("B\x82\x88matroska"), zeros(8)))).toBe("mkv");
  });

  it("recognises text: ASCII, Arabic UTF-8, BOMs, UTF-16 with BOM", () => {
    expect(sniffExtension(ascii("hello world\r\nline two\n"))).toBe("txt");
    expect(sniffExtension(new TextEncoder().encode("مرحبا بالعالم\n"))).toBe("txt");
    expect(sniffExtension(cat(b(0xef, 0xbb, 0xbf), ascii("bom text")))).toBe("txt");
    expect(sniffExtension(b(0xff, 0xfe, 0x68, 0x00, 0x69, 0x00))).toBe("txt");
    expect(sniffExtension(b(0xfe, 0xff, 0x00, 0x68, 0x00, 0x69))).toBe("txt");
  });

  it("a long Arabic text cut in the middle of a multi-byte character is still text", () => {
    const text = new TextEncoder().encode("ع".repeat(40_000));
    expect(text.length).toBeGreaterThan(64 * 1024);
    expect(sniffExtension(text)).toBe("txt");
  });

  it("falls back to bin for control bytes, invalid UTF-8 and empty input", () => {
    expect(sniffExtension(b(0x01, 0x02, 0x03, 0x04))).toBe("bin");
    expect(sniffExtension(b(0x61, 0xc3, 0x28))).toBe("bin");
    expect(sniffExtension(new Uint8Array(0))).toBe("bin");
    expect(sniffExtension(zeros(32))).toBe("bin");
  });
});

describe("mimeOfExt", () => {
  it("maps known extensions and defaults to octet-stream", () => {
    expect(mimeOfExt("png")).toBe("image/png");
    expect(mimeOfExt("JPG")).toBe("image/jpeg");
    expect(mimeOfExt("txt")).toBe("text/plain;charset=utf-8");
    expect(mimeOfExt("zip")).toBe("application/zip");
    expect(mimeOfExt("weird")).toBe("application/octet-stream");
    expect(mimeOfExt("")).toBe("application/octet-stream");
  });
});
