// Worker-side font conversion (see fonts.ts).

import { writeEot } from "../lib/eot";
import { containerOf, decodeWoff, encodeWoff, hasCffOutlines, readSfnt, writeSfnt, type Sfnt } from "../lib/sfnt";
import { stemOf } from "../names";
import { ConvertError, throwIfAborted, type Job, type ProgressFn } from "../types";

const TYPES: Record<string, string> = { woff: "font/woff", ttf: "font/ttf", otf: "font/otf", eot: "application/vnd.ms-fontobject" };

/** A font read from bytes, with the original file when it was a plain TTF/OTF (an EOT stores it unchanged). */
interface LoadedFont {
  font: Sfnt;
  original?: Uint8Array;
}

/** Reads TTF, OTF or WOFF bytes whatever the file is called (a ".ttf" is sometimes a WOFF, and many ".ttf" files are CFF fonts). */
export function loadFont(bytes: Uint8Array): Sfnt {
  return load(bytes).font;
}

function load(bytes: Uint8Array): LoadedFont {
  const kind = containerOf(bytes);
  if (kind === "woff") return { font: decodeWoff(bytes) };
  if (kind === "woff2") throw new ConvertError("woff2"); // Brotli is not available to pages: the Windows app does WOFF2
  return { font: readSfnt(bytes), original: bytes };
}

/** Converts the bytes of one font. Exposed for tests; runFonts wraps it. */
export function convertFontBytes(bytes: Uint8Array, target: string): Uint8Array {
  const { font, original } = load(bytes);
  if (target === "woff") return encodeWoff(font);
  if (target === "eot") {
    if (original) return writeEot(font, original);
    // A web font: its tables written as an sfnt; the header then repeats that file's head.checkSumAdjustment.
    const copy: Sfnt = { flavor: font.flavor, tables: new Map(font.tables) };
    const data = writeSfnt(copy);
    return writeEot(copy, data);
  }
  // A web font back to the desktop font it wraps. Changing TrueType outlines into CFF ones (or back) is the Windows app's job.
  const cff = hasCffOutlines(font);
  if ((target === "ttf" && cff) || (target === "otf" && !cff)) throw new ConvertError("fontOutlines");
  if (target === "ttf" || target === "otf") return writeSfnt(font);
  throw new ConvertError("unsupported");
}

export async function runFonts(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
  const target = job.operation.slice("convert:".length);
  const file = job.files[0];
  if (!file || !(target in TYPES)) throw new ConvertError("unsupported");
  throwIfAborted(signal);
  onProgress(0.1);
  const bytes = new Uint8Array(await file.arrayBuffer());
  throwIfAborted(signal);
  onProgress(0.4);
  const out = convertFontBytes(bytes, target);
  onProgress(1);
  return [new File([out as BlobPart], `${stemOf(file.name)}.${target}`, { type: TYPES[target] })];
}
