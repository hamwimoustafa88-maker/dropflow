// Pure ffmpeg command construction + capability gating for the audio/video converter. Node-compatible.
// Every argument list here was run against the real @ffmpeg/core 0.12.10 (see __tests__/media-ffmpeg-real.test.ts).
// `-y -nostdin` are prepended by the core itself; input files are always called in0.<ext> and the output out.<ext>.
import type { MediaCaps } from "../caps";
import { normalizeExt } from "../names";
import { ConvertError, type OptionValues, type ToolOption } from "../types";
import { formatFfmpegTime, parseTime, parseTimeRange } from "./media-time";

export interface BuiltArgs {
  args: string[];
  inputName: string;
  outputName: string;
  outputExt: string;
  mime: string;
  /** "image" for snapshots, otherwise the kind of the produced stream(s). */
  kind: "audio" | "video" | "image";
}

// ---------------------------------------------------------------- tables

interface AudioSpec {
  ext: string;
  mime: string;
  encoder: string;
  /** Muxer names in order of preference; the first one present in caps is used with `-f`. */
  muxers: readonly string[];
  args: readonly string[];
}

const AUDIO_SPECS: Record<string, AudioSpec> = {
  mp3: { ext: "mp3", mime: "audio/mpeg", encoder: "libmp3lame", muxers: ["mp3"], args: ["-c:a", "libmp3lame", "-q:a", "2"] },
  m4a: { ext: "m4a", mime: "audio/mp4", encoder: "aac", muxers: ["ipod", "mp4"], args: ["-c:a", "aac", "-b:a", "192k"] },
  wav: { ext: "wav", mime: "audio/wav", encoder: "pcm_s16le", muxers: ["wav"], args: ["-c:a", "pcm_s16le"] },
  flac: { ext: "flac", mime: "audio/flac", encoder: "flac", muxers: ["flac"], args: ["-c:a", "flac"] },
  ogg: { ext: "ogg", mime: "audio/ogg", encoder: "libvorbis", muxers: ["ogg"], args: ["-c:a", "libvorbis", "-q:a", "5"] },
  opus: { ext: "opus", mime: "audio/ogg;codecs=opus", encoder: "libopus", muxers: ["opus", "ogg"], args: ["-c:a", "libopus", "-b:a", "128k"] },
  aiff: { ext: "aiff", mime: "audio/aiff", encoder: "pcm_s16be", muxers: ["aiff"], args: ["-c:a", "pcm_s16be"] },
  wma: { ext: "wma", mime: "audio/x-ms-wma", encoder: "wmav2", muxers: ["asf"], args: ["-c:a", "wmav2", "-b:a", "192k"] },
  aac: { ext: "aac", mime: "audio/aac", encoder: "aac", muxers: ["adts"], args: ["-c:a", "aac", "-b:a", "192k"] },
};

const AUDIO_ORDER = ["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma", "aac"] as const;

type Family = "x264" | "vp8" | "mpeg4" | "wmv2";

interface ContainerSpec {
  ext: string;
  mime: string;
  muxers: readonly string[];
  family: Family;
}

const CONTAINERS: Record<string, ContainerSpec> = {
  mp4: { ext: "mp4", mime: "video/mp4", muxers: ["mp4"], family: "x264" },
  m4v: { ext: "m4v", mime: "video/x-m4v", muxers: ["mp4"], family: "x264" },
  mov: { ext: "mov", mime: "video/quicktime", muxers: ["mov"], family: "x264" },
  mkv: { ext: "mkv", mime: "video/x-matroska", muxers: ["matroska"], family: "x264" },
  webm: { ext: "webm", mime: "video/webm", muxers: ["webm"], family: "vp8" },
  avi: { ext: "avi", mime: "video/x-msvideo", muxers: ["avi"], family: "mpeg4" },
  wmv: { ext: "wmv", mime: "video/x-ms-wmv", muxers: ["asf"], family: "wmv2" },
};

