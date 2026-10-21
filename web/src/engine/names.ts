// File-name helpers. DropFlow never overwrites or replaces an input: every result is a NEW file called
// `name (converted).ext`, with a counter when needed, mirroring desktop OutputPathResolver.

export function extOf(name: string): string {
  const base = name.split(/[\\/]/).pop() ?? name;
  const i = base.lastIndexOf(".");
  return i > 0 && i < base.length - 1 ? base.slice(i + 1).toLowerCase() : "";
}

export function stemOf(name: string): string {
  const base = name.split(/[\\/]/).pop() ?? name;
  const i = base.lastIndexOf(".");
  return i > 0 ? base.slice(0, i) : base;
}

const ALIASES: Record<string, string> = {
  jpeg: "jpg",
  jpe: "jpg",
  jfif: "jpg",
  tif: "tiff",
  heif: "heic",
  htm: "html",
  markdown: "md",
  aif: "aiff",
  aifc: "aiff",
  oga: "ogg",
  mpeg4: "mp4",
};

/** Canonical form of an extension (lower-case, no dot): jpeg -> jpg, tif -> tiff, aif -> aiff... */
export function normalizeExt(ext: string): string {
  const e = ext.replace(/^\./, "").toLowerCase();
  return ALIASES[e] ?? e;
}

/** Canonical extension of a file name. */
export function extensionOf(name: string): string {
  return normalizeExt(extOf(name));
}

/** Strips characters that are illegal in file names on Windows/macOS/Linux and control chars; never returns "". */
export function sanitizeFileName(name: string): string {
  let n = name.replace(/[\u0000-\u001f<>:"/\\|?*]/g, "_").replace(/[. ]+$/g, "");
  if (/^(con|prn|aux|nul|com\d|lpt\d)(\..*)?$/i.test(n)) n = `_${n}`;
  return n.length ? n.slice(0, 180) : "file";
}

export interface NameOptions {
  /** Lower-cased names already used by earlier results; the new name is made unique against them. */
  taken?: ReadonlySet<string>;
}

/**
 * `stem (suffix).ext`; when the job made several files: `stem (suffix 01).ext`, `stem (suffix 02).ext`...
 * `index` is 0-based; `count` is the number of files the job produced.
 */
export function outputName(inputName: string, suffix: string, ext: string, index = 0, count = 1, opts: NameOptions = {}): string {
  const stem = sanitizeFileName(stemOf(inputName));
  const label = count > 1 ? `${suffix} ${String(index + 1).padStart(String(count).length, "0")}` : suffix;
  return unique(`${stem} (${label})${ext ? "." + ext : ""}`, opts.taken);
}

/** For converters that keep their own names (archive extraction). */
export function keptName(name: string, opts: NameOptions = {}): string {
  return unique(name, opts.taken);
}

/** "a.png" -> "a (2).png" -> "a (3).png" until it is not in `taken` (a set of LOWER-CASED names). */
export function unique(name: string, taken?: ReadonlySet<string>): string {
  if (!taken) return name;
  if (!taken.has(name.toLowerCase())) return name;
  const ext = extOf(name);
  const stem = ext ? name.slice(0, name.length - ext.length - 1) : name;
  for (let n = 2; ; n++) {
    const candidate = `${stem} (${n})${ext ? "." + ext : ""}`;
    if (!taken.has(candidate.toLowerCase())) return candidate;
  }
}

export function formatBytes(n: number): string {
  if (!Number.isFinite(n) || n < 0) return "";
  if (n < 1024) return `${n} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let v = n / 1024;
  let u = 0;
  while (v >= 1024 && u < units.length - 1) {
    v /= 1024;
    u++;
  }
  return `${v >= 100 ? v.toFixed(0) : v >= 10 ? v.toFixed(1) : v.toFixed(2)} ${units[u]}`;
}
