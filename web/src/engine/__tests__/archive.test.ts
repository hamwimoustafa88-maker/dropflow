import { randomBytes } from "node:crypto";
import { gzipSync, unzipSync, zipSync, type UnzipFileInfo } from "fflate";
import { describe, expect, it } from "vitest";
import catalog from "../../data/catalog.json";
import { archiveConverter } from "../converters/archive";
import {
  MAX_ZIP_INPUT_BYTES,
  createZip,
  detectArchiveKind,
  extractArchiveBytes,
  extractGzipBytes,
  extractTarBytes,
  extractZipBytes,
  planZipNames,
  zipLevelFor,
} from "../converters/archive.impl";
import { LIMITS } from "../lib/zip-safe";
import { ConvertError, type Job } from "../types";
import { buildTar, buildZip, enc } from "./archive-fixtures";

const noop = () => {};
const signal = () => new AbortController().signal;
const MIB = 1024 * 1024;
const dec = (u: Uint8Array) => new TextDecoder().decode(u);

function job(op: string, files: File[]): Job {
  return { id: "j", operation: op, files, options: {} };
}

async function codeOf(p: Promise<unknown> | (() => unknown)): Promise<string> {
  try {
    await (typeof p === "function" ? p() : p);
  } catch (e) {
    return e instanceof ConvertError ? e.code : `other:${(e as Error).name}:${(e as Error).message}`;
  }
  return "ok";
}

function sync(fn: () => unknown): string {
  try {
    fn();
  } catch (e) {
    return e instanceof ConvertError ? e.code : `other:${(e as Error).name}:${(e as Error).message}`;
  }
  return "ok";
}

/** Independent look at a zip's central directory (does not use the code under test). */
function rawCentralDirectory(zip: Uint8Array): { flags: number; method: number; name: string; dosDate: number }[] {
  const dv = new DataView(zip.buffer, zip.byteOffset, zip.byteLength);
  const out = [];
  for (let i = 0; i + 46 <= zip.length; i++) {
    if (dv.getUint32(i, true) !== 0x02014b50) continue;
    const nameLen = dv.getUint16(i + 28, true);
    out.push({
      flags: dv.getUint16(i + 8, true),
      method: dv.getUint16(i + 10, true),
      dosDate: dv.getUint16(i + 14, true),
      name: new TextDecoder().decode(zip.subarray(i + 46, i + 46 + nameLen)),
    });
  }
  return out;
}

describe("archiveConverter manifest", () => {
  it("declares arc.zip and arc.extract only", () => {
    expect(archiveConverter.operations).toEqual(["arc.zip", "arc.extract"]);
    for (const op of archiveConverter.operations) {
      expect(archiveConverter.options(op)).toEqual([]);
      expect(archiveConverter.runtime(op, "zip")).toBe("worker");
    }
    expect(archiveConverter.suffix("arc.zip")).toBe("archive");
    expect(archiveConverter.suffix("arc.extract")).toBe("extracted");
    expect(archiveConverter.keepOutputNames?.("arc.extract")).toBe(true);
    expect(archiveConverter.keepOutputNames?.("arc.zip")).toBe(false);
    expect(archiveConverter.maxBytes?.("arc.extract")).toBe(1024 * MIB);
    expect(archiveConverter.maxBytes?.("arc.zip")).toBe(2048 * MIB);
  });

  it("arc.zip accepts any extension (including none); arc.extract only zip/tar/gz/tgz", () => {
    for (const ext of ["png", "pdf", "", "zip", "weird", "7z"]) expect(archiveConverter.supports("arc.zip", ext), ext).toBe(true);
    for (const ext of ["zip", "tar", "gz", "tgz", "ZIP", "Tgz"]) expect(archiveConverter.supports("arc.extract", ext), ext).toBe(true);
    for (const ext of ["7z", "rar", "png", "", "bz2", "xz", "docx"]) expect(archiveConverter.supports("arc.extract", ext), ext).toBe(false);
    expect(archiveConverter.supports("convert:zip", "zip")).toBe(false);
    expect(archiveConverter.supports("arc.other", "zip")).toBe(false);
  });

  it("tool ids exist in the catalog for the web platform", () => {
    for (const op of archiveConverter.operations) {
      const tool = catalog.tools.find((t) => t.id === op);
      expect(tool, op).toBeDefined();
      expect(tool!.platforms).toContain("web");
    }
    expect(catalog.tools.find((t) => t.id === "arc.zip")!.multi).toBe(true);
  });
});

