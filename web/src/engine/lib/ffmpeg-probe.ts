// Parsers for the capability listings printed by the ffmpeg.wasm core (`-encoders`, `-decoders`, `-muxers`,
// `-demuxers`, `-filters`) and a classifier for failed runs. Pure and Node-compatible; tested against real output.
import type { MediaCaps } from "../caps";

/** Version of @ffmpeg/core served from /vendor/ffmpeg (a unit test keeps this equal to the installed package). */
export const CORE_VERSION = "0.12.10";

function lines(text: string): string[] {
  return text.split(/\r?\n/);
}

// " V....D libx264              libx264 H.264 ..."  (type letter + 5 flag chars + name). Legend rows have "=" as name.
const CODEC_ROW = /^ ([VAS])([.FSXBD]{5}) +([^\s=]\S*)/;

function parseCodecs(text: string, type: "V" | "A" | "S" | null): string[] {
  const out: string[] = [];
  for (const l of lines(text)) {
    const m = CODEC_ROW.exec(l);
    if (m && (type === null || m[1] === type)) out.push(m[3]);
  }
  return out;
}

/** All encoder names ("libx264", "aac", "libmp3lame"...). */
export function parseEncoders(text: string): string[] {
  return parseCodecs(text, null);
}

/** All decoder names ("h264", "aac", "mp3float"...). */
export function parseDecoders(text: string): string[] {
  return parseCodecs(text, null);
}

// "  E mp3     MP3 ..."  /  " DE mp4  ..."  /  " D  aac ..."  (one space, D flag, E flag, space, name)
const FORMAT_ROW = /^ ([D ])([E ]) +(\S+)/;

function parseFormats(text: string, flag: "D" | "E"): string[] {
  const out: string[] = [];
  for (const l of lines(text)) {
    const m = FORMAT_ROW.exec(l);
    if (!m) continue;
    if ((flag === "D" ? m[1] : m[2]) !== flag) continue;
    // demuxer names may be comma lists: "mov,mp4,m4a,3gp,3g2,mj2", "matroska,webm"
    for (const n of m[3].split(",")) if (n) out.push(n);
  }
  return out;
}

export function parseMuxers(text: string): string[] {
  return parseFormats(text, "E");
}

/** Flattened: "matroska,webm" yields both "matroska" and "webm". */
export function parseDemuxers(text: string): string[] {
  return parseFormats(text, "D");
}

// " ... abench            A->A       Benchmark ..."   (3 flag chars, name, io signature)
const FILTER_ROW = /^ ([T.][S.][C.]) +([^\s=|]\S*)\s+\S+->\S+/;

export function parseFilters(text: string): string[] {
  const out: string[] = [];
  for (const l of lines(text)) {
    const m = FILTER_ROW.exec(l);
    if (m) out.push(m[2]);
  }
  return out;
}

export interface ProbeTexts {
  encoders: string;
  decoders: string;
  muxers: string;
  demuxers: string;
  filters: string;
}

export function buildMediaCaps(coreVersion: string, t: ProbeTexts): MediaCaps {
  return {
    coreVersion,
    encoders: parseEncoders(t.encoders),
    decoders: parseDecoders(t.decoders),
    muxers: parseMuxers(t.muxers),
    demuxers: parseDemuxers(t.demuxers),
    filters: parseFilters(t.filters),
  };
}

/** Validates a MediaCaps read back from localStorage (never trust stored JSON). */
export function isValidMediaCaps(x: unknown): x is MediaCaps {
  if (!x || typeof x !== "object") return false;
  const c = x as Record<string, unknown>;
  const list = (v: unknown) => Array.isArray(v) && v.length > 0 && v.every((s) => typeof s === "string");
  return (
    typeof c.coreVersion === "string" &&
    list(c.encoders) &&
    list(c.decoders) &&
    list(c.muxers) &&
    list(c.demuxers) &&
    list(c.filters)
  );
}

export type FailureCode = "noAudio" | "decodeFailed" | "outOfMemory" | "encodeFailed";

/** Maps the log of a failed ffmpeg run to one of our error codes. */
export function classifyFfmpegLog(log: readonly string[]): FailureCode {
  const text = log.join("\n");
  if (/cannot enlarge memory|out of memory|\bOOM\b|memory access out of bounds|Aborted\(.*(?:memory|OOM)/i.test(text)) {
    return "outOfMemory";
  }
  if (/Stream map '[^']*:a[^']*' matches no streams|does not contain any stream/i.test(text)) return "noAudio";
  if (
    /Invalid data found when processing input|moov atom not found|could not find codec parameters|Error while decoding|Decoder \(codec [^)]*\) not found|Unknown format|Failed to read frame size|\bin\d+\.\w+: (?:Invalid argument|End of file)|no decoder/i.test(
      text,
    )
  ) {
    return "decodeFailed";
  }
  return "encodeFailed";
}
