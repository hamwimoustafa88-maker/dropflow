import { createHash, randomBytes } from "node:crypto";
import { describe, expect, it } from "vitest";
import catalog from "../../data/catalog.json";
import { generalConverter } from "../converters/general";
import { ConvertError, type Job, type OptionValues } from "../types";

function job(op: string, file: File, options: OptionValues = {}): Job {
  return { id: "j", operation: op, files: [file], options };
}
const noop = () => {};
const signal = () => new AbortController().signal;
const run = (op: string, file: File, options: OptionValues = {}) => generalConverter.run(job(op, file, options), noop, signal());

describe("generalConverter manifest", () => {
  it("declares gen.hash and gen.base64 with the documented options", () => {
    expect(generalConverter.operations).toEqual(["gen.hash", "gen.base64"]);
    expect(generalConverter.options("gen.hash")).toEqual([
      { key: "algorithm", kind: "choice", default: "sha-256", choices: ["sha-256", "sha-1", "sha-384", "sha-512", "md5", "all"] },
    ]);
    expect(generalConverter.options("gen.base64")).toEqual([
      { key: "mode", kind: "choice", default: "encode", choices: ["encode", "decode"] },
      { key: "urlSafe", kind: "bool", default: false },
    ]);
    expect(generalConverter.options("unknown")).toEqual([]);
    expect(generalConverter.suffix("gen.hash")).toBe("hash");
    expect(generalConverter.suffix("gen.base64")).toBe("base64");
    expect(generalConverter.maxBytes?.("gen.hash")).toBe(1024 * 1024 * 1024);
    expect(generalConverter.maxBytes?.("gen.base64")).toBe(300 * 1024 * 1024);
    for (const op of generalConverter.operations) expect(generalConverter.runtime(op, "png")).toBe("worker");
  });

  it("supports any extension, including none, and only its own operations", () => {
    for (const ext of ["png", "pdf", "", "weird", "TXT", "tar.gz"]) {
      expect(generalConverter.supports("gen.hash", ext), ext).toBe(true);
      expect(generalConverter.supports("gen.base64", ext), ext).toBe(true);
    }
    expect(generalConverter.supports("gen.other", "png")).toBe(false);
    expect(generalConverter.supports("convert:png", "png")).toBe(false);
  });

  it("tool ids exist in the catalog for the web platform", () => {
    for (const op of generalConverter.operations) {
      const tool = catalog.tools.find((t) => t.id === op);
      expect(tool, op).toBeDefined();
      expect(tool!.platforms).toContain("web");
    }
  });
});

describe("gen.hash", () => {
  const nodeAlgo: Record<string, string> = { "sha-256": "sha256", "sha-1": "sha1", "sha-384": "sha384", "sha-512": "sha512", md5: "md5" };

  it("matches node:crypto for every algorithm in sha256sum format, with Arabic file names", async () => {
    const data = randomBytes(300_000);
    const file = new File([data], "تقرير نهائي.pdf");
    for (const algo of Object.keys(nodeAlgo)) {
      const [out] = await run("gen.hash", file, { algorithm: algo });
      expect(out.type).toBe("text/plain;charset=utf-8");
      expect(out.name).toBe("hash.txt");
      expect(await out.text()).toBe(`${createHash(nodeAlgo[algo]).update(data).digest("hex")}  تقرير نهائي.pdf\n`);
    }
  });

  it("'all' reports every algorithm on its own labelled line", async () => {
    const data = randomBytes(5 * 1024 * 1024 + 7); // spans several 4 MiB MD5 slices
    const [out] = await run("gen.hash", new File([data], "big.bin"), { algorithm: "all" });
    const lines = (await out.text()).trimEnd().split("\n");
    expect(lines.map((l) => l.split("  ")[0])).toEqual(["MD5", "SHA-1", "SHA-256", "SHA-384", "SHA-512"]);
    for (const line of lines) {
      const [label, hex, name] = line.split("  ");
      const algo = Object.keys(nodeAlgo).find((a) => a.toUpperCase() === label)!;
      expect(hex).toBe(createHash(nodeAlgo[algo]).update(data).digest("hex"));
      expect(name).toBe("big.bin");
    }
  });

  it("defaults to SHA-256 and handles the empty file", async () => {
    const [out] = await run("gen.hash", new File([], "empty.txt"));
    expect(await out.text()).toBe("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  empty.txt\n");
    const [md5] = await run("gen.hash", new File([], "empty.txt"), { algorithm: "md5" });
    expect(await md5.text()).toBe("d41d8cd98f00b204e9800998ecf8427e  empty.txt\n");
  });

  it("refuses files above the 1 GiB cap before reading them", async () => {
    const huge = { name: "huge.iso", size: 1024 * 1024 * 1024 + 1, arrayBuffer: () => Promise.reject(new Error("must not read")) } as unknown as File;
    await expect(run("gen.hash", huge)).rejects.toMatchObject({ code: "tooLarge" });
  });

  it("honours cancellation and never touches the input", async () => {
    const ac = new AbortController();
    ac.abort();
    const file = new File(["data"], "x.txt");
    await expect(generalConverter.run(job("gen.hash", file), noop, ac.signal)).rejects.toMatchObject({ name: "AbortError" });
    expect(await file.text()).toBe("data");
  });
});