/** Containers a video can be CONVERTED to (catalog video outputs minus the audio ones), plus gif. */
const VIDEO_TARGET_ORDER = ["mp4", "mkv", "mov", "webm", "avi", "wmv", "gif"] as const;
/** Containers whose streams we re-encode for trim / speed / compress (same container out as in). */
const EDIT_EXTS = ["mp4", "m4v", "mov", "mkv", "webm", "avi"] as const;
/** Containers where `-c:v copy -an` (mute) is offered. */
const COPY_CONTAINERS: Record<string, { mime: string; muxers: readonly string[] }> = {
  mp4: { mime: "video/mp4", muxers: ["mp4"] },
  m4v: { mime: "video/x-m4v", muxers: ["mp4"] },
  mov: { mime: "video/quicktime", muxers: ["mov"] },
  mkv: { mime: "video/x-matroska", muxers: ["matroska"] },
  webm: { mime: "video/webm", muxers: ["webm"] },
  avi: { mime: "video/x-msvideo", muxers: ["avi"] },
  wmv: { mime: "video/x-ms-wmv", muxers: ["asf"] },
  flv: { mime: "video/x-flv", muxers: ["flv"] },
};

interface InputSpec {
  demuxers: readonly string[];
  /** At least one of these decoders must exist. */
  decoders: readonly string[];
}

const AUDIO_INPUTS: Record<string, InputSpec> = {
  mp3: { demuxers: ["mp3"], decoders: ["mp3", "mp3float"] },
  m4a: { demuxers: ["mov"], decoders: ["aac", "aac_fixed", "alac"] },
  wav: { demuxers: ["wav"], decoders: ["pcm_s16le"] },
  flac: { demuxers: ["flac"], decoders: ["flac"] },
  ogg: { demuxers: ["ogg"], decoders: ["vorbis", "libvorbis", "opus", "libopus", "flac"] },
  opus: { demuxers: ["ogg"], decoders: ["opus", "libopus"] },
  aiff: { demuxers: ["aiff"], decoders: ["pcm_s16be"] },
  wma: { demuxers: ["asf"], decoders: ["wmav2", "wmav1", "wmapro"] },
  aac: { demuxers: ["aac"], decoders: ["aac", "aac_fixed"] },
};

const VIDEO_INPUTS: Record<string, InputSpec> = {
  mp4: { demuxers: ["mov"], decoders: ["h264", "mpeg4"] },
  m4v: { demuxers: ["mov"], decoders: ["h264", "mpeg4"] },
  mov: { demuxers: ["mov"], decoders: ["h264", "mpeg4"] },
  mkv: { demuxers: ["matroska"], decoders: ["h264", "vp8", "vp9", "mpeg4"] },
  webm: { demuxers: ["webm", "matroska"], decoders: ["vp8", "vp9", "libvpx", "libvpx-vp9"] },
  avi: { demuxers: ["avi"], decoders: ["mpeg4", "msmpeg4v2", "msmpeg4v3", "mjpeg", "h264"] },
  wmv: { demuxers: ["asf"], decoders: ["wmv1", "wmv2", "wmv3"] },
  gif: { demuxers: ["gif"], decoders: ["gif"] },
  flv: { demuxers: ["flv"], decoders: ["flv", "h264"] },
};

// ---------------------------------------------------------------- caps helpers

function has(list: readonly string[], name: string): boolean {
  return list.includes(name);
}
function hasAny(list: readonly string[], names: readonly string[]): boolean {
  return names.some((n) => list.includes(n));
}
function firstOf(list: readonly string[], names: readonly string[]): string | undefined {
  return names.find((n) => list.includes(n));
}
function hasFilters(caps: MediaCaps, names: readonly string[]): boolean {
  return names.every((n) => has(caps.filters, n));
}

function inputAvailable(spec: InputSpec | undefined, caps: MediaCaps): boolean {
  return !!spec && hasAny(caps.demuxers, spec.demuxers) && hasAny(caps.decoders, spec.decoders);
}

/** Audio file extensions this core can read (demuxer AND decoder present). */
export function audioInputsFor(caps: MediaCaps): string[] {
  return AUDIO_ORDER.filter((e) => inputAvailable(AUDIO_INPUTS[e], caps));
}

