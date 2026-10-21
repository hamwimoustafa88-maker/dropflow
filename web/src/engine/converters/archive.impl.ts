// Worker-side archive code (see archive.ts). Creation streams files through fflate in slices; extraction validates
// every entry (path, caps, CRC) before returning NEW File objects. Nothing here touches the network.

import { Zip, ZipDeflate, ZipPassThrough, gunzipSync, inflateSync } from "fflate";
import { extOf, sanitizeFileName, unique } from "../names";
import { looksLikeTar, readTar } from "../lib/tar";
import { mimeOfExt, sniffExtension } from "../lib/sniff";
import { LIMITS, checkLimits, crc32, readZipDirectory, sanitizeEntryPath, zipEntryRange, type ArchiveLimits } from "../lib/zip-safe";
import { ConvertError, throwIfAborted, type Job, type ProgressFn } from "../types";

const MIB = 1024 * 1024;
/** arc.zip refuses more than this many input bytes in total. */
export const MAX_ZIP_INPUT_BYTES = 1.5 * 1024 * MIB;
const READ_SLICE = 4 * MIB;

// Formats that are already compressed (or zip containers): storing them avoids wasting time for ~0% gain.
const STORE_EXTS = new Set([
  "png", "jpg", "jpeg", "webp", "gif", "avif", "heic", "heif",
  "mp3", "m4a", "aac", "ogg", "opus", "flac", "wma",
  "mp4", "m4v", "mov", "mkv", "webm", "wmv", "flv",
  "zip", "gz", "tgz", "7z", "rar", "bz2", "xz", "zst",
  "docx", "xlsx", "pptx", "odt", "ods", "odp", "epub", "jar", "apk",
  "pdf",
]);

export function zipLevelFor(fileName: string): 0 | 6 {
  return STORE_EXTS.has(extOf(fileName)) ? 0 : 6;
}

/** Sanitised, unique names for the entries of a new zip ("a.txt", "a (2).txt"...). Entries sit at the zip root. */
export function planZipNames(files: readonly { name: string }[]): string[] {
  const taken = new Set<string>();
  return files.map((f) => {
    const name = unique(sanitizeFileName(f.name), taken);
    taken.add(name.toLowerCase());
    return name;
  });
}

function clampDate(ms: number): Date {
  const min = Date.UTC(1980, 0, 2);
  const max = Date.UTC(2099, 11, 30);
  const t = Number.isFinite(ms) && ms > 0 ? ms : Date.now();
  return new Date(Math.min(max, Math.max(min, t)));
}

/** Streams the files into a ZIP and returns it as a Blob (UTF-8 names, mtime from File.lastModified). */
export async function createZip(files: readonly File[], onProgress: ProgressFn, signal: AbortSignal): Promise<Blob> {
  const total = files.reduce((n, f) => n + f.size, 0);
  if (total > MAX_ZIP_INPUT_BYTES) throw new ConvertError("tooLarge");
  const names = planZipNames(files);
  const chunks: Uint8Array[] = [];
  let failure: unknown = null;
  const zip = new Zip((err, chunk) => {
    if (err) failure = err;
    else chunks.push(chunk);
  });
  let done = 0;
  for (let i = 0; i < files.length; i++) {
    throwIfAborted(signal);
    const file = files[i];
    const level = zipLevelFor(file.name);
    const entry = level === 0 ? new ZipPassThrough(names[i]) : new ZipDeflate(names[i], { level });
    entry.mtime = clampDate(file.lastModified);
    zip.add(entry);
    if (file.size === 0) {
      entry.push(new Uint8Array(0), true);
    } else {
      for (let off = 0; off < file.size; off += READ_SLICE) {
        throwIfAborted(signal);
        const piece = new Uint8Array(await file.slice(off, off + READ_SLICE).arrayBuffer());
        entry.push(piece, off + READ_SLICE >= file.size);
        done += piece.length;
        onProgress(Math.min(0.99, done / Math.max(1, total)));
      }
    }
    if (failure) throw failure instanceof Error ? failure : new ConvertError("corrupt");
  }
  zip.end();
  if (failure) throw failure instanceof Error ? failure : new ConvertError("corrupt");
  onProgress(1);
  return new Blob(chunks as BlobPart[], { type: "application/zip" });
}

// ---------------------------------------------------------------------------------------------------------------------
// Extraction

export interface ExtractedItem {
  /** Sanitised relative path with "/" separators; unique among the items. */
  path: string;
  data: Uint8Array;
  mtime: Date | null;
}

class Collector {
  readonly items: ExtractedItem[] = [];
  rejected = 0;
  private readonly taken = new Set<string>();

  add(rawName: string, data: Uint8Array, mtime: Date | null): void {
    const path = sanitizeEntryPath(rawName);
    if (!path) {
      this.rejected++; // unsafe name (zip-slip...): skip this entry, keep the others
      return;
    }
    const finalPath = unique(path, this.taken);
    this.taken.add(finalPath.toLowerCase());
    this.items.push({ path: finalPath, data, mtime });
  }

  result(): ExtractedItem[] {
    if (this.items.length === 0) throw new ConvertError("emptyArchive");
    return this.items;
  }
}

