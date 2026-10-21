import { zipSync } from "fflate";
import { describe, expect, it } from "vitest";
import {
  LIMITS,
  checkLimits,
  crc32,
  entryBaseName,
  entryPathFromFileName,
  readZipDirectory,
  sanitizeEntryPath,
  zipEntryRange,
} from "../lib/zip-safe";
import { ConvertError } from "../types";
import { buildZip, concat, enc } from "./archive-fixtures";

describe("sanitizeEntryPath", () => {
  it("keeps ordinary relative paths, including Arabic names", () => {
    expect(sanitizeEntryPath("a.txt")).toBe("a.txt");
    expect(sanitizeEntryPath("dir/sub/file.tar.gz")).toBe("dir/sub/file.tar.gz");
    expect(sanitizeEntryPath("مجلد/ملف جميل.txt")).toBe("مجلد/ملف جميل.txt");
    expect(sanitizeEntryPath("تقرير\u200f.pdf")).toBe("تقرير\u200f.pdf");
  });

  it("rejects every kind of parent-directory traversal", () => {
    for (const bad of ["../a", "a/../../b", "a/b/../c", "..\\..\\x", "..", "a/..", "../", "a\\..\\b", "a/.../b", "a/.. /b", "a/..  ", "....//x", "x/..\\y"]) {
      expect(sanitizeEntryPath(bad), bad).toBeNull();
    }
  });

  it("strips absolute prefixes, drive letters and UNC roots instead of honouring them", () => {
    expect(sanitizeEntryPath("/etc/passwd")).toBe("etc/passwd");
    expect(sanitizeEntryPath("//server/share/f.txt")).toBe("server/share/f.txt");
    expect(sanitizeEntryPath("C:\\Windows\\x")).toBe("Windows/x");
    expect(sanitizeEntryPath("c:/a/b")).toBe("a/b");
    expect(sanitizeEntryPath("C:relative.txt")).toBe("relative.txt");
    expect(sanitizeEntryPath("\\\\?\\C:\\x\\y.txt")).toBe("_/C_/x/y.txt");
  });

  it("normalises separators and redundant segments", () => {
    expect(sanitizeEntryPath("a/./b")).toBe("a/b");
    expect(sanitizeEntryPath("./a")).toBe("a");
    expect(sanitizeEntryPath("a//b///c")).toBe("a/b/c");
    expect(sanitizeEntryPath("a\\b\\c.txt")).toBe("a/b/c.txt");
    expect(sanitizeEntryPath("dir/")).toBe("dir");
  });

  it("rejects NUL and control characters", () => {
    for (const bad of ["a\u0000b", "a\nb", "a\tb", "a\u001fb", "a\u007fb", "\u0000"]) expect(sanitizeEntryPath(bad), JSON.stringify(bad)).toBeNull();
  });

  it("rejects empty results", () => {
    for (const bad of ["", "/", "./", ".", "   ", "//", "\\", " . "]) expect(sanitizeEntryPath(bad), JSON.stringify(bad)).toBeNull();
  });

  it("prefixes reserved Windows names and trims trailing dots and spaces", () => {
    expect(sanitizeEntryPath("CON.txt")).toBe("_CON.txt");
    expect(sanitizeEntryPath("dir/NUL")).toBe("dir/_NUL");
    expect(sanitizeEntryPath("aux/x")).toBe("_aux/x");
    expect(sanitizeEntryPath("com1.log")).toBe("_com1.log");
    expect(sanitizeEntryPath("file. ")).toBe("file");
    expect(sanitizeEntryPath("dir./x")).toBe("dir/x");
    expect(sanitizeEntryPath("console.txt")).toBe("console.txt");
  });

  it("replaces characters that are illegal in file names (including NTFS streams)", () => {
    expect(sanitizeEntryPath("ab:c/d*e?.txt")).toBe("ab_c/d_e_.txt");
    expect(sanitizeEntryPath("file.txt:hidden")).toBe("file.txt_hidden");
    expect(sanitizeEntryPath('q"<>|.txt')).toBe("q____.txt");
  });

  it("removes bidi override characters used to spoof extensions", () => {
    expect(sanitizeEntryPath("photo\u202Egpj.exe")).toBe("photogpj.exe");
    expect(sanitizeEntryPath("a\u2066b\u2069.txt")).toBe("ab.txt");
  });

  it("limits length and depth", () => {
    const long = sanitizeEntryPath("a".repeat(300) + ".txt");
    expect(long).not.toBeNull();
    expect(long!.length).toBeLessThanOrEqual(255);

    const dirs = Array.from({ length: 5 }, () => "d".repeat(40)).join("/");
    const fitted = sanitizeEntryPath(`${dirs}/${"x".repeat(100)}.txt`);
    expect(fitted).not.toBeNull();
    expect(fitted!.length).toBe(255);
    expect(fitted!.endsWith(".txt")).toBe(true);

    const tooLongDirs = Array.from({ length: 10 }, () => "d".repeat(40)).join("/");
    expect(sanitizeEntryPath(`${tooLongDirs}/f.txt`)).toBeNull();

    expect(sanitizeEntryPath(Array(32).fill("a").join("/"))).not.toBeNull();
    expect(sanitizeEntryPath(Array(33).fill("a").join("/"))).toBeNull();
  });

  it("is idempotent and never returns something that could escape", () => {
    const inputs = ["a/b", "/x/y", "C:\\a\\b", "تقرير/ملف", "a:b", "CON", "x. ", "photo\u202Ee.exe", "a/./b//c"];
    for (const i of inputs) {
      const once = sanitizeEntryPath(i);
      expect(once, i).not.toBeNull();
      expect(sanitizeEntryPath(once!)).toBe(once);
      expect(once!.startsWith("/")).toBe(false);
      expect(once!.split("/")).not.toContain("..");
      expect(once).not.toMatch(/^[A-Za-z]:/);
    }
  });

  it("helpers for names", () => {
    expect(entryBaseName("a/b/c.txt")).toBe("c.txt");
    expect(entryBaseName("c.txt")).toBe("c.txt");
    expect(entryPathFromFileName("a:b:c.txt")).toBe("a/b/c.txt");
  });
});