/** Video file extensions this core can read. */
export function videoInputsFor(caps: MediaCaps): string[] {
  return Object.keys(VIDEO_INPUTS).filter((e) => inputAvailable(VIDEO_INPUTS[e], caps));
}

function audioSpecFor(ext: string, caps: MediaCaps): (AudioSpec & { muxer: string }) | null {
  const s = AUDIO_SPECS[ext];
  if (!s || !has(caps.encoders, s.encoder)) return null;
  const muxer = firstOf(caps.muxers, s.muxers);
  return muxer ? { ...s, muxer } : null;
}

/** Audio formats a file can be converted to: encoder AND muxer exist. */
export function audioTargetsFor(caps: MediaCaps): string[] {
  return AUDIO_ORDER.filter((e) => audioSpecFor(e, caps) !== null);
}

function familyAvailable(family: Family, caps: MediaCaps): boolean {
  switch (family) {
    case "x264":
      return has(caps.encoders, "libx264") && has(caps.encoders, "aac");
    case "vp8":
      return has(caps.encoders, "libvpx") && hasAny(caps.encoders, ["libvorbis", "libopus"]);
    case "mpeg4":
      return has(caps.encoders, "mpeg4") && has(caps.encoders, "libmp3lame");
    case "wmv2":
      return has(caps.encoders, "wmv2") && has(caps.encoders, "wmav2");
  }
}

function containerFor(ext: string, caps: MediaCaps): (ContainerSpec & { muxer: string }) | null {
  const c = CONTAINERS[ext];
  if (!c || !familyAvailable(c.family, caps)) return null;
  const muxer = firstOf(caps.muxers, c.muxers);
  return muxer ? { ...c, muxer } : null;
}

function gifAvailable(caps: MediaCaps): boolean {
  return has(caps.encoders, "gif") && has(caps.muxers, "gif") && hasFilters(caps, ["fps", "scale", "split", "palettegen", "paletteuse"]);
}

/** Video formats a video can be converted to (gif included only when its encoder, muxer and filters exist). */
export function videoTargetsFor(caps: MediaCaps): string[] {
  return VIDEO_TARGET_ORDER.filter((e) => (e === "gif" ? gifAvailable(caps) : containerFor(e, caps) !== null));
}

// ---------------------------------------------------------------- options

export const TOOL_IDS = ["aud.normalize", "aud.trim", "vid.trim", "vid.mute", "vid.snapshot", "vid.speed", "vid.compress"] as const;
export const SPEEDS = ["0.5", "0.75", "1.25", "1.5", "2", "4"] as const;
export const LEVELS = ["high", "medium", "small"] as const;
type Level = (typeof LEVELS)[number];

export function toolOptions(op: string): ToolOption[] {
  switch (op) {
    case "aud.trim":
    case "vid.trim":
      return [
        { key: "start", kind: "text", default: "0", placeholder: "0:00" },
        { key: "end", kind: "text", default: "", placeholder: "0:30" },
      ];
    case "vid.snapshot":
      return [{ key: "time", kind: "text", default: "0", placeholder: "0:05" }];
    case "vid.speed":
      return [{ key: "speed", kind: "choice", default: "2", choices: SPEEDS }];
    case "vid.compress":
      return [{ key: "level", kind: "choice", default: "medium", choices: LEVELS }];
    default:
      return [];
  }
}

function str(o: OptionValues, key: string, fallback: string): string {
  const v = o[key];
  return v === undefined || v === null ? fallback : String(v);
}

function levelOf(o: OptionValues): Level {
  const v = str(o, "level", "medium");
  return (LEVELS as readonly string[]).includes(v) ? (v as Level) : "medium";
}

function speedOf(o: OptionValues): number {
  const v = Number(str(o, "speed", "2").replace(",", "."));
  if (!Number.isFinite(v) || v < 0.25 || v > 8) throw new ConvertError("invalidSpeed");
  return v;
}

