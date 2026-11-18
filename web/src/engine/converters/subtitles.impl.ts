// Worker-side implementation of the subtitle conversions (see subtitles.ts).

import { extOf, stemOf } from "../names";
import { cuesToSrt, cuesToTxt, cuesToVtt } from "../lib/subtitles";
import { cuesToLrc, cuesToSbv, parseAnySubtitles, type AnySubtitleFormat } from "../lib/subtitle-formats";
import { decodeText } from "../lib/text-decode";
import { ConvertError, throwIfAborted, type Job, type ProgressFn } from "../types";
import { canonicalSubtitleExt } from "./subtitles";

const TYPES: Record<string, string> = {
  srt: "text/plain;charset=utf-8",
  vtt: "text/vtt;charset=utf-8",
  txt: "text/plain;charset=utf-8",
  lrc: "text/plain;charset=utf-8",
  sbv: "text/plain;charset=utf-8",
};

const KNOWN: readonly AnySubtitleFormat[] = ["srt", "vtt", "ass", "smi", "lrc", "ttml", "sub", "sbv"];

function hintFor(fileName: string): AnySubtitleFormat {
  const ext = canonicalSubtitleExt(extOf(fileName)) as AnySubtitleFormat;
  return KNOWN.includes(ext) ? ext : "srt";
}

/** Converts the bytes of one subtitle file. Exposed for tests; run() wraps it. The output is always UTF-8. */
export function convertSubtitleBytes(bytes: Uint8Array, fileName: string, target: string): { text: string; cues: number; skipped: number; encoding: string } {
  const { text, encoding } = decodeText(bytes);
  const { cues, skipped } = parseAnySubtitles(text, hintFor(fileName));
  if (cues.length === 0) throw new ConvertError("noCues");
  const out =
    target === "vtt" ? cuesToVtt(cues)
    : target === "txt" ? cuesToTxt(cues)
    : target === "lrc" ? cuesToLrc(cues)
    : target === "sbv" ? cuesToSbv(cues)
    : cuesToSrt(cues);
  return { text: out, cues: cues.length, skipped, encoding };
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
