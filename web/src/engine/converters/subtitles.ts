// Subtitle conversion: srt / vtt / ass (+ssa) / smi (+sami) / lrc / ttml (+dfxp) / sub (MicroDVD) / sbv in, srt / vtt / txt / lrc / sbv
// out. Pure text work, runs in the worker. There is no ASS output (catalog lists it, but a lossless style-preserving ASS writer is out
// of scope here; the desktop app writes it). Light manifest: the parsers load lazily from ./subtitles.impl.

import type { Converter, Job, ProgressFn } from "../types";
import { normalizeExt } from "../names";

const TARGETS = ["srt", "vtt", "txt", "lrc", "sbv"] as const;
const INPUTS: readonly string[] = ["srt", "vtt", "ass", "smi", "lrc", "ttml", "sub", "sbv"];
const MAX_BYTES = 50 * 1024 * 1024; // a subtitle file this large is not a subtitle file

/** One spelling per format: ssa is ASS, sami is SAMI, dfxp is TTML. */
export function canonicalSubtitleExt(ext: string): string {
  const e = normalizeExt(ext);
  return e === "ssa" ? "ass" : e === "sami" ? "smi" : e === "dfxp" ? "ttml" : e;
}

export const subtitlesConverter: Converter = {
  id: "subtitles",
  operations: TARGETS.map((t) => `convert:${t}`),
  supports(operation, ext) {
    if (!operation.startsWith("convert:")) return false;
    const target = operation.slice("convert:".length);
    const input = canonicalSubtitleExt(ext);
    return (TARGETS as readonly string[]).includes(target) && INPUTS.includes(input) && target !== input;
  },
  options: () => [],
  runtime: () => "worker",
  suffix: () => "converted",
  maxBytes: () => MAX_BYTES,
  async run(job: Job, onProgress: ProgressFn, signal: AbortSignal) {
    const impl = await import("./subtitles.impl");
    return impl.runSubtitles(job, onProgress, signal);
  },
};