/** Splits a tempo factor into atempo stages that each lie in [0.5, 2]: 4 -> [2, 2], 0.25 -> [0.5, 0.5], 1.5 -> [1.5]. */
export function atempoChain(speed: number): number[] {
  if (!(speed > 0)) throw new ConvertError("invalidSpeed");
  const stages: number[] = [];
  let rest = speed;
  while (rest > 2 + 1e-9) {
    stages.push(2);
    rest /= 2;
  }
  while (rest < 0.5 - 1e-9) {
    stages.push(0.5);
    rest /= 0.5;
  }
  if (Math.abs(rest - 1) > 1e-9 || stages.length === 0) stages.push(Number(rest.toFixed(4)));
  return stages;
}

// ---------------------------------------------------------------- support matrix

interface Derived {
  audioIn: ReadonlySet<string>;
  videoIn: ReadonlySet<string>;
  audioOut: ReadonlySet<string>;
  videoOut: ReadonlySet<string>;
}
// The wheel calls supports() dozens of times per render; the capability lists never change for a given caps object.
const derivedCache = new WeakMap<MediaCaps, Derived>();

function derive(caps: MediaCaps): Derived {
  let d = derivedCache.get(caps);
  if (!d) {
    d = {
      audioIn: new Set(audioInputsFor(caps)),
      videoIn: new Set(videoInputsFor(caps)),
      audioOut: new Set(audioTargetsFor(caps)),
      videoOut: new Set(videoTargetsFor(caps)),
    };
    derivedCache.set(caps, d);
  }
  return d;
}

export function supportsOp(op: string, rawExt: string, caps: MediaCaps): boolean {
  const e = normalizeExt(rawExt);
  const d = derive(caps);
  const audioIn = d.audioIn.has(e);
  const videoIn = d.videoIn.has(e);
  if (op.startsWith("convert:")) {
    const t = op.slice("convert:".length);
    if (t === e) return false;
    if (audioIn) return d.audioOut.has(t);
    if (videoIn) {
      if (d.videoOut.has(t)) return true;
      // audio extraction; a gif has no audio track to extract
      return e !== "gif" && d.audioOut.has(t);
    }
    return false;
  }
  switch (op) {
    case "aud.normalize":
      return audioIn && audioSpecFor(e, caps) !== null && hasFilters(caps, ["loudnorm"]);
    case "aud.trim":
      return audioIn && audioSpecFor(e, caps) !== null;
    case "vid.trim":
    case "vid.compress":
      return videoIn && (EDIT_EXTS as readonly string[]).includes(e) && containerFor(e, caps) !== null;
    case "vid.speed":
      return videoIn && (EDIT_EXTS as readonly string[]).includes(e) && containerFor(e, caps) !== null && hasFilters(caps, ["setpts", "atempo"]);
    case "vid.mute": {
      const c = COPY_CONTAINERS[e];
      return videoIn && !!c && firstOf(caps.muxers, c.muxers) !== undefined;
    }
    case "vid.snapshot":
      return videoIn && has(caps.encoders, "png") && has(caps.muxers, "image2");
    default:
      return false;
  }
}

// ---------------------------------------------------------------- command construction

const EVEN_SCALE = "scale=trunc(iw/2)*2:trunc(ih/2)*2";
const CAP_SCALE = "scale='trunc(min(1280,iw)/2)*2':-2";
const GIF_FILTER = "fps=12,scale='min(480,iw)':-2:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse";
const LOUDNORM = "loudnorm=I=-16:TP=-1.5:LRA=11";

type Purpose = "convert" | "edit" | "compress";