describe("arc.zip (create)", () => {
  it("plans unique, sanitised names and chooses store for compressed formats", () => {
    expect(planZipNames([{ name: "a.txt" }, { name: "a.txt" }, { name: "A.TXT" }, { name: "dir/evil:name?.txt" }, { name: "README" }, { name: "README" }])).toEqual([
      "a.txt",
      "a (2).txt",
      "A (3).txt",
      "dir_evil_name_.txt",
      "README",
      "README (2)",
    ]);
    for (const n of ["x.png", "x.JPG", "x.mp4", "x.zip", "x.docx", "x.pdf", "x.mp3"]) expect(zipLevelFor(n), n).toBe(0);
    for (const n of ["x.txt", "x.csv", "x.bmp", "x.wav", "x.json", "README", "x.svg"]) expect(zipLevelFor(n), n).toBe(6);
  });

  it("creates a valid zip that an independent reader (fflate unzipSync) opens: Arabic + duplicate + extension-less + big + empty", async () => {
    const big = randomBytes(10 * MIB + 17); // spans three 4 MiB read slices
    const png = Uint8Array.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, ...randomBytes(100)]);
    const modified = Date.UTC(2023, 5, 15, 12, 0, 0);
    const files = [
      new File(["مرحبا بالعالم"], "مرحبا.txt", { lastModified: modified }),
      new File(["first"], "a.txt", { lastModified: modified }),
      new File(["second"], "a.txt", { lastModified: modified }),
      new File(["no extension here"], "README"),
      new File([png], "pic.png"),
      new File([big], "big.bin"),
      new File([], "empty.txt"),
    ];
    const seen: number[] = [];
    const blob = await createZip(files, (f) => seen.push(f), signal());
    expect(blob.type).toBe("application/zip");
    expect(seen.at(-1)).toBe(1);
    expect(seen.every((v, i) => i === 0 || v >= seen[i - 1])).toBe(true);

    const zipBytes = new Uint8Array(await blob.arrayBuffer());
    const infos: UnzipFileInfo[] = [];
    const out = unzipSync(zipBytes, {
      filter(info) {
        infos.push(info);
        return true;
      },
    });
    expect(Object.keys(out)).toEqual(["مرحبا.txt", "a.txt", "a (2).txt", "README", "pic.png", "big.bin", "empty.txt"]);
    expect(dec(out["مرحبا.txt"])).toBe("مرحبا بالعالم");
    expect(dec(out["a.txt"])).toBe("first");
    expect(dec(out["a (2).txt"])).toBe("second");
    expect(dec(out["README"])).toBe("no extension here");
    expect(Buffer.from(out["pic.png"]).equals(Buffer.from(png))).toBe(true);
    expect(Buffer.from(out["big.bin"]).equals(big)).toBe(true);
    expect(out["empty.txt"].length).toBe(0);

    // png is stored (method 0), text is deflated (method 8)
    const byName = Object.fromEntries(infos.map((i) => [i.name, i]));
    expect(byName["pic.png"].compression).toBe(0);
    expect(byName["a.txt"].compression).toBe(8);

    // Central directory: UTF-8 flag (bit 11) set for the Arabic name, and the mtime year survived
    const cd = rawCentralDirectory(zipBytes);
    const arabic = cd.find((e) => e.name === "مرحبا.txt")!;
    expect(arabic.flags & 0x800).toBe(0x800);
    expect(((arabic.dosDate >> 9) & 127) + 1980).toBe(2023);
    expect((arabic.dosDate >> 5) & 15).toBe(6);

    // And our own reader agrees
    const ours = extractZipBytes(zipBytes);
    expect(ours.map((i) => i.path)).toEqual(Object.keys(out));
  });

  it("refuses more than 1.5 GiB of input before reading anything", async () => {
    const fake = { name: "big.iso", size: MAX_ZIP_INPUT_BYTES + 1, lastModified: Date.now(), slice: () => { throw new Error("must not read"); } } as unknown as File;
    expect(await codeOf(createZip([fake], noop, signal()))).toBe("tooLarge");
  });

  it("clamps impossible modification times instead of throwing", async () => {
    const files = [new File(["x"], "old.txt", { lastModified: 1 }), new File(["y"], "future.txt", { lastModified: Date.UTC(2200, 0, 1) })];
    const blob = await createZip(files, noop, signal());
    const out = unzipSync(new Uint8Array(await blob.arrayBuffer()));
    expect(Object.keys(out)).toEqual(["old.txt", "future.txt"]);
  });

  it("honours cancellation and never touches the inputs", async () => {
    const f = new File(["data"], "x.txt");
    const ac = new AbortController();
    ac.abort();
    await expect(createZip([f], noop, ac.signal)).rejects.toMatchObject({ name: "AbortError" });
    expect(await f.text()).toBe("data");
    expect(f.name).toBe("x.txt");
  });
});

