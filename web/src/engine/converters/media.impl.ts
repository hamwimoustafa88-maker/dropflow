// run() for the audio/video operations. Runs on the MAIN thread: ffmpeg.wasm lives in its own worker (created by the
// FFmpeg class in media-engine.ts), this function only builds the command, hands the input over and collects the output.
import { getMediaCaps } from "../caps";
import { buildArgs } from "../lib/ffmpeg-args";
import { classifyFfmpegLog } from "../lib/ffmpeg-probe";
import { extensionOf, stemOf } from "../names";
import { ConvertError, throwIfAborted, type Job, type ProgressFn } from "../types";
import { getEngine, terminateEngine } from "./media-engine";

/** The wasm heap is 32-bit (max 2 GB) and holds input + output; 700 MB inputs are the realistic ceiling. */
export const MAX_MEDIA_BYTES = 700 * 1024 * 1024;
/** Inputs bigger than this make the engine restart afterwards, because a grown wasm heap is never given back. */
const RECYCLE_BYTES = 64 * 1024 * 1024;

export async function runMedia(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
  const file = job.files[0];
  if (!file) throw new ConvertError("noFiles");
  throwIfAborted(signal);
  if (file.size > MAX_MEDIA_BYTES) throw new ConvertError("tooLarge", { max: "700 MB" });

  let caps = getMediaCaps();
  if (!caps) {
    const { loadMediaCaps } = await import("./media");
    caps = await loadMediaCaps();
  }

  const ext = extensionOf(file.name);
  const built = buildArgs(job.operation, ext, job.options, caps);
  const engine = getEngine();

  onProgress(0, "loading");
  const res = await engine.exec(built.args, {
    inputs: [{ name: built.inputName, data: file }],
    outputs: [built.outputName],
    signal,
    onProgress: (f) => onProgress(Math.min(0.99, f)),
  });
  throwIfAborted(signal);

  const data = res.outputs[0];
  const recycle = file.size > RECYCLE_BYTES;
  try {
    if (res.code !== 0) throw new ConvertError(classifyFfmpegLog(res.log), {}, res.log.slice(-3).join(" | "));
    if (!data) {
      // ffmpeg exited cleanly but wrote nothing: a snapshot past the end, or nothing to encode.
      if (job.operation === "vid.snapshot") throw new ConvertError("noFrame");
      throw new ConvertError(classifyFfmpegLog(res.log), {}, res.log.slice(-3).join(" | "));
    }
    if (job.operation === "vid.compress" && data.length >= file.size) throw new ConvertError("notSmaller");

    onProgress(1);
    const name = `${stemOf(file.name)}.${built.outputExt}`;
    return [new File([data as BlobPart], name, { type: built.mime })];
  } finally {
    if (recycle) terminateEngine();
  }
}