function codecArgs(family: Family, caps: MediaCaps, purpose: Purpose, level: Level): { v: string[]; a: string[] } {
  const li = LEVELS.indexOf(level);
  switch (family) {
    case "x264": {
      const crf = purpose === "compress" ? [24, 28, 33][li] : 23;
      const preset = purpose === "edit" ? "ultrafast" : "veryfast";
      const ab = purpose === "compress" ? ["128k", "112k", "96k"][li] : "128k";
      return {
        v: ["-c:v", "libx264", "-preset", preset, "-crf", String(crf), "-pix_fmt", "yuv420p"],
        a: ["-c:a", "aac", "-b:a", ab],
      };
    }
    case "vp8": {
      const crf = purpose === "compress" ? [30, 36, 42][li] : 10;
      const bv = purpose === "compress" ? ["3M", "2M", "1M"][li] : "1M";
      const vorbis = has(caps.encoders, "libvorbis");
      const q = purpose === "compress" ? ["4", "3", "2"][li] : "4";
      const ob = purpose === "compress" ? ["128k", "96k", "64k"][li] : "128k";
      return {
        v: ["-c:v", "libvpx", "-deadline", "realtime", "-cpu-used", "8", "-crf", String(crf), "-b:v", bv],
        a: vorbis ? ["-c:a", "libvorbis", "-q:a", q] : ["-c:a", "libopus", "-b:a", ob],
      };
    }
    case "mpeg4": {
      const q = purpose === "compress" ? [4, 8, 12][li] : 5;
      const aq = purpose === "compress" ? ["3", "5", "6"][li] : "4";
      return { v: ["-c:v", "mpeg4", "-q:v", String(q)], a: ["-c:a", "libmp3lame", "-q:a", aq] };
    }
    case "wmv2":
      return { v: ["-c:v", "wmv2", "-b:v", "2M"], a: ["-c:a", "wmav2", "-b:a", "128k"] };
  }
}

function fail(code: string): never {
  throw new ConvertError(code);
}

interface VideoEncodeInput {
  ext: string;
  caps: MediaCaps;
  purpose: Purpose;
  level?: Level;
  /** Extra video filters placed before the even-dimension scale (e.g. setpts). */
  pre?: string[];
  /** Replace the default even-dimension scale (compress uses a width cap). */
  scale?: string | null;
  audioFilter?: string;
}

/** The part of the command shared by every re-encoding video op: filters, codecs, muxer. */
function videoEncodeArgs(i: VideoEncodeInput): { args: string[]; spec: ContainerSpec & { muxer: string } } {
  const spec = containerFor(i.ext, i.caps) ?? fail("unsupported");
  const level = i.level ?? "medium";
  const { v, a } = codecArgs(spec.family, i.caps, i.purpose, level);
  const vf = [...(i.pre ?? [])];
  const scale = i.scale === undefined ? EVEN_SCALE : i.scale;
  if (scale && has(i.caps.filters, "scale")) vf.push(scale);
  const args = [
    ...(vf.length ? ["-vf", vf.join(",")] : []),
    ...(i.audioFilter ? ["-af", i.audioFilter] : []),
    ...v,
    ...a,
    ...(spec.ext === "mp4" || spec.ext === "m4v" || spec.ext === "mov" ? ["-movflags", "+faststart"] : []),
    "-f",
    spec.muxer,
  ];
  return { args, spec };
}

const MAP_AV = ["-map", "0:v:0", "-map", "0:a:0?"];

function audioOut(ext: string, caps: MediaCaps, filters: string[] = []): { args: string[]; spec: AudioSpec & { muxer: string } } {
  const spec = audioSpecFor(ext, caps) ?? fail("unsupported");
  return { args: [...filters, ...spec.args, "-f", spec.muxer], spec };
}

/**
 * Builds the ffmpeg command for `op` on a file with extension `inputExt`. Throws ConvertError for options that
 * cannot work ("invalidTime", "invalidSpeed") or combinations the probed core cannot do ("unsupported").
 */
