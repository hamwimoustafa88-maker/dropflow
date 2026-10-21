// "Download all" as one ZIP, built in the browser with fflate (streaming, in slices, UTF-8 names). Runs on the main
// thread because results are already in memory and mostly already-compressed (images, pdf, media are stored, not
// deflated). Entry paths come from the results ("folder/a.txt" for extracted archives, flat names otherwise).

export interface ZipEntry {
  path: string;
  file: File;
}

const SLICE = 4 * 1024 * 1024;
const STORE_EXTS = new Set([
  "png", "jpg", "jpeg", "webp", "gif", "avif", "bmp", "pdf", "mp3", "m4a", "aac", "ogg", "opus", "flac", "wma",
  "mp4", "m4v", "mov", "mkv", "webm", "wmv", "flv", "zip", "gz", "tgz", "7z", "rar", "docx", "xlsx", "pptx",
]);

export function zipLevelForPath(path: string): 0 | 6 {
  const i = path.lastIndexOf(".");
  return i >= 0 && STORE_EXTS.has(path.slice(i + 1).toLowerCase()) ? 0 : 6;
}

function clampDate(ms: number): Date {
  const min = Date.UTC(1980, 0, 2);
  const max = Date.UTC(2099, 11, 30);
  return new Date(Math.min(max, Math.max(min, Number.isFinite(ms) && ms > 0 ? ms : Date.now())));
}

export async function zipEntries(entries: readonly ZipEntry[], signal?: AbortSignal): Promise<Blob> {
  const { Zip, ZipDeflate, ZipPassThrough } = await import("fflate");
  const chunks: Uint8Array[] = [];
  let failure: unknown = null;
  const zip = new Zip((err, chunk) => {
    if (err) failure = err;
    else chunks.push(chunk);
  });
  for (const { path, file } of entries) {
    if (signal?.aborted) throw new DOMException("Cancelled", "AbortError");
    const level = zipLevelForPath(path);
    const entry = level === 0 ? new ZipPassThrough(path) : new ZipDeflate(path, { level });
    entry.mtime = clampDate(file.lastModified);
    zip.add(entry);
    if (file.size === 0) {
      entry.push(new Uint8Array(0), true);
    } else {
      for (let off = 0; off < file.size; off += SLICE) {
        const piece = new Uint8Array(await file.slice(off, off + SLICE).arrayBuffer());
        entry.push(piece, off + SLICE >= file.size);
      }
    }
    if (failure) throw failure instanceof Error ? failure : new Error("zip failed");
  }
  zip.end();
  if (failure) throw failure instanceof Error ? failure : new Error("zip failed");
  return new Blob(chunks as BlobPart[], { type: "application/zip" });
}
