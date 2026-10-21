// Worker-side implementation of the subtitle conversions (see subtitles.ts).

import { extOf, normalizeExt, stemOf } from "../names";
import { cuesToSrt, cuesToTxt, cuesToVtt, parseSubtitles, type SubtitleFormat } from "../lib/subtitles";
import { decodeText } from "../lib/text-decode";
import { ConvertError, throwIfAborted, type Job, type ProgressFn } from "../types";

const TYPES: Record<string, string> = {
  srt: "text/plain;charset=utf-8",
  vtt: "text/vtt;charset=utf-8",
  txt: "text/plain;charset=utf-8",
};

function hintFor(fileName: string): SubtitleFormat {
  const ext = normalizeExt(extOf(fileName));
  return ext === "vtt" ? "vtt" : ext === "ass" || ext === "ssa" ? "ass" : "srt";
}

/** Converts the bytes of one subtitle file. Exposed for tests; run() wraps it. */
export function convertSubtitleBytes(bytes: Uint8Array, fileName: string, target: string): { text: string; cues: number; skipped: number } {
  const { text } = decodeText(bytes);
  const { cues, skipped } = parseSubtitles(text, hintFor(fileName));
  if (cues.length === 0) throw new ConvertError("noCues");
  const out = target === "vtt" ? cuesToVtt(cues) : target === "txt" ? cuesToTxt(cues) : cuesToSrt(cues);
  return { text: out, cues: cues.length, skipped };
}

export async function runSubtitles(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
  const target = job.operation.slice("convert:".length);
  const file = job.files[0];
  if (!file || !(target in TYPES)) throw new ConvertError("unsupported");
  throwIfAborted(signal);
  onProgress(0.1);
  const bytes = new Uint8Array(await file.arrayBuffer());
  throwIfAborted(signal);
  onProgress(0.5);
  const { text } = convertSubtitleBytes(bytes, file.name, target);
  onProgress(1);
  return [new File([text], `${stemOf(file.name)}.${target}`, { type: TYPES[target] })];
}