export function extractZipBytes(bytes: Uint8Array, onProgress: ProgressFn = () => {}, signal: AbortSignal = new AbortController().signal, limits: ArchiveLimits = LIMITS): ExtractedItem[] {
  const files = readZipDirectory(bytes, limits.maxEntries).filter((e) => !e.isDirectory && !e.isSymlink);
  checkLimits(files, limits);
  if (files.some((e) => e.encrypted)) throw new ConvertError("encrypted");
  if (files.some((e) => e.method !== 0 && e.method !== 8)) throw new ConvertError("unsupportedArchive");
  const out = new Collector();
  files.forEach((entry, i) => {
    throwIfAborted(signal);
    if (!sanitizeEntryPath(entry.name)) {
      out.rejected++;
      return;
    }
    const [start, end] = zipEntryRange(bytes, entry);
    let data: Uint8Array;
    if (entry.method === 0) {
      data = bytes.subarray(start, end);
    } else {
      try {
        // The output buffer has exactly the declared size: a header that lies low gets truncated data (caught by the CRC below).
        data = inflateSync(bytes.subarray(start, end), { out: new Uint8Array(entry.size) });
      } catch {
        throw new ConvertError("corrupt");
      }
    }
    if (data.length !== entry.size || crc32(data) !== entry.crc) throw new ConvertError("corrupt");
    out.add(entry.name, data, entry.mtime);
    onProgress((i + 1) / files.length);
  });
  return out.result();
}

export function extractTarBytes(bytes: Uint8Array, onProgress: ProgressFn = () => {}, signal: AbortSignal = new AbortController().signal, limits: ArchiveLimits = LIMITS): ExtractedItem[] {
  const entries = readTar(bytes, limits.maxEntries);
  checkLimits(entries.map((e) => ({ size: e.size, compressedSize: e.size })), limits);
  const out = new Collector();
  entries.forEach((e, i) => {
    throwIfAborted(signal);
    out.add(e.name, e.data, e.mtime);
    onProgress((i + 1) / entries.length);
  });
  return out.result();
}

/** .gz / .tgz: a tar inside is extracted as a tar, anything else becomes one file named like the archive without .gz. */
export function extractGzipBytes(bytes: Uint8Array, fileName: string, onProgress: ProgressFn = () => {}, signal: AbortSignal = new AbortController().signal, limits: ArchiveLimits = LIMITS): ExtractedItem[] {
  if (bytes.length < 18 || bytes[0] !== 0x1f || bytes[1] !== 0x8b) throw new ConvertError("corrupt");
  const n = bytes.length;
  const isize = (bytes[n - 4] | (bytes[n - 3] << 8) | (bytes[n - 2] << 16) | (bytes[n - 1] << 24)) >>> 0;
  const storedCrc = (bytes[n - 8] | (bytes[n - 7] << 8) | (bytes[n - 6] << 16) | (bytes[n - 5] << 24)) >>> 0;
  // The gzip layer may hold a whole tar, so it may be as large as the total cap (not just the per-entry cap).
  checkLimits([{ size: isize, compressedSize: n }], { ...limits, maxEntryBytes: limits.maxTotalBytes });
  throwIfAborted(signal);
  let raw: Uint8Array;
  try {
    raw = gunzipSync(bytes, { out: new Uint8Array(isize) });
  } catch {
    throw new ConvertError("corrupt");
  }
  if (raw.length !== isize || crc32(raw) !== storedCrc) throw new ConvertError("corrupt");
  throwIfAborted(signal);
  if (looksLikeTar(raw)) return extractTarBytes(raw, onProgress, signal, limits);

  const out = new Collector();
  let name = fileName.replace(/\.(?:gz|tgz)$/i, "") || "file";
  if (!extOf(name)) {
    const guessed = sniffExtension(raw);
    if (guessed !== "bin") name = `${name}.${guessed}`;
  }
  out.add(name, raw, null);
  onProgress(1);
  return out.result();
}

export type ArchiveKind = "zip" | "gz" | "tar";

/** The content decides; the extension only breaks ties for files without a recognisable signature. */
export function detectArchiveKind(bytes: Uint8Array, ext: string): ArchiveKind | null {
  const sniffed = sniffExtension(bytes.subarray(0, 600));
  if (sniffed === "zip" || sniffed === "gz" || sniffed === "tar") return sniffed;
  if (ext === "zip") return "zip";
  if (ext === "tar") return "tar";
  return null;
}

export function extractArchiveBytes(bytes: Uint8Array, fileName: string, onProgress: ProgressFn, signal: AbortSignal, limits: ArchiveLimits = LIMITS): ExtractedItem[] {
  const ext = extOf(fileName);
  const kind = detectArchiveKind(bytes, ext);
  if (kind === "zip") return extractZipBytes(bytes, onProgress, signal, limits);
  if (kind === "gz") return extractGzipBytes(bytes, fileName, onProgress, signal, limits);
  if (kind === "tar") return extractTarBytes(bytes, onProgress, signal, limits);
  // A .gz/.tgz that is not gzip is damaged; anything else is not an archive we know.
  throw new ConvertError(ext === "gz" || ext === "tgz" ? "corrupt" : "unsupportedArchive");
}

export async function runArchive(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
  if (job.operation === "arc.zip") {
    if (job.files.length === 0) throw new ConvertError("unsupported");
    const blob = await createZip(job.files, onProgress, signal);
    return [new File([blob], "archive.zip", { type: "application/zip", lastModified: Date.now() })];
  }
  if (job.operation === "arc.extract") {
    const file = job.files[0];
    if (!file) throw new ConvertError("unsupported");
    let bytes: Uint8Array;
    try {
      bytes = new Uint8Array(await file.arrayBuffer());
    } catch {
      throw new ConvertError("tooLarge");
    }
    throwIfAborted(signal);
    const items = extractArchiveBytes(bytes, file.name, onProgress, signal);
    onProgress(1);
    return items.map((item) => new File([item.data as BlobPart], item.path, { type: mimeOfExt(extOf(item.path)), lastModified: item.mtime?.getTime() ?? Date.now() }));
  }
  throw new ConvertError("unsupported");
}