describe("arc.extract (zip)", () => {
  it("extracts nested folders, Arabic paths and sets types and mtimes", () => {
    const zip = zipSync({
      "readme.txt": [enc("hello"), { mtime: new Date(2022, 0, 2, 3, 4, 6) }],
      "dir/sub/data.json": enc('{"a":1}'),
      "مجلد/ملف.txt": enc("مرحبا"),
      "dir/": new Uint8Array(0),
      "dir/empty.bin": new Uint8Array(0),
    });
    const items = extractZipBytes(zip);
    expect(items.map((i) => i.path).sort()).toEqual(["dir/empty.bin", "dir/sub/data.json", "readme.txt", "مجلد/ملف.txt"].sort());
    const byPath = Object.fromEntries(items.map((i) => [i.path, i]));
    expect(dec(byPath["dir/sub/data.json"].data)).toBe('{"a":1}');
    expect(dec(byPath["مجلد/ملف.txt"].data)).toBe("مرحبا");
    expect(byPath["dir/empty.bin"].data.length).toBe(0);
    expect(byPath["readme.txt"].mtime?.getFullYear()).toBe(2022);
  });

  it("skips entries with unsafe names (zip-slip) and keeps the safe ones", () => {
    const zip = buildZip([
      { name: "../evil.txt", data: enc("evil") },
      { name: "/abs.txt", data: enc("abs") },
      { name: "ok.txt", data: enc("ok"), method: 8 },
      { name: "a/../../b.txt", data: enc("traversal") },
      { name: "C:\\win.txt", data: enc("win") },
      { name: "..\\..\\x.txt", data: enc("x") },
      { name: "nul\u0000.txt", data: enc("nul") },
    ]);
    const items = extractZipBytes(zip);
    expect(items.map((i) => i.path)).toEqual(["abs.txt", "ok.txt", "win.txt"]);
    for (const i of items) expect(i.path.split("/")).not.toContain("..");
    expect(dec(items[0].data)).toBe("abs");
  });

  it("throws emptyArchive when every entry is rejected, or the archive has no files", () => {
    const evil = buildZip([{ name: "../a.txt", data: enc("a") }, { name: "x/../../b.txt", data: enc("b") }]);
    expect(sync(() => extractZipBytes(evil))).toBe("emptyArchive");
    expect(sync(() => extractZipBytes(buildZip([])))).toBe("emptyArchive");
    expect(sync(() => extractZipBytes(buildZip([{ name: "only-a-folder/" }])))).toBe("emptyArchive");
  });

  it("makes duplicate names unique after sanitising (case-insensitive)", () => {
    const zip = buildZip([
      { name: "a.txt", data: enc("1") },
      { name: "A.TXT", data: enc("2") },
      { name: "x/y.txt", data: enc("3") },
      { name: "x\\y.txt", data: enc("4") },
      { name: "./a.txt", data: enc("5") },
    ]);
    expect(extractZipBytes(zip).map((i) => i.path)).toEqual(["a.txt", "A (2).txt", "x/y.txt", "x/y (2).txt", "a (3).txt"]);
  });

  it("skips symlink entries", () => {
    const zip = buildZip([
      { name: "link", data: enc("/etc/passwd"), madeBy: (3 << 8) | 20, externalAttrs: (0o120777 << 16) >>> 0 },
      { name: "real.txt", data: enc("real"), madeBy: (3 << 8) | 20, externalAttrs: (0o100644 << 16) >>> 0 },
    ]);
    expect(extractZipBytes(zip).map((i) => i.path)).toEqual(["real.txt"]);
  });

  it("detects a zip bomb from the header before inflating (ratio and per-entry caps)", () => {
    const zeros = new Uint8Array(64 * MIB); // deflates to ~64 KB: ratio > 1000
    const bomb = buildZip([{ name: "bomb.bin", data: zeros, method: 8 }]);
    expect(bomb.length).toBeLessThan(100_000);
    expect(sync(() => extractZipBytes(bomb))).toBe("tooLarge");

    // header claiming 600 MiB (> 512 MiB per-entry cap) - nothing is inflated
    const huge = buildZip([{ name: "huge.bin", data: enc("tiny"), claimedSize: 600 * MIB }]);
    expect(sync(() => extractZipBytes(huge))).toBe("tooLarge");
  });

  it("enforces the entry-count and total-size caps (and custom limits)", () => {
    const many = buildZip(Array.from({ length: LIMITS.maxEntries + 1 }, (_, i) => ({ name: `f${i}.txt`, data: enc("x") })));
    expect(sync(() => extractZipBytes(many))).toBe("tooManyFiles");

    const three = buildZip([0, 1, 2].map((i) => ({ name: `f${i}.bin`, data: new Uint8Array(40) })));
    const tiny = { ...LIMITS, maxTotalBytes: 100 };
    expect(sync(() => extractZipBytes(three, noop, signal(), tiny))).toBe("tooLarge");
    expect(sync(() => extractZipBytes(three, noop, signal(), { ...LIMITS, maxEntries: 2 }))).toBe("tooManyFiles");
    expect(sync(() => extractZipBytes(three, noop, signal(), { ...LIMITS, maxEntryBytes: 39 }))).toBe("tooLarge");
  });

  it("reports encrypted and unsupported entries", () => {
    expect(sync(() => extractZipBytes(buildZip([{ name: "a.txt", data: enc("x"), flags: 0x801 }])))).toBe("encrypted");
    expect(sync(() => extractZipBytes(buildZip([{ name: "a.txt", data: enc("x"), method: 12 as 0 }])))).toBe("unsupportedArchive");
  });

  it("detects corruption: bad CRC, headers that lie about the size, truncation, garbage", () => {
    const good = buildZip([{ name: "a.txt", data: enc("hello world, hello world"), method: 8 }]);
    const flipped = good.slice();
    flipped[flipped.indexOf(0x50) + 40] ^= 0xff; // inside the entry data
    expect(sync(() => extractZipBytes(flipped))).toBe("corrupt");

    const lyingLow = buildZip([{ name: "a.txt", data: new Uint8Array(100_000).fill(1), method: 8, claimedSize: 50 }]);
    expect(sync(() => extractZipBytes(lyingLow))).toBe("corrupt");

    const wrongCrc = buildZip([{ name: "a.txt", data: enc("hello"), crc: 12345 }]);
    expect(sync(() => extractZipBytes(wrongCrc))).toBe("corrupt");

    expect(sync(() => extractZipBytes(good.subarray(0, 30)))).toBe("corrupt");
    expect(sync(() => extractZipBytes(enc("definitely not a zip")))).toBe("corrupt");
  });

  it("honours cancellation", () => {
    const ac = new AbortController();
    ac.abort();
    expect(() => extractZipBytes(buildZip([{ name: "a.txt", data: enc("x") }]), noop, ac.signal)).toThrowError(/Cancelled|abort/i);
  });
});

