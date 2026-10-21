// Singleton wrapper around the ffmpeg.wasm worker (single-thread @ffmpeg/core, so no SharedArrayBuffer / COOP / COEP).
// The worker is the package's own worker.js (vendored to /vendor/ffmpeg); ./ffmpeg-client is our small client for it
// (the package's FFmpeg class cannot be bundled by Turbopack, see ffmpeg-client.ts). It is imported dynamically the
// first time an audio/video file is used, so none of it is in the landing page or the initial /app bundle.
//
// The FFmpeg client spawns the Web Worker; this module (main thread) only orchestrates: write input, exec, read
// output. Everything is loaded from the site's own origin (/vendor/ffmpeg/*, copied by scripts/vendor.mjs): no CDN.
// Nothing here sends data anywhere: the only network traffic is the same-origin GET of those static files.
//
// Verified against the real core: every exec() leaks a little inside the wasm instance and after ~60-85 commands the
// instance dies with "RuntimeError: memory access out of bounds". So the instance is recycled (terminated and
// re-created from the cached wasm) every RECYCLE_AFTER commands, and a command that crashes a *reused* instance is
// retried once on a fresh one.
import type { FFmpeg } from "./ffmpeg-client";
import type { MediaCaps } from "../caps";
import { buildMediaCaps, classifyFfmpegLog, CORE_VERSION, type ProbeTexts } from "../lib/ffmpeg-probe";
import { ConvertError } from "../types";

const VENDOR = "/vendor/ffmpeg";
/** The wasm core is ~31 MB; give slow connections plenty of time before giving up. */
const LOAD_TIMEOUT_MS = 5 * 60_000;
const MAX_LOG_LINES = 400;
/** Real core dies after ~60 commands (even `-version`); recycle well before that. */
export const RECYCLE_AFTER = 20;

export interface ExecInput {
  /** Name inside the wasm file system, e.g. "in0.mp4". Never the user's file name. */
  name: string;
  data: Blob | Uint8Array;
}

export interface ExecOptions {
  inputs: ExecInput[];
  /** Names to read back after the run (missing/empty ones come back as null). */
  outputs: string[];
  signal?: AbortSignal;
  /** 0..1, only values ffmpeg really reports (garbage is dropped). */
  onProgress?: (fraction: number) => void;
  /** Keep this many log lines (the probe needs more than the default). */
  maxLogLines?: number;
}

export interface ExecResult {
  code: number;
  outputs: (Uint8Array | null)[];
  log: string[];
}

function abortError(): DOMException {
  return new DOMException("Cancelled", "AbortError");
}

function origin(): string {
  return typeof location !== "undefined" ? location.origin : "";
}

/** Absolute URL: the FFmpeg class does `new Worker(new URL(classWorkerURL, import.meta.url))`, which needs one. */
function abs(path: string): string {
  return new URL(path, origin() || "http://localhost").href;
}

const OOM_RE = /cannot enlarge memory|out of memory|\bOOM\b|allocation failed/i;
/** What a dead/corrupted wasm instance throws. */
const CRASH_RE = /memory access out of bounds|RuntimeError|unreachable|table index is out of bounds|null function/i;

function mapEngineError(e: unknown, log: string[]): ConvertError {
  if (e instanceof ConvertError) return e;
  const text = e instanceof Error ? e.message : String(e);
  if (OOM_RE.test(text) || /memory access out of bounds/i.test(text)) return new ConvertError("outOfMemory", {}, text.slice(0, 200));
  return new ConvertError(classifyFfmpegLog([...log, text]), {}, text.slice(0, 200));
}

/** Internal: the command crashed an instance that had already run other commands; worth one retry on a fresh one. */
class ReusedInstanceCrash extends Error {
  constructor(readonly inner: ConvertError) {
    super("reused instance crashed");
  }
}

class MediaEngine {
  private ff: FFmpeg | null = null;
  private loading: Promise<FFmpeg> | null = null;
  /** Commands run on the current instance. */
  private execCount = 0;
  /** Serialises jobs: one ffmpeg instance, one command at a time. */
  private tail: Promise<void> = Promise.resolve();

  get loaded(): boolean {
    return !!this.ff?.loaded;
  }

  private async acquire(signal?: AbortSignal): Promise<() => void> {
    const prev = this.tail;
    let release!: () => void;
    const mine = new Promise<void>((r) => (release = r));
    this.tail = prev.then(() => mine);
    try {
      await new Promise<void>((resolve, reject) => {
        if (signal?.aborted) return reject(abortError());
        const onAbort = () => reject(abortError());
        signal?.addEventListener("abort", onAbort, { once: true });
        prev.then(() => {
          signal?.removeEventListener("abort", onAbort);
          resolve();
        });
      });
    } catch (e) {
      release();
      throw e;
    }
    return release;
  }

