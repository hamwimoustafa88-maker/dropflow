import { describe, expect, it } from "vitest";
import { looksLikeTar, readTar } from "../lib/tar";
import { sanitizeEntryPath } from "../lib/zip-safe";
import { buildTar, buildTarLongName, buildTarPax, concat, enc, tarHeader } from "./archive-fixtures";

const text = (u: Uint8Array) => new TextDecoder().decode(u);

describe("readTar", () => {
  it("reads regular files and skips directories, symlinks and unknown types", () => {
    const tar = buildTar([
      { name: "dir/", type: "5" },
      { name: "dir/a.txt", data: enc("hello") },
      { name: "link", type: "2" },
      { name: "dir/ملف عربي.txt", data: enc("مرحبا") },
      { name: "empty.bin", data: new Uint8Array(0) },
      { name: "big.bin", data: new Uint8Array(1300).fill(7) },
      { name: "fifo", type: "6" },
    ]);
    const entries = readTar(tar);
    expect(entries.map((e) => e.name)).toEqual(["dir/a.txt", "dir/ملف عربي.txt", "empty.bin", "big.bin"]);
    expect(text(entries[0].data)).toBe("hello");
    expect(text(entries[1].data)).toBe("مرحبا");
    expect(entries[2].size).toBe(0);
    expect(entries[3].data.length).toBe(1300);
    expect(entries[3].data.every((b) => b === 7)).toBe(true);
    expect(entries[0].mtime?.getTime()).toBe(1_700_000_000_000);
  });

  it("joins the ustar prefix field with the name but not for GNU headers", () => {
    const posix = readTar(buildTar([{ name: "file.txt", prefix: "very/long/directory/path", data: enc("x") }]));
    expect(posix[0].name).toBe("very/long/directory/path/file.txt");
    const gnu = readTar(buildTar([{ name: "file.txt", prefix: "ignored", gnu: true, data: enc("x") }]));
    expect(gnu[0].name).toBe("file.txt");
  });

  it("applies GNU long names ('L') to the next entry only", () => {
    const longName = `${"nested/".repeat(20)}${"x".repeat(60)}.txt`;
    expect(longName.length).toBeGreaterThan(100);
    const entries = readTar(buildTarLongName(longName, enc("long!")));
    expect(entries).toHaveLength(1);
    expect(entries[0].name).toBe(longName);
    expect(text(entries[0].data)).toBe("long!");
  });

  it("applies pax 'path' and 'mtime' records", () => {
    const entries = readTar(buildTarPax("pax/дир/ملف.txt", enc("pax data")));
    expect(entries[0].name).toBe("pax/дир/ملف.txt");
    expect(entries[0].mtime?.getTime()).toBe(1_700_000_000_500);
    expect(text(entries[0].data)).toBe("pax data");
  });

  it("returns evil names raw so the caller can sanitise them (and sanitising rejects them)", () => {
    const entries = readTar(
      buildTar([
        { name: "../evil", data: enc("a") },
        { name: "/abs/path.txt", data: enc("b") },
        { name: "ok/fine.txt", data: enc("c") },
        { name: "x/../../y", data: enc("d") },
      ]),
    );
    expect(entries.map((e) => e.name)).toEqual(["../evil", "/abs/path.txt", "ok/fine.txt", "x/../../y"]);
    expect(entries.map((e) => sanitizeEntryPath(e.name))).toEqual([null, "abs/path.txt", "ok/fine.txt", null]);
  });

  it("works without the terminating zero blocks and with extra padding after them", () => {
    expect(readTar(buildTar([{ name: "a", data: enc("1") }], { terminator: false }))).toHaveLength(1);
    const padded = concat([buildTar([{ name: "a", data: enc("1") }]), new Uint8Array(10240)]);
    expect(readTar(padded)).toHaveLength(1);
    expect(readTar(new Uint8Array(0))).toEqual([]);
    expect(readTar(new Uint8Array(1024))).toEqual([]);
  });

  it("throws corrupt for a bad checksum, truncated data and trailing garbage", () => {
    const bad = concat([tarHeader({ name: "a", size: 1, type: "0", corruptChecksum: true }), new Uint8Array(512), new Uint8Array(1024)]);
    expect(() => readTar(bad)).toThrowError(/corrupt/);

    const good = buildTar([{ name: "a.txt", data: new Uint8Array(2000).fill(1) }]);
    expect(() => readTar(good.subarray(0, 512 + 1000))).toThrowError(/corrupt/);
    expect(() => readTar(concat([good.subarray(0, 512 + 2048), enc("garbage")]))).toThrowError(/corrupt/);
    expect(() => readTar(enc("x".repeat(600)))).toThrowError(/corrupt/);
  });

  it("enforces the entry cap", () => {
    const tar = buildTar(Array.from({ length: 4 }, (_, i) => ({ name: `f${i}`, data: enc("x") })));
    expect(() => readTar(tar, 3)).toThrowError(/tooManyFiles/);
    expect(readTar(tar, 4)).toHaveLength(4);
  });

  it("returns views, not copies", () => {
    const tar = buildTar([{ name: "a", data: enc("hello") }]);
    const [e] = readTar(tar);
    expect(e.data.buffer).toBe(tar.buffer);
  });
});

describe("looksLikeTar", () => {
  it("accepts ustar and checksum-valid v7 headers, rejects others", () => {
    expect(looksLikeTar(buildTar([{ name: "a", data: enc("x") }]))).toBe(true);
    const v7 = buildTar([{ name: "a", data: enc("x") }]);
    v7.fill(0, 257, 265);
    // recompute checksum for the modified header
    let sum = 0;
    for (let i = 0; i < 512; i++) sum += i >= 148 && i < 156 ? 0x20 : v7[i];
    v7.set(enc(sum.toString(8).padStart(6, "0")), 148);
    v7[154] = 0;
    v7[155] = 0x20;
    expect(looksLikeTar(v7)).toBe(true);
    expect(looksLikeTar(new Uint8Array(2048))).toBe(false);
    expect(looksLikeTar(enc("hello world".repeat(100)))).toBe(false);
    expect(looksLikeTar(new Uint8Array(100))).toBe(false);
  });
});
