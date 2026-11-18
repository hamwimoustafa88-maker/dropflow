// Fonts in the browser: TTF/OTF to WOFF and WOFF back to the desktop font it wraps (zlib per table, through fflate), and TTF/OTF/WOFF to
// EOT (uncompressed Embedded OpenType, for old Internet Explorer; output only). WOFF2 needs Brotli, which no browser exposes to pages
// (`CompressionStream("brotli")` is missing in Edge/Chrome 157), and outline conversion between TTF and OTF are done by the Windows app.
// Light manifest: the work loads from ./fonts.impl.

import type { Converter, Job, ProgressFn } from "../types";
import { normalizeExt } from "../names";

const PAIRS: Readonly<Record<string, readonly string[]>> = {
  woff: ["ttf", "otf"],
  ttf: ["woff"],
  otf: ["woff"],
  eot: ["ttf", "otf", "woff"],
};
const MAX_BYTES = 64 * 1024 * 1024; // larger than any real font

export const fontsConverter: Converter = {
  id: "fonts",
  operations: Object.keys(PAIRS).map((t) => `convert:${t}`),
  supports(operation, ext) {
    if (!operation.startsWith("convert:")) return false;
    const target = operation.slice("convert:".length);
    return PAIRS[target]?.includes(normalizeExt(ext)) ?? false;
  },
  options: () => [],
  runtime: () => "worker",
  suffix: () => "converted",
  maxBytes: () => MAX_BYTES,
  async run(job: Job, onProgress: ProgressFn, signal: AbortSignal) {
    const impl = await import("./fonts.impl");
    return impl.runFonts(job, onProgress, signal);
  },
};