describe("crc32", () => {
  it("matches known values and supports chunking", () => {
    expect(crc32(new Uint8Array(0))).toBe(0);
    expect(crc32(enc("123456789"))).toBe(0xcbf43926);
    expect(crc32(enc("The quick brown fox jumps over the lazy dog"))).toBe(0x414fa339);
    expect(crc32(enc("6789"), crc32(enc("12345")))).toBe(0xcbf43926);
  });
});

describe("checkLimits", () => {
  const MIB = 1024 * 1024;
  const code = (fn: () => void) => {
    try {
      fn();
    } catch (e) {
      return e instanceof ConvertError ? e.code : "other";
    }
    return "ok";
  };

  it("exports the documented caps", () => {
    expect(LIMITS.maxEntries).toBe(5000);
    expect(LIMITS.maxTotalBytes).toBe(1024 * MIB);
    expect(LIMITS.maxEntryBytes).toBe(512 * MIB);
    expect(LIMITS.maxRatio).toBe(1000);
  });

  it("accepts normal archives", () => {
    expect(code(() => checkLimits([{ size: 10 * MIB, compressedSize: 5 * MIB }, { size: 100, compressedSize: 80 }]))).toBe("ok");
    expect(code(() => checkLimits([]))).toBe("ok");
  });

  it("refuses too many entries", () => {
    const many = Array.from({ length: LIMITS.maxEntries + 1 }, () => ({ size: 1, compressedSize: 1 }));
    expect(code(() => checkLimits(many))).toBe("tooManyFiles");
    expect(code(() => checkLimits(many.slice(0, LIMITS.maxEntries)))).toBe("ok");
  });

  it("refuses oversized entries and totals", () => {
    expect(code(() => checkLimits([{ size: 513 * MIB, compressedSize: 400 * MIB }]))).toBe("tooLarge");
    expect(code(() => checkLimits([{ size: 512 * MIB, compressedSize: 400 * MIB }]))).toBe("ok");
    expect(code(() => checkLimits([{ size: 500 * MIB, compressedSize: 400 * MIB }, { size: 500 * MIB, compressedSize: 400 * MIB }, { size: 100 * MIB, compressedSize: 90 * MIB }]))).toBe("tooLarge");
  });

  it("refuses zip-bomb ratios only above the floor", () => {
    expect(code(() => checkLimits([{ size: 64 * MIB, compressedSize: 60 * 1024 }]))).toBe("tooLarge");
    expect(code(() => checkLimits([{ size: 10 * MIB, compressedSize: 10 * MIB / 1000 }]))).toBe("ok");
    expect(code(() => checkLimits([{ size: 10 * MIB, compressedSize: Math.floor((10 * MIB) / 1001) }]))).toBe("tooLarge");
    expect(code(() => checkLimits([{ size: 2 * MIB, compressedSize: 0 }]))).toBe("tooLarge");
    // below the floor: 1 KiB from 1 byte is not a bomb
    expect(code(() => checkLimits([{ size: 1000, compressedSize: 1 }]))).toBe("ok");
  });

  it("honours custom limits", () => {
    const tiny = { maxEntries: 2, maxTotalBytes: 100, maxEntryBytes: 60, maxRatio: 10, ratioFloorBytes: 20 };
    expect(code(() => checkLimits([{ size: 10, compressedSize: 10 }], tiny))).toBe("ok");
    expect(code(() => checkLimits([{ size: 61, compressedSize: 61 }], tiny))).toBe("tooLarge");
    expect(code(() => checkLimits([{ size: 50, compressedSize: 50 }, { size: 51, compressedSize: 51 }], tiny))).toBe("tooLarge");
    expect(code(() => checkLimits([{ size: 50, compressedSize: 2 }], tiny))).toBe("tooLarge");
    expect(code(() => checkLimits([{ size: 1, compressedSize: 1 }, { size: 1, compressedSize: 1 }, { size: 1, compressedSize: 1 }], tiny))).toBe("tooManyFiles");
  });
});