export function buildArgs(op: string, inputExt: string, options: OptionValues, caps: MediaCaps): BuiltArgs {
  const ext = normalizeExt(inputExt);
  if (!supportsOp(op, ext, caps)) fail("unsupported");
  const input = `in0.${ext}`;
  const out = (e: string, mime: string, kind: BuiltArgs["kind"], args: string[]): BuiltArgs => ({
    args,
    inputName: input,
    outputName: `out.${e}`,
    outputExt: e,
    mime,
    kind,
  });

  if (op.startsWith("convert:")) {
    const t = op.slice("convert:".length);
    const audioTarget = audioSpecFor(t, caps);
    if (audioTarget && AUDIO_SPECS[t]) {
      const a = audioOut(t, caps);
      return out(t, a.spec.mime, "audio", ["-i", input, "-vn", "-map", "0:a:0", ...a.args, `out.${t}`]);
    }
    if (t === "gif") {
      return out("gif", "image/gif", "video", ["-i", input, "-an", "-vf", GIF_FILTER, "-loop", "0", "-f", "gif", "out.gif"]);
    }
    const v = videoEncodeArgs({ ext: t, caps, purpose: "convert" });
    return out(t, v.spec.mime, "video", ["-i", input, ...MAP_AV, ...v.args, `out.${t}`]);
  }

  switch (op) {
    case "aud.normalize": {
      const a = audioOut(ext, caps, ["-af", LOUDNORM, "-ar", "48000"]);
      return out(ext, a.spec.mime, "audio", ["-i", input, "-vn", "-map", "0:a:0", ...a.args, `out.${ext}`]);
    }
    case "aud.trim": {
      const r = parseTimeRange(str(options, "start", "0"), str(options, "end", ""));
      const a = audioOut(ext, caps);
      return out(ext, a.spec.mime, "audio", [
        "-ss",
        formatFfmpegTime(r.start),
        "-i",
        input,
        ...(r.end === null ? [] : ["-t", formatFfmpegTime(r.end - r.start)]),
        "-vn",
        "-map",
        "0:a:0",
        ...a.args,
        `out.${ext}`,
      ]);
    }
    case "vid.trim": {
      const r = parseTimeRange(str(options, "start", "0"), str(options, "end", ""));
      const v = videoEncodeArgs({ ext, caps, purpose: "edit" });
      return out(ext, v.spec.mime, "video", [
        "-ss",
        formatFfmpegTime(r.start),
        "-i",
        input,
        ...(r.end === null ? [] : ["-t", formatFfmpegTime(r.end - r.start)]),
        ...MAP_AV,
        ...v.args,
        `out.${ext}`,
      ]);
    }
    case "vid.mute": {
      const c = COPY_CONTAINERS[ext] ?? fail("unsupported");
      const muxer = firstOf(caps.muxers, c.muxers) ?? fail("unsupported");
      const faststart = ext === "mp4" || ext === "m4v" || ext === "mov" ? ["-movflags", "+faststart"] : [];
      return out(ext, c.mime, "video", ["-i", input, "-map", "0:v:0", "-c:v", "copy", "-an", ...faststart, "-f", muxer, `out.${ext}`]);
    }
    case "vid.snapshot": {
      const t = parseTime(str(options, "time", "0"));
      if (t === null) fail("invalidTime");
      return out("png", "image/png", "image", [
        "-ss",
        formatFfmpegTime(t),
        "-i",
        input,
        "-map",
        "0:v:0",
        "-frames:v",
        "1",
        "-an",
        "-update",
        "1",
        "-f",
        "image2",
        "out.png",
      ]);
    }
    case "vid.speed": {
      const f = speedOf(options);
      const fs = String(Number(f.toFixed(4)));
      const v = videoEncodeArgs({
        ext,
        caps,
        purpose: "edit",
        pre: [`setpts=PTS/${fs}`],
        audioFilter: atempoChain(f)
          .map((s) => `atempo=${s}`)
          .join(","),
      });
      return out(ext, v.spec.mime, "video", ["-i", input, ...MAP_AV, ...v.args, `out.${ext}`]);
    }
    case "vid.compress": {
      const level = levelOf(options);
      const v = videoEncodeArgs({ ext, caps, purpose: "compress", level, scale: level === "high" ? EVEN_SCALE : CAP_SCALE });
      return out(ext, v.spec.mime, "video", ["-i", input, ...MAP_AV, ...v.args, `out.${ext}`]);
    }
    default:
      return fail("unsupported");
  }
}
