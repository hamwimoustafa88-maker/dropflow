// Audio + video converter (ffmpeg.wasm). LIGHT manifest: safe to import from the UI bundle, the worker and Node tests.
// The engine (@ffmpeg/ffmpeg, ~31 MB of wasm) is only fetched when an audio/video file is actually used.
import { getMediaCaps, setMediaCaps, type MediaCaps } from "../caps";
import {
  TOOL_IDS,
  audioTargetsFor,
  supportsOp,
  toolOptions,
  videoTargetsFor,
} from "../lib/ffmpeg-args";
import { CORE_VERSION, isValidMediaCaps } from "../lib/ffmpeg-probe";
import type { Converter, Job, ProgressFn, ToolOption } from "../types";

const MAX_BYTES = 700 * 1024 * 1024;
const CACHE_KEY = `dropflow.media.caps.v1.${CORE_VERSION}`;

/** Every operation this converter could ever offer; `supports()` narrows it to what the probed core really does. */
const ALL_CONVERT_TARGETS = ["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma", "aac", "mp4", "mkv", "mov", "webm", "avi", "wmv", "gif"];

const SUFFIX: Record<string, string> = {
  "aud.normalize": "normalized",
  "aud.trim": "trimmed",
  "vid.trim": "trimmed",
  "vid.mute": "muted",
  "vid.snapshot": "frame",
  "vid.speed": "speed",
  "vid.compress": "compressed",
};

export const mediaConverter: Converter = {
  id: "media",
  operations: [...ALL_CONVERT_TARGETS.map((t) => `convert:${t}`), ...TOOL_IDS],

  supports(operation: string, ext: string): boolean {
    const caps = getMediaCaps();
    return caps ? supportsOp(operation, ext, caps) : false;
  },

  options(operation: string): readonly ToolOption[] {
    return toolOptions(operation);
  },

  // ffmpeg runs in its own worker; the main thread only orchestrates.
  runtime: () => "main",

  suffix(operation: string): string {
    return operation.startsWith("convert:") ? "converted" : (SUFFIX[operation] ?? "converted");
  },

  maxBytes: () => MAX_BYTES,

  async run(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
    engineTouched = true;
    const impl = await import("./media.impl");
    return impl.runMedia(job, onProgress, signal);
  },
};

// ------------------------------------------------------------------ capability probe

let engineTouched = false;
let inflight: Promise<MediaCaps> | null = null;

function readCache(): MediaCaps | null {
  try {
    if (typeof localStorage === "undefined") return null;
    const raw = localStorage.getItem(CACHE_KEY);
    if (!raw) return null;
    const parsed: unknown = JSON.parse(raw);
    return isValidMediaCaps(parsed) && parsed.coreVersion === CORE_VERSION ? parsed : null;
  } catch {
    return null;
  }
}

function writeCache(caps: MediaCaps): void {
  try {
    if (typeof localStorage !== "undefined") localStorage.setItem(CACHE_KEY, JSON.stringify(caps));
  } catch {
    /* private mode / quota: the probe is just redone next session */
  }
}

/**
 * Makes `supports()` truthful for audio/video. A probe cached for this core version (localStorage) is used at once
 * without loading the engine; otherwise the engine is loaded and probed (`-encoders`, `-muxers`...). Concurrent calls
 * share one probe. `force` ignores both the in-memory and the cached result.
 */
export function loadMediaCaps(opts: { force?: boolean } = {}): Promise<MediaCaps> {
  const current = getMediaCaps();
  if (!opts.force && current) return Promise.resolve(current);
  if (inflight) return inflight;
  inflight = (async () => {
    if (!opts.force) {
      const cached = readCache();
      if (cached) {
        setMediaCaps(cached);
        return cached;
      }
    }
    engineTouched = true;
    const { probeMedia } = await import("./media-engine");
    const caps = await probeMedia();
    writeCache(caps);
    setMediaCaps(caps);
    return caps;
  })().finally(() => {
    inflight = null;
  });
  return inflight;
}

/** Terminates the ffmpeg worker and frees its memory. A no-op when the engine was never used. */
export function releaseMedia(): void {
  if (!engineTouched) return;
  void import("./media-engine").then((m) => m.terminateEngine());
}

/** Convenience for the UI: which targets the probed core offers (empty until probed). */
export function mediaTargets(): { audio: string[]; video: string[] } {
  const caps = getMediaCaps();
  return caps ? { audio: audioTargetsFor(caps), video: videoTargetsFor(caps) } : { audio: [], video: [] };
}
