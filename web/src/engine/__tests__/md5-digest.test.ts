import { createHash, randomBytes } from "node:crypto";
import { describe, expect, it } from "vitest";
import { ALL_ALGOS, algoLabel, algorithmsFor, formatHashReport, hashBlob, toHex, type HashAlgo } from "../lib/md5-digest";
import { ConvertError } from "../types";

const nodeAlgo: Record<HashAlgo, string> = { md5: "md5", "sha-1": "sha1", "sha-256": "sha256", "sha-384": "sha384", "sha-512": "sha512" };

describe("hashBlob", () => {
  it("matches node:crypto for every algorithm (multi-slice input)", async () => {
    const data = randomBytes(9 * 1024 * 1024 + 123);
    const results = await hashBlob(new Blob([data]), ALL_ALGOS);
    expect(results.map((r) => r.algo)).toEqual([...ALL_ALGOS]);
    for (const r of results) expect(r.hex, r.algo).toBe(createHash(nodeAlgo[r.algo]).update(data).digest("hex"));
  });

  it("matches node:crypto for small inputs and for the empty file", async () => {
    for (const data of [Buffer.alloc(0), Buffer.from("abc"), Buffer.from("مرحبا بالعالم")]) {
      for (const algo of ALL_ALGOS) {
        const [r] = await hashBlob(new Blob([data]), [algo]);
        expect(r.hex, `${algo} ${data.length}`).toBe(createHash(nodeAlgo[algo]).update(data).digest("hex"));
      }
    }
  });

  it("MD5-only streams slices and does not need WebCrypto", async () => {
    const data = randomBytes(5 * 1024 * 1024);
    const original = Object.getOwnPropertyDescriptor(globalThis, "crypto");
    Object.defineProperty(globalThis, "crypto", { value: undefined, configurable: true });
    try {
      const [r] = await hashBlob(new Blob([data]), ["md5"]);
      expect(r.hex).toBe(createHash("md5").update(data).digest("hex"));
    } finally {
      if (original) Object.defineProperty(globalThis, "crypto", original);
    }
  });

  it("reports progress up to 1 and honours an aborted signal", async () => {
    const seen: number[] = [];
    await hashBlob(new Blob([randomBytes(9 * 1024 * 1024)]), ["md5", "sha-256"], { onProgress: (f) => seen.push(f) });
    expect(seen.at(-1)).toBe(1);
    expect(Math.max(...seen)).toBeLessThanOrEqual(1);

    const ac = new AbortController();
    ac.abort();
    await expect(hashBlob(new Blob([randomBytes(1000)]), ["sha-256"], { signal: ac.signal })).rejects.toMatchObject({ name: "AbortError" });
  });

  it("throws noCrypto for SHA when WebCrypto is unavailable", async () => {
    const original = Object.getOwnPropertyDescriptor(globalThis, "crypto");
    Object.defineProperty(globalThis, "crypto", { value: undefined, configurable: true });
    try {
      await expect(hashBlob(new Blob(["x"]), ["sha-256"])).rejects.toMatchObject({ code: "noCrypto" });
      await expect(hashBlob(new Blob(["x"]), ["sha-256"])).rejects.toBeInstanceOf(ConvertError);
    } finally {
      if (original) Object.defineProperty(globalThis, "crypto", original);
    }
  });
});

describe("report formatting", () => {
  it("single algorithm: sha256sum style line", () => {
    const hex = createHash("sha256").update("abc").digest("hex");
    expect(formatHashReport([{ algo: "sha-256", hex }], "تقرير final.pdf")).toBe(`${hex}  تقرير final.pdf\n`);
  });

  it("several algorithms: one labelled line each; newlines in names are flattened", () => {
    const text = formatHashReport(
      [
        { algo: "md5", hex: "aa" },
        { algo: "sha-1", hex: "bb" },
      ],
      "a\nb.txt",
    );
    expect(text).toBe("MD5  aa  a b.txt\nSHA-1  bb  a b.txt\n");
  });

  it("labels, hex and option mapping", () => {
    expect(algoLabel("sha-512")).toBe("SHA-512");
    expect(toHex(Uint8Array.from([0, 15, 255]))).toBe("000fff");
    expect(toHex(Uint8Array.from([1, 2]).buffer)).toBe("0102");
    expect(algorithmsFor("all")).toEqual([...ALL_ALGOS]);
    expect(algorithmsFor("md5")).toEqual(["md5"]);
    expect(algorithmsFor("sha-1")).toEqual(["sha-1"]);
    expect(algorithmsFor("bogus")).toEqual(["sha-256"]);
  });
});