  private async load(signal?: AbortSignal): Promise<FFmpeg> {
    if (this.ff?.loaded) return this.ff;
    if (!this.loading) {
      this.loading = (async () => {
        let timer: ReturnType<typeof setTimeout> | undefined;
        try {
          const { FFmpeg: FFmpegClass } = await import("./ffmpeg-client");
          const ff = new FFmpegClass();
          this.ff = ff;
          this.execCount = 0;
          const timeout = new Promise<never>((_, reject) => {
            timer = setTimeout(() => reject(new Error("load timeout")), LOAD_TIMEOUT_MS);
          });
          await Promise.race([
            ff.load({
              classWorkerURL: abs(`${VENDOR}/worker.js`),
              coreURL: abs(`${VENDOR}/ffmpeg-core.js`),
              wasmURL: abs(`${VENDOR}/ffmpeg-core.wasm`),
            }),
            timeout,
          ]);
          return ff;
        } catch (e) {
          this.kill();
          if (signal?.aborted) throw abortError();
          throw new ConvertError("engineLoadFailed", {}, e instanceof Error ? e.message : String(e));
        } finally {
          if (timer) clearTimeout(timer);
          this.loading = null;
        }
      })();
    }
    return this.loading;
  }

  /** Hard stop: terminates the ffmpeg worker (the only way to cancel a running command). Next job re-creates it. */
  kill(): void {
    const ff = this.ff;
    this.ff = null;
    this.loading = null;
    this.execCount = 0;
    try {
      ff?.terminate();
    } catch {
      /* already gone */
    }
  }

  async exec(args: string[], o: ExecOptions): Promise<ExecResult> {
    const release = await this.acquire(o.signal);
    try {
      try {
        return await this.execOnce(args, o);
      } catch (e) {
        // A reused instance that crashed is retried once on a fresh one (inputs are re-read from their Blobs).
        if (e instanceof ReusedInstanceCrash && o.inputs.every((i) => i.data instanceof Blob)) {
          try {
            return await this.execOnce(args, o);
          } catch (e2) {
            throw e2 instanceof ReusedInstanceCrash ? e2.inner : e2;
          }
        }
        throw e instanceof ReusedInstanceCrash ? e.inner : e;
      }
    } finally {
      release();
    }
  }

  private async execOnce(args: string[], o: ExecOptions): Promise<ExecResult> {
    const log: string[] = [];
    const max = o.maxLogLines ?? MAX_LOG_LINES;
    let ff: FFmpeg | null = null;
    const touched: string[] = [];
    const onLog = ({ message }: { message: string }) => {
      log.push(message);
      if (log.length > max) log.shift();
    };
    const onProgress = ({ progress }: { progress: number }) => {
      if (progress >= 0 && progress <= 1) o.onProgress?.(progress);
    };
    const onAbort = () => this.kill();
    try {
      if (o.signal?.aborted) throw abortError();
      o.signal?.addEventListener("abort", onAbort, { once: true });
      if (this.execCount >= RECYCLE_AFTER) this.kill(); // fresh instance before the core's leak catches up
      ff = await this.load(o.signal);
      const reused = this.execCount > 0;
      this.execCount++;
      ff.on("log", onLog);
      ff.on("progress", onProgress);
      try {
        for (const f of o.inputs) {
          const data = f.data instanceof Uint8Array ? f.data : new Uint8Array(await f.data.arrayBuffer());
          if (o.signal?.aborted) throw abortError();
          await ff.writeFile(f.name, data);
          touched.push(f.name);
        }
        const code = await ff.exec(args);
        const outputs: (Uint8Array | null)[] = [];
        for (const name of o.outputs) {
          touched.push(name);
          if (code !== 0) {
            outputs.push(null);
            continue;
          }
          try {
            const d = await ff.readFile(name);
            outputs.push(d instanceof Uint8Array && d.length > 0 ? d : null);
          } catch {
            outputs.push(null); // ffmpeg produced no such file (e.g. a snapshot beyond the end)
          }
        }
        return { code, outputs, log };
      } catch (e) {
        if (o.signal?.aborted) throw abortError();
        if (e instanceof ConvertError) throw e;
        this.kill(); // unknown state: start clean next time
        const mapped = mapEngineError(e, log);
        const text = e instanceof Error ? e.message : String(e);
        if (reused && CRASH_RE.test(text)) throw new ReusedInstanceCrash(mapped);
        throw mapped;
      }
    } finally {
      o.signal?.removeEventListener("abort", onAbort);
      if (ff && this.ff === ff) {
        ff.off("log", onLog);
        ff.off("progress", onProgress);
        for (const n of touched) {
          try {
            await ff.deleteFile(n);
          } catch {
            /* never created, or the worker was terminated */
          }
        }
      }
    }
  }

  /** Lists what this exact core can do (encoders, decoders, muxers, demuxers, filters). */
  async probe(): Promise<MediaCaps> {
    const texts: Record<string, string> = {};
    for (const kind of ["encoders", "decoders", "muxers", "demuxers", "filters"] as const) {
      const r = await this.exec(["-hide_banner", `-${kind}`], { inputs: [], outputs: [], maxLogLines: 20_000 });
      texts[kind] = r.log.join("\n");
    }
    return buildMediaCaps(CORE_VERSION, texts as unknown as ProbeTexts);
  }
}

let engine: MediaEngine | null = null;

export function getEngine(): MediaEngine {
  engine ??= new MediaEngine();
  return engine;
}

export function probeMedia(): Promise<MediaCaps> {
  return getEngine().probe();
}

/** Terminates the ffmpeg worker and frees its wasm heap (it never shrinks). */
export function terminateEngine(): void {
  engine?.kill();
}
