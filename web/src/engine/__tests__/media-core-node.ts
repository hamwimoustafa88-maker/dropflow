// Test helper (not a test): runs the REAL @ffmpeg/core 0.12.10 (the same bytes DropFlow serves from /vendor/ffmpeg)
// inside Node, so the pure argument builders can be verified against actual ffmpeg + ffprobe, with no mocks.
//
// The core is built for web workers only: its glue hard-codes ENVIRONMENT_IS_WORKER and reads `self.location`, so
// `self`/`location` are shimmed and the wasm bytes are passed in via `wasmBinary` (no fetch involved).
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";

export interface StreamInfo {
  type: string;
  codec: string;
  width?: number;
  height?: number;
  sampleRate?: number;
}

export interface MediaInfo {
  formatName: string;
  duration: number;
  streams: StreamInfo[];
  hasAudio: boolean;
  hasVideo: boolean;
}

export interface RunResult {
  code: number;
  log: string[];
  progress: { progress: number; time: number }[];
}

interface CoreModule {
  exec(...args: string[]): number;
  ffprobe(...args: string[]): number;
  setLogger(fn: (l: { type: string; message: string }) => void): void;
  setProgress(fn: (p: { progress: number; time: number }) => void): void;
  setTimeout(ms: number): void;
  reset(): void;
  ret: number;
  FS: {
    writeFile(path: string, data: Uint8Array): void;
    readFile(path: string): Uint8Array;
    stat(path: string): { size: number };
    unlink(path: string): void;
  };
}

/**
 * The real core leaks inside the wasm instance on every command (found while writing this test: after ~60-85
 * commands, even `-version`, it dies with "memory access out of bounds"). The production engine therefore recycles
 * its instance (see RECYCLE_AFTER in media-engine.ts); this helper does the same, at the start of write().
 */
const RECYCLE_AFTER = 18;

async function createCore(): Promise<CoreModule> {
  const g = globalThis as unknown as { self?: unknown; location?: unknown };
  g.self ??= globalThis;
  g.location ??= { href: "file:///ffmpeg/" };
  const require = createRequire(import.meta.url);
  const create = require("@ffmpeg/core") as (opts: object) => Promise<CoreModule>;
  return create({ wasmBinary: wasm ??= readFileSync(require.resolve("@ffmpeg/core/wasm")) });
}
let wasm: Buffer | undefined;

export class RealCore {
  private log: string[] = [];
  private progress: { progress: number; time: number }[] = [];
  private commands = 0;
  recycles = 0;

  private constructor(private core: CoreModule) {
    this.attach();
  }

  private attach(): void {
    this.core.setLogger((l) => this.log.push(l.message));
    this.core.setProgress((p) => this.progress.push(p));
  }

  static async load(): Promise<RealCore> {
    return new RealCore(await createCore());
  }

  /** Starts a fresh wasm instance (empty file system) once the current one has run enough commands. */
  private async freshIfNeeded(): Promise<void> {
    if (this.commands < RECYCLE_AFTER) return;
    this.core = await createCore();
    this.attach();
    this.commands = 0;
    this.recycles++;
  }

  /** Call before staging inputs for a new operation; recycles the wasm instance when it is due. */
  async begin(): Promise<void> {
    await this.freshIfNeeded();
  }

  /** Runs `ffmpeg <args>` (the core prepends -nostdin -y itself). */
  run(args: string[]): RunResult {
    this.log = [];
    this.progress = [];
    this.commands++;
    this.core.setTimeout(-1);
    this.core.exec(...args);
    const code = this.core.ret;
    this.core.reset();
    return { code, log: this.log, progress: this.progress };
  }

  write(name: string, data: Uint8Array): void {
    this.core.FS.writeFile(name, data);
  }

  exists(name: string): boolean {
    try {
      return this.core.FS.stat(name).size >= 0;
    } catch {
      return false;
    }
  }

  size(name: string): number {
    return this.exists(name) ? this.core.FS.stat(name).size : -1;
  }

  read(name: string): Uint8Array | null {
    return this.exists(name) ? this.core.FS.readFile(name) : null;
  }

  remove(name: string): void {
    try {
      this.core.FS.unlink(name);
    } catch {
      /* not there */
    }
  }

  /** ffprobe summary of a file already in the wasm FS. */
  info(name: string): MediaInfo {
    this.remove("probe.json");
    this.log = [];
    this.commands++;
    this.core.ffprobe(
      "-v",
      "error",
      "-show_entries",
      "format=duration,format_name:stream=codec_type,codec_name,width,height,sample_rate",
      "-of",
      "json",
      name,
      "-o",
      "probe.json",
    );
    const raw = this.read("probe.json");
    if (!raw) throw new Error(`ffprobe produced nothing for ${name}: ${this.log.join(" | ")}`);
    const j = JSON.parse(new TextDecoder().decode(raw)) as {
      format?: { duration?: string; format_name?: string };
      streams?: { codec_type: string; codec_name: string; width?: number; height?: number; sample_rate?: string }[];
    };
    const streams: StreamInfo[] = (j.streams ?? []).map((s) => ({
      type: s.codec_type,
      codec: s.codec_name,
      width: s.width,
      height: s.height,
      sampleRate: s.sample_rate ? Number(s.sample_rate) : undefined,
    }));
    return {
      formatName: j.format?.format_name ?? "",
      duration: Number(j.format?.duration ?? NaN),
      streams,
      hasAudio: streams.some((s) => s.type === "audio"),
      hasVideo: streams.some((s) => s.type === "video"),
    };
  }
}

/** Container magic checks used by the integration test. */
export function startsWith(bytes: Uint8Array, ...sig: number[]): boolean {
  return sig.every((b, i) => bytes[i] === b);
}
export function ascii(bytes: Uint8Array, from: number, to: number): string {
  return String.fromCharCode(...bytes.slice(from, to));
}

export function magicOf(ext: string, b: Uint8Array): boolean {
  switch (ext) {
    case "wav":
      return ascii(b, 0, 4) === "RIFF" && ascii(b, 8, 12) === "WAVE";
    case "mp3":
      return ascii(b, 0, 3) === "ID3" || (b[0] === 0xff && (b[1] & 0xe0) === 0xe0);
    case "flac":
      return ascii(b, 0, 4) === "fLaC";
    case "ogg":
    case "opus":
      return ascii(b, 0, 4) === "OggS";
    case "m4a":
    case "mp4":
    case "mov":
    case "m4v":
      return ascii(b, 4, 8) === "ftyp" || ascii(b, 4, 8) === "moov" || ascii(b, 4, 8) === "wide";
    case "aiff":
      return ascii(b, 0, 4) === "FORM" && ascii(b, 8, 12).startsWith("AIF");
    case "aac":
      return b[0] === 0xff && (b[1] & 0xf0) === 0xf0; // ADTS sync word
    case "wma":
    case "wmv":
      return startsWith(b, 0x30, 0x26, 0xb2, 0x75); // ASF header GUID
    case "mkv":
    case "webm":
      return startsWith(b, 0x1a, 0x45, 0xdf, 0xa3); // EBML
    case "avi":
      return ascii(b, 0, 4) === "RIFF" && ascii(b, 8, 12) === "AVI ";
    case "gif":
      return ascii(b, 0, 6) === "GIF89a" || ascii(b, 0, 6) === "GIF87a";
    case "flv":
      return ascii(b, 0, 3) === "FLV";
    case "png":
      return startsWith(b, 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a);
    default:
      return false;
  }
}