describe("readZipDirectory", () => {
  it("reads a zip written by fflate (nested folders, dates, Arabic names, stored + deflated)", () => {
    const zip = zipSync({
      "root.txt": [enc("hello"), { level: 6, mtime: new Date(2023, 2, 4, 10, 20, 30) }],
      "dir/sub/ملف.txt": [enc("مرحبا"), { level: 0 }],
      "dir/empty": new Uint8Array(0),
    });
    const entries = readZipDirectory(zip);
    expect(entries.map((e) => e.name)).toEqual(["root.txt", "dir/sub/ملف.txt", "dir/empty"]);
    const root = entries[0];
    expect(root.method).toBe(8);
    expect(root.size).toBe(5);
    expect(root.crc).toBe(crc32(enc("hello")));
    expect(root.mtime?.getFullYear()).toBe(2023);
    expect(root.mtime?.getMonth()).toBe(2);
    expect(root.mtime?.getDate()).toBe(4);
    expect(entries[1].method).toBe(0);
    expect(entries[1].size).toBe(enc("مرحبا").length);
    expect(entries.every((e) => !e.encrypted && !e.isSymlink && !e.isDirectory)).toBe(true);
    const [start, end] = zipEntryRange(zip, entries[1]);
    expect(Array.from(zip.subarray(start, end))).toEqual(Array.from(enc("مرحبا")));
  });

  it("handles empty archives and rejects garbage / truncation", () => {
    const empty = buildZip([]);
    expect(readZipDirectory(empty)).toEqual([]);
    for (const bad of [new Uint8Array(0), new Uint8Array(10), enc("this is not a zip file at all, just text"), new Uint8Array(100)]) {
      expect(() => readZipDirectory(bad)).toThrowError(/corrupt/);
    }
    const good = buildZip([{ name: "a.txt", data: enc("x") }]);
    expect(() => readZipDirectory(good.subarray(0, good.length - 10))).toThrowError(/corrupt/);
  });

  it("flags directories, symlinks and encrypted entries", () => {
    const zip = buildZip([
      { name: "folder/", data: new Uint8Array(0) },
      { name: "link", data: enc("target"), madeBy: (3 << 8) | 20, externalAttrs: (0o120777 << 16) >>> 0 },
      { name: "secret.txt", data: enc("x"), flags: 0x801 },
      { name: "strong.txt", data: enc("x"), flags: 0x840 },
      { name: "file.txt", data: enc("x"), madeBy: (3 << 8) | 20, externalAttrs: (0o100644 << 16) >>> 0 },
    ]);
    const e = readZipDirectory(zip);
    expect(e.map((x) => [x.isDirectory, x.isSymlink, x.encrypted])).toEqual([
      [true, false, false],
      [false, true, false],
      [false, false, true],
      [false, false, true],
      [false, false, false],
    ]);
  });

  it("decodes names: UTF-8 flag, unflagged UTF-8, legacy windows-1256, Info-ZIP unicode path", () => {
    const utf8Name = enc("ملف.txt");
    const legacy = Uint8Array.from([0xe3, 0xd1, 0xcd, 0xc8, 0xc7, 0x2e, 0x74, 0x78, 0x74]); // "مرحبا.txt" in windows-1256
    const unicodePath = (raw: Uint8Array, unicode: string) => {
      const u = enc(unicode);
      const crc = crc32(raw);
      return concat([Uint8Array.of(0x75, 0x70), Uint8Array.of(5 + u.length, 0), Uint8Array.of(1), Uint8Array.of(crc & 255, (crc >>> 8) & 255, (crc >>> 16) & 255, (crc >>> 24) & 255), u]);
    };
    const asciiFallback = enc("fallback.txt");
    const zip = buildZip([
      { name: utf8Name, flags: 0x800 },
      { name: utf8Name, flags: 0 },
      { name: legacy, flags: 0 },
      { name: asciiFallback, flags: 0, extraCentral: unicodePath(asciiFallback, "اسم.txt") },
      { name: asciiFallback, flags: 0, extraCentral: unicodePath(enc("other.txt"), "wrong-crc.txt") },
    ]);
    expect(readZipDirectory(zip).map((x) => x.name)).toEqual(["ملف.txt", "ملف.txt", "مرحبا.txt", "اسم.txt", "fallback.txt"]);
  });

  it("reads ZIP64 sizes and offsets from the extra field and the ZIP64 end record", () => {
    const zip = buildZip([{ name: "a.txt", data: enc("hello"), zip64: true }, { name: "b.txt", data: enc("world!"), zip64: true }], { zip64Eocd: true });
    const e = readZipDirectory(zip);
    expect(e.map((x) => [x.name, x.size, x.compressedSize])).toEqual([
      ["a.txt", 5, 5],
      ["b.txt", 6, 6],
    ]);
    const [s, t] = zipEntryRange(zip, e[1]);
    expect(new TextDecoder().decode(zip.subarray(s, t))).toBe("world!");
  });

  it("enforces the entry-count cap from the end record before parsing entries", () => {
    const zip = buildZip(Array.from({ length: 6 }, (_, i) => ({ name: `f${i}.txt`, data: enc("x") })));
    expect(() => readZipDirectory(zip, 5)).toThrowError(/tooManyFiles/);
    expect(readZipDirectory(zip, 6)).toHaveLength(6);
  });

  it("zipEntryRange rejects offsets that do not point at a local header", () => {
    const zip = buildZip([{ name: "a.txt", data: enc("hello") }]);
    expect(() => zipEntryRange(zip, { localOffset: 3, compressedSize: 5 })).toThrowError(/corrupt/);
    expect(() => zipEntryRange(zip, { localOffset: 0, compressedSize: 5_000_000 })).toThrowError(/corrupt/);
  });
});
