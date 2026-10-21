// File hashing used by gen.hash: SHA-1/256/384/512 through WebCrypto, MD5 through the pure Md5 class.

import { ConvertError, throwIfAborted } from "../types";
import { Md5 } from "./md5";

export type HashAlgo = "md5" | "sha-1" | "sha-256" | "sha-384" | "sha-512";

/** Report order for "all". */
export const ALL_ALGOS: readonly HashAlgo[] = ["md5", "sha-1", "sha-256", "sha-384", "sha-512"];

export const HASH_SLICE = 4 * 1024 * 1024;

export interface HashResult {
  algo: HashAlgo;
  hex: string;
}

export function algoLabel(a: HashAlgo): string {
  return a.toUpperCase();
}

export function toHex(bytes: ArrayBuffer | Uint8Array): string {
  const u = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
  let out = "";
  for (let i = 0; i < u.length; i++) out += u[i].toString(16).padStart(2, "0");
  return out;
}

export interface HashOptions {
  signal?: AbortSignal;
  onProgress?: (fraction: number) => void;
  subtle?: SubtleCrypto;
}

/**
 * Hashes a Blob with the given algorithms. MD5-only runs stream the blob in 4 MiB slices; any SHA needs the whole
 * content in memory because WebCrypto has no incremental digest (callers cap the input size).
 */
export async function hashBlob(blob: Blob, algos: readonly HashAlgo[], opts: HashOptions = {}): Promise<HashResult[]> {
  const signal = opts.signal ?? new AbortController().signal;
  const wantSha = algos.some((a) => a !== "md5");
  const subtle = opts.subtle ?? globalThis.crypto?.subtle;
  if (wantSha && !subtle) throw new ConvertError("noCrypto");

  const results = new Map<HashAlgo, string>();
  let whole: ArrayBuffer | null = null;
  if (wantSha) {
    whole = await blob.arrayBuffer();
    throwIfAborted(signal);
  }

  if (algos.includes("md5")) {
    const md5 = new Md5();
    for (let off = 0; off < blob.size; off += HASH_SLICE) {
      throwIfAborted(signal);
      const slice = whole ? new Uint8Array(whole, off, Math.min(HASH_SLICE, whole.byteLength - off)) : new Uint8Array(await blob.slice(off, off + HASH_SLICE).arrayBuffer());
      md5.update(slice);
      opts.onProgress?.(Math.min(1, (off + HASH_SLICE) / Math.max(1, blob.size)) * (wantSha ? 0.5 : 1));
    }
    results.set("md5", md5.digestHex());
  }

  if (wantSha && whole && subtle) {
    const shas = algos.filter((a): a is Exclude<HashAlgo, "md5"> => a !== "md5");
    for (let i = 0; i < shas.length; i++) {
      throwIfAborted(signal);
      const algo = shas[i];
      results.set(algo, toHex(await subtle.digest(algo.toUpperCase(), whole)));
      opts.onProgress?.(0.5 + (0.5 * (i + 1)) / shas.length);
    }
  }
  opts.onProgress?.(1);
  return algos.map((algo) => ({ algo, hex: results.get(algo) ?? "" }));
}

/**
 * `<hex>  <name>` per line (sha256sum style: `sha256sum -c` accepts it) for one algorithm; `<ALGO>  <hex>  <name>`
 * per line when several algorithms are reported.
 */
export function formatHashReport(results: readonly HashResult[], fileName: string): string {
  const name = fileName.replace(/[\r\n]+/g, " ");
  if (results.length === 1) return `${results[0].hex}  ${name}\n`;
  return results.map((r) => `${algoLabel(r.algo)}  ${r.hex}  ${name}`).join("\n") + "\n";
}

/** Option value -> list of algorithms ("all" expands to every algorithm). Unknown values fall back to SHA-256. */
export function algorithmsFor(option: string): readonly HashAlgo[] {
  if (option === "all") return ALL_ALGOS;
  return (ALL_ALGOS as readonly string[]).includes(option) ? [option as HashAlgo] : ["sha-256"];
}