describe("arc.extract (tar, gz, tgz)", () => {
  it("extracts a plain tar, skipping evil names", () => {
    const tar = buildTar([
      { name: "dir/", type: "5" },
      { name: "dir/a.txt", data: enc("a") },
      { name: "../evil", data: enc("evil") },
      { name: "/abs/path.txt", data: enc("abs") },
      { name: "ملف.txt", data: enc("مرحبا") },
    ]);
    const items = extractTarBytes(tar);
    expect(items.map((i) => i.path)).toEqual(["dir/a.txt", "abs/path.txt", "ملف.txt"]);
    expect(dec(items[2].data)).toBe("مرحبا");
    expect(sync(() => extractTarBytes(buildTar([{ name: "../evil", data: enc("e") }])))).toBe("emptyArchive");
  });

  it("a .gz of a single file extracts to the name without .gz; the extension is sniffed when missing", () => {
    const text = extractGzipBytes(gzipSync(enc("hello")), "notes.txt.gz");
    expect(text.map((i) => [i.path, dec(i.data)])).toEqual([["notes.txt", "hello"]]);

    const png = Uint8Array.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3, 4]);
    expect(extractGzipBytes(gzipSync(png), "data.gz")[0].path).toBe("data.png");
    expect(extractGzipBytes(gzipSync(randomBytes(64)), "blob.GZ")[0].path).toBe("blob");
    expect(extractGzipBytes(gzipSync(enc("x")), ".gz")[0].path).toBe("file.txt");
  });

  it("a gzip containing a tar (tgz and tar.gz) is extracted as a tar", () => {
    const tar = buildTar([
      { name: "src/main.ts", data: enc("console.log(1)") },
      { name: "README.md", data: enc("# hi") },
    ]);
    const gz = gzipSync(tar);
    for (const name of ["backup.tgz", "backup.tar.gz", "weird.gz"]) {
      expect(extractGzipBytes(gz, name).map((i) => i.path), name).toEqual(["src/main.ts", "README.md"]);
    }
  });

  it("detects gzip corruption: bad magic, wrong CRC, lying ISIZE", () => {
    const gz = gzipSync(enc("hello world hello world"));
    expect(sync(() => extractGzipBytes(new Uint8Array(40), "a.gz"))).toBe("corrupt");

    const badCrc = gz.slice();
    badCrc[badCrc.length - 8] ^= 0xff;
    expect(sync(() => extractGzipBytes(badCrc, "a.gz"))).toBe("corrupt");

    const lyingIsize = gzipSync(new Uint8Array(100_000).fill(65));
    lyingIsize[lyingIsize.length - 4] = 10;
    lyingIsize[lyingIsize.length - 3] = 0;
    lyingIsize[lyingIsize.length - 2] = 0;
    lyingIsize[lyingIsize.length - 1] = 0;
    expect(sync(() => extractGzipBytes(lyingIsize, "a.gz"))).toBe("corrupt");
  });

  it("refuses a gzip bomb by its trailer before inflating", () => {
    const bomb = gzipSync(new Uint8Array(64 * MIB));
    expect(bomb.length).toBeLessThan(100_000);
    expect(sync(() => extractGzipBytes(bomb, "bomb.gz"))).toBe("tooLarge");
  });

  it("the gzip layer may be as large as the total cap (not just the per-entry cap)", () => {
    const data = randomBytes(300_000);
    const gz = gzipSync(data);
    const limits = { ...LIMITS, maxEntryBytes: 100_000, maxTotalBytes: 400_000 };
    expect(extractGzipBytes(gz, "big.gz", noop, signal(), limits)[0].data.length).toBe(300_000);
    expect(sync(() => extractGzipBytes(gz, "big.gz", noop, signal(), { ...limits, maxTotalBytes: 200_000 }))).toBe("tooLarge");
  });
});

