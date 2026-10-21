// Subtitle conversion: srt / vtt / ass (+ssa) in, srt / vtt / txt out. Pure text work, runs in the worker.
// There is no ASS output (catalog lists it, but a lossless style-preserving ASS writer is out of scope).
// Light manifest: the parsers load lazily from ./subtitles.impl.

import type { Converter, Job, ProgressFn } from "../types";
import { normalizeExt } from "../names";

const TARGETS = ["srt", "vtt", "txt"] as const;
const INPUTS: readonly string[] = ["srt", "vtt", "ass"];
const MAX_BYTES = 50 * 1024 * 1024; // a subtitle file this large is not a subtitle file

function canonical(ext: string): string {
  const e = normalizeExt(ext);
  return e === "ssa" ? "ass" : e;
}

export const subtitlesConverter: Converter = {
  id: "subtitles",
  operations: TARGETS.map((t) => `convert:${t}`),
  supports(operation, ext) {
    if (!operation.startsWith("convert:")) return false;
    const target = operation.slice("convert:".length);
    const input = canonical(ext);
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