describe("gen.base64", () => {
  it("encodes binary data exactly like Buffer (multi-slice input) and decodes it back", async () => {
    const data = randomBytes(7 * 1024 * 1024 + 5); // 3 slices of 3 MiB
    const [enc] = await run("gen.base64", new File([data], "blob.bin"), { mode: "encode" });
    expect(enc.name).toBe("base64.txt");
    expect(enc.type).toBe("text/plain;charset=utf-8");
    const text = await enc.text();
    expect(text).toBe(data.toString("base64"));
    expect(text).not.toMatch(/\s/);

    const [dec] = await run("gen.base64", new File([text], "blob.b64"), { mode: "decode" });
    expect(dec.name).toBe("decoded.bin");
    expect(Buffer.from(await dec.arrayBuffer()).equals(data)).toBe(true);
  });

  it("URL-safe encoding drops padding and uses -_", async () => {
    const data = Uint8Array.from([0xfb, 0xff, 0xfe, 0xff]);
    const [std] = await run("gen.base64", new File([data], "a"), { mode: "encode", urlSafe: false });
    const [url] = await run("gen.base64", new File([data], "a"), { mode: "encode", urlSafe: true });
    expect(await std.text()).toBe("+//+/w==");
    expect(await url.text()).toBe("-__-_w");
  });

  it("decode ignores whitespace/newlines, accepts data URLs, URL-safe text and missing padding", async () => {
    const png = Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), randomBytes(40)]);
    const wrapped = png.toString("base64").replace(/(.{20})/g, "$1\r\n");
    const [a] = await run("gen.base64", new File([wrapped], "w.txt"), { mode: "decode" });
    expect(a.name).toBe("decoded.png");
    expect(a.type).toBe("image/png");
    expect(Buffer.from(await a.arrayBuffer()).equals(png)).toBe(true);

    const [b] = await run("gen.base64", new File([`data:image/png;base64,${png.toString("base64")}`], "d.txt"), { mode: "decode" });
    expect(Buffer.from(await b.arrayBuffer()).equals(png)).toBe(true);

    const [c] = await run("gen.base64", new File([png.toString("base64url")], "u.txt"), { mode: "decode" });
    expect(Buffer.from(await c.arrayBuffer()).equals(png)).toBe(true);
  });

  it("decodes UTF-8 text to a .txt result the UI can copy", async () => {
    const [out] = await run("gen.base64", new File([Buffer.from("مرحبا بالعالم\n").toString("base64")], "t.txt"), { mode: "decode" });
    expect(out.name).toBe("decoded.txt");
    expect(out.type).toBe("text/plain;charset=utf-8");
    expect(await out.text()).toBe("مرحبا بالعالم\n");
  });

  it("decodes base64 saved with a UTF-8 BOM or as UTF-16", async () => {
    const b64 = Buffer.from("hello").toString("base64");
    const [bom] = await run("gen.base64", new File([Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from(b64)])], "bom.txt"), { mode: "decode" });
    expect(await bom.text()).toBe("hello");
    const utf16 = Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from(b64, "utf16le")]);
    const [u16] = await run("gen.base64", new File([utf16], "u16.txt"), { mode: "decode" });
    expect(await u16.text()).toBe("hello");
  });

  it("invalid base64 -> ConvertError('invalidBase64')", async () => {
    for (const content of ["", "not base64!!", "SGVsbG8*", "A", "====", "data:text/plain,hello"]) {
      const p = run("gen.base64", new File([content], "bad.txt"), { mode: "decode" });
      await expect(p).rejects.toBeInstanceOf(ConvertError);
      await expect(p).rejects.toMatchObject({ code: "invalidBase64" });
    }
  });

  it("refuses files above the 300 MiB cap", async () => {
    const huge = { name: "huge", size: 300 * 1024 * 1024 + 1 } as unknown as File;
    await expect(run("gen.base64", huge, { mode: "encode" })).rejects.toMatchObject({ code: "tooLarge" });
    await expect(run("gen.base64", huge, { mode: "decode" })).rejects.toMatchObject({ code: "tooLarge" });
  });

  it("defaults to encode when no mode is given and reports progress", async () => {
    const seen: number[] = [];
    const [out] = await generalConverter.run(job("gen.base64", new File(["abc"], "a.txt")), (f) => seen.push(f), signal());
    expect(await out.text()).toBe("YWJj");
    expect(seen.at(-1)).toBe(1);
  });
});