describe("detectArchiveKind / extractArchiveBytes", () => {
  it("content beats the extension; unknown content falls back to zip/tar by extension", () => {
    const zip = zipSync({ "a.txt": enc("a") });
    const gz = gzipSync(enc("x"));
    const tar = buildTar([{ name: "a", data: enc("a") }]);
    expect(detectArchiveKind(zip, "tar")).toBe("zip");
    expect(detectArchiveKind(gz, "zip")).toBe("gz");
    expect(detectArchiveKind(tar, "zip")).toBe("tar");
    expect(detectArchiveKind(enc("junk"), "zip")).toBe("zip");
    expect(detectArchiveKind(enc("junk"), "tar")).toBe("tar");
    expect(detectArchiveKind(enc("junk"), "gz")).toBeNull();
    expect(detectArchiveKind(enc("junk"), "7z")).toBeNull();
  });

  it("errors by situation", () => {
    expect(sync(() => extractArchiveBytes(enc("junk"), "a.zip", noop, signal()))).toBe("corrupt");
    expect(sync(() => extractArchiveBytes(enc("junk"), "a.gz", noop, signal()))).toBe("corrupt");
    expect(sync(() => extractArchiveBytes(enc("junk"), "a.rar", noop, signal()))).toBe("unsupportedArchive");
  });
});

