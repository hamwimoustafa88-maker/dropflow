// Worker-side implementation of gen.hash and gen.base64 (see general.ts).

import { algorithmsFor, formatHashReport, hashBlob } from "../lib/md5-digest";
import { mimeOfExt, sniffExtension } from "../lib/sniff";
import { base64Parts, base64ToBytes } from "../lib/sniff-base64";
import { decodeText } from "../lib/text-decode";
import { ConvertError, throwIfAborted, type Job, type ProgressFn } from "../types";

const MIB = 1024 * 1024;
const MAX_HASH_BYTES = 1024 * MIB;
const MAX_BASE64_BYTES = 300 * MIB;
/** Multiple of 3 so every slice encodes to whole 4-character groups (no padding in the middle). */
const ENCODE_SLICE = 3 * MIB;
const TEXT = "text/plain;charset=utf-8";

function optString(job: Job, key: string, fallback: string): string {
  const v = job.options[key];
  return typeof v === "string" ? v : fallback;
}

function optBool(job: Job, key: string, fallback: boolean): boolean {
  const v = job.options[key];
  return typeof v === "boolean" ? v : fallback;
}

async function runHash(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
  const file = job.files[0];
  if (file.size > MAX_HASH_BYTES) throw new ConvertError("tooLarge");
  const algos = algorithmsFor(optString(job, "algorithm", "sha-256"));
  const results = await hashBlob(file, algos, { signal, onProgress: (f) => onProgress(f) });
  return [new File([formatHashReport(results, file.name)], "hash.txt", { type: TEXT })];
}

async function encodeBase64(file: File, urlSafe: boolean, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
  if (file.size > MAX_BASE64_BYTES) throw new ConvertError("tooLarge");
  const parts: Uint8Array[] = [];
  for (let off = 0; off < file.size; off += ENCODE_SLICE) {
    throwIfAborted(signal);
    const slice = new Uint8Array(await file.slice(off, off + ENCODE_SLICE).arrayBuffer());
    for (const part of base64Parts(slice, { urlSafe }, ENCODE_SLICE)) parts.push(part);
    onProgress(Math.min(0.99, (off + ENCODE_SLICE) / Math.max(1, file.size)));
  }
  onProgress(1);
  return [new File(parts as BlobPart[], "base64.txt", { type: TEXT })];
}

async function decodeBase64(file: File, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
  if (file.size > MAX_BASE64_BYTES) throw new ConvertError("tooLarge");
  const { text } = decodeText(new Uint8Array(await file.arrayBuffer()));
  throwIfAborted(signal);
  onProgress(0.5);
  const bytes = base64ToBytes(text); // throws ConvertError("invalidBase64")
  const ext = sniffExtension(bytes);
  onProgress(1);
  return [new File([bytes as BlobPart], `decoded.${ext}`, { type: mimeOfExt(ext) })];
}

export async function runGeneral(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
  const file = job.files[0];
  if (!file) throw new ConvertError("unsupported");
  throwIfAborted(signal);
  if (job.operation === "gen.hash") return runHash(job, onProgress, signal);
  if (job.operation === "gen.base64") {
    return optString(job, "mode", "encode") === "decode" ? decodeBase64(file, onProgress, signal) : encodeBase64(file, optBool(job, "urlSafe", false), onProgress, signal);
  }
  throw new ConvertError("unsupported");
}