describe("archiveConverter.run", () => {
  it("arc.zip -> arc.extract round-trips contents and names (Arabic, nested names are flattened by sanitising)", async () => {
    const files = [new File(["محتوى"], "ملف.txt"), new File([randomBytes(5000)], "data.bin"), new File(["x"], "no_ext")];
    const [zip] = await archiveConverter.run(job("arc.zip", files), noop, signal());
    expect(zip.name).toBe("archive.zip");
    expect(zip.type).toBe("application/zip");
    const [first, ...rest] = await archiveConverter.run(job("arc.extract", [new File([zip], "archive.zip")]), noop, signal());
    expect([first, ...rest].map((f) => f.name)).toEqual(["ملف.txt", "data.bin", "no_ext"]);
    expect(await first.text()).toBe("محتوى");
    expect(first.type).toBe("text/plain;charset=utf-8");
    expect(Buffer.from(await rest[0].arrayBuffer()).equals(Buffer.from(await files[1].arrayBuffer()))).toBe(true);
  });

  it("arc.extract returns Files whose names carry the folder path and whose types come from the extension", async () => {
    const zip = zipSync({ "dir/sub/pic.png": Uint8Array.from([1, 2, 3]), "dir/notes.txt": enc("hi") });
    const out = await archiveConverter.run(job("arc.extract", [new File([zip], "stuff.zip")]), noop, signal());
    expect(out.map((f) => [f.name, f.type])).toEqual([
      ["dir/sub/pic.png", "image/png"],
      ["dir/notes.txt", "text/plain;charset=utf-8"],
    ]);
  });

  it("arc.extract works on tar, tgz and gz files given as File objects", async () => {
    const tar = buildTar([{ name: "a/b.txt", data: enc("tar!") }]);
    const t = await archiveConverter.run(job("arc.extract", [new File([gzipSync(tar)], "x.tgz")]), noop, signal());
    expect(t.map((f) => f.name)).toEqual(["a/b.txt"]);
    const g = await archiveConverter.run(job("arc.extract", [new File([gzipSync(enc("plain"))], "doc.txt.gz")]), noop, signal());
    expect(g.map((f) => f.name)).toEqual(["doc.txt"]);
    expect(await g[0].text()).toBe("plain");
  });

  it("does not modify the input archive and honours cancellation", async () => {
    const zip = zipSync({ "a.txt": enc("a") });
    const input = new File([zip], "keep.zip");
    await archiveConverter.run(job("arc.extract", [input]), noop, signal());
    expect(input.name).toBe("keep.zip");
    expect(input.size).toBe(zip.length);
    const ac = new AbortController();
    ac.abort();
    await expect(archiveConverter.run(job("arc.extract", [input]), noop, ac.signal)).rejects.toMatchObject({ name: "AbortError" });
  });

  it("unknown operations fail cleanly", async () => {
    await expect(archiveConverter.run(job("arc.nope", [new File(["x"], "a")]), noop, signal())).rejects.toMatchObject({ code: "unsupported" });
  });
});

