import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import type { MediaCaps } from "../caps";
import {
  LEVELS,
  SPEEDS,
  TOOL_IDS,
  atempoChain,
  audioInputsFor,
  audioTargetsFor,
  buildArgs,
  supportsOp,
  toolOptions,
  videoInputsFor,
  videoTargetsFor,
} from "../lib/ffmpeg-args";
import { CORE_VERSION, buildMediaCaps } from "../lib/ffmpeg-probe";
import { ConvertError } from "../types";

const fx = (name: string) => readFileSync(new URL(`./fixtures/ffmpeg-${name}.txt`, import.meta.url), "utf8");

/** Capabilities of the real core (from captured output). */
const FULL: MediaCaps = buildMediaCaps(CORE_VERSION, {
  encoders: fx("encoders"),
  decoders: fx("decoders"),
  muxers: fx("muxers"),
  demuxers: fx("demuxers"),
  filters: fx("filters"),
});

/** FULL minus some names, to test gating. */
function without(opts: Partial<Record<"encoders" | "decoders" | "muxers" | "demuxers" | "filters", string[]>>): MediaCaps {
  const c = { ...FULL };
  for (const k of Object.keys(opts) as (keyof typeof opts)[]) c[k] = FULL[k].filter((n) => !opts[k]!.includes(n));
  return c;
}

const AUDIO = ["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma", "aac"];

describe("capability helpers (real core)", () => {
  it("lists every catalog audio format as input and target", () => {
    expect(audioInputsFor(FULL)).toEqual(AUDIO);
    expect(audioTargetsFor(FULL)).toEqual(AUDIO);
  });
  it("lists the video inputs and targets", () => {
    expect(videoInputsFor(FULL).sort()).toEqual(["avi", "flv", "gif", "m4v", "mkv", "mov", "mp4", "webm", "wmv"]);
    expect(videoTargetsFor(FULL)).toEqual(["mp4", "mkv", "mov", "webm", "avi", "wmv", "gif"]);
  });
});

describe("capability gating (a target exists only if its encoder AND muxer exist)", () => {
  it("drops mp3 without libmp3lame and still offers the rest", () => {
    const c = without({ encoders: ["libmp3lame"] });
    expect(audioTargetsFor(c)).not.toContain("mp3");
    expect(audioTargetsFor(c)).toContain("wav");
    expect(supportsOp("convert:mp3", "wav", c)).toBe(false);
    // avi needs libmp3lame for its audio, so it goes too
    expect(videoTargetsFor(c)).not.toContain("avi");
    expect(videoTargetsFor(c)).toContain("mp4");
  });
  it("drops targets whose muxer is missing", () => {
    expect(audioTargetsFor(without({ muxers: ["flac"] }))).not.toContain("flac");
    expect(audioTargetsFor(without({ muxers: ["adts"] }))).not.toContain("aac");
    expect(audioTargetsFor(without({ muxers: ["asf"] }))).not.toContain("wma");
    expect(videoTargetsFor(without({ muxers: ["asf"] }))).not.toContain("wmv");
  });
  it("m4a prefers the ipod muxer and falls back to mp4", () => {
    const a = buildArgs("convert:m4a", "wav", {}, FULL).args;
    expect(a.slice(-3)).toEqual(["-f", "ipod", "out.m4a"]);
    const b = buildArgs("convert:m4a", "wav", {}, without({ muxers: ["ipod"] })).args;
    expect(b.slice(-3)).toEqual(["-f", "mp4", "out.m4a"]);
    expect(audioTargetsFor(without({ muxers: ["ipod", "mp4"] }))).not.toContain("m4a");
  });
  it("opus falls back from the opus muxer to ogg", () => {
    expect(buildArgs("convert:opus", "wav", {}, FULL).args.slice(-3)).toEqual(["-f", "opus", "out.opus"]);
    expect(buildArgs("convert:opus", "wav", {}, without({ muxers: ["opus"] })).args.slice(-3)).toEqual(["-f", "ogg", "out.opus"]);
  });
  it("webm uses libvorbis, or libopus when vorbis is missing, and is gone without libvpx", () => {
    expect(buildArgs("convert:webm", "mp4", {}, FULL).args).toContain("libvorbis");
    const noVorbis = without({ encoders: ["libvorbis"] });
    expect(buildArgs("convert:webm", "mp4", {}, noVorbis).args).toContain("libopus");
    expect(videoTargetsFor(without({ encoders: ["libvpx"] }))).not.toContain("webm");
    expect(videoTargetsFor(without({ encoders: ["libvorbis", "libopus"] }))).not.toContain("webm");
  });
  it("mp4/mkv/mov need libx264 and aac", () => {
    for (const enc of ["libx264", "aac"]) {
      const t = videoTargetsFor(without({ encoders: [enc] }));
      expect(t).not.toContain("mp4");
      expect(t).not.toContain("mkv");
      expect(t).not.toContain("mov");
      expect(t).toContain("webm");
    }
  });
  it("gif needs its encoder, muxer and the palette filters", () => {
    expect(videoTargetsFor(without({ filters: ["palettegen"] }))).not.toContain("gif");
    expect(videoTargetsFor(without({ filters: ["paletteuse"] }))).not.toContain("gif");
    expect(videoTargetsFor(without({ filters: ["split"] }))).not.toContain("gif");
    expect(videoTargetsFor(without({ encoders: ["gif"] }))).not.toContain("gif");
    expect(videoTargetsFor(without({ muxers: ["gif"] }))).not.toContain("gif");
  });
  it("an input is listed only with a demuxer AND a decoder", () => {
    expect(audioInputsFor(without({ demuxers: ["flac"] }))).not.toContain("flac");
    expect(audioInputsFor(without({ decoders: ["flac"] }))).not.toContain("flac");
    expect(audioInputsFor(without({ decoders: ["aac", "aac_fixed", "alac"] }))).not.toContain("m4a");
    expect(audioInputsFor(without({ decoders: ["aac", "aac_fixed", "alac"] }))).not.toContain("aac");
    expect(audioInputsFor(without({ decoders: ["mp3", "mp3float"] }))).not.toContain("mp3");
    expect(videoInputsFor(without({ demuxers: ["flv"] }))).not.toContain("flv");
    expect(videoInputsFor(without({ decoders: ["vp8", "vp9", "libvpx", "libvpx-vp9"] }))).not.toContain("webm");
    // webm is also readable through the matroska demuxer name flattened from "matroska,webm"
    expect(videoInputsFor(FULL)).toContain("webm");
  });
  it("tools need their filters", () => {
    expect(supportsOp("aud.normalize", "wav", without({ filters: ["loudnorm"] }))).toBe(false);
    expect(supportsOp("aud.trim", "wav", without({ filters: ["loudnorm"] }))).toBe(true);
    expect(supportsOp("vid.speed", "mp4", without({ filters: ["atempo"] }))).toBe(false);
    expect(supportsOp("vid.speed", "mp4", without({ filters: ["setpts"] }))).toBe(false);
    expect(supportsOp("vid.trim", "mp4", without({ filters: ["setpts", "atempo"] }))).toBe(true);
    expect(supportsOp("vid.snapshot", "mp4", without({ encoders: ["png"] }))).toBe(false);
    expect(supportsOp("vid.snapshot", "mp4", without({ muxers: ["image2"] }))).toBe(false);
    expect(supportsOp("vid.mute", "mp4", without({ muxers: ["mp4"] }))).toBe(false);
    expect(supportsOp("vid.compress", "mp4", without({ encoders: ["libx264"] }))).toBe(false);
  });
});

describe("supportsOp matrix", () => {
  it("never converts a format to itself", () => {
    for (const e of AUDIO) expect(supportsOp(`convert:${e}`, e, FULL), e).toBe(false);
    for (const e of ["mp4", "mkv", "mov", "webm", "avi", "wmv", "gif"]) expect(supportsOp(`convert:${e}`, e, FULL), e).toBe(false);
  });
  it("audio -> audio only", () => {
    expect(supportsOp("convert:mp3", "wav", FULL)).toBe(true);
    expect(supportsOp("convert:wav", "opus", FULL)).toBe(true);
    expect(supportsOp("convert:ogg", "opus", FULL)).toBe(true); // different codec, same container: allowed
    expect(supportsOp("convert:mp4", "mp3", FULL)).toBe(false);
    expect(supportsOp("convert:gif", "wav", FULL)).toBe(false);
  });
  it("video -> video and video -> audio extraction", () => {
    expect(supportsOp("convert:webm", "mp4", FULL)).toBe(true);
    expect(supportsOp("convert:mp3", "mp4", FULL)).toBe(true);
    expect(supportsOp("convert:gif", "webm", FULL)).toBe(true);
    expect(supportsOp("convert:mp4", "gif", FULL)).toBe(true);
    expect(supportsOp("convert:mp3", "gif", FULL)).toBe(false); // a gif has no audio to extract
    expect(supportsOp("convert:png", "mp4", FULL)).toBe(false);
  });
  it("normalizes extension aliases", () => {
    expect(supportsOp("convert:mp3", "AIF", FULL)).toBe(true);
    expect(supportsOp("convert:mp3", ".wav", FULL)).toBe(true);
  });
  it("audio tools: audio inputs only", () => {
    for (const e of AUDIO) {
      expect(supportsOp("aud.normalize", e, FULL), e).toBe(true);
      expect(supportsOp("aud.trim", e, FULL), e).toBe(true);
    }
    expect(supportsOp("aud.trim", "mp4", FULL)).toBe(false);
    expect(supportsOp("aud.normalize", "pdf", FULL)).toBe(false);
  });
  it("video tools: the right containers only", () => {
    for (const e of ["mp4", "m4v", "mov", "mkv", "webm", "avi"]) {
      for (const op of ["vid.trim", "vid.speed", "vid.compress", "vid.mute", "vid.snapshot"]) expect(supportsOp(op, e, FULL), `${op} ${e}`).toBe(true);
    }
    for (const e of ["wmv", "flv"]) {
      expect(supportsOp("vid.mute", e, FULL), e).toBe(true);
      expect(supportsOp("vid.snapshot", e, FULL), e).toBe(true);
      for (const op of ["vid.trim", "vid.speed", "vid.compress"]) expect(supportsOp(op, e, FULL), `${op} ${e}`).toBe(false);
    }
    expect(supportsOp("vid.mute", "gif", FULL)).toBe(false);
    expect(supportsOp("vid.snapshot", "gif", FULL)).toBe(true);
    expect(supportsOp("vid.trim", "wav", FULL)).toBe(false);
    expect(supportsOp("vid.mute", "mp3", FULL)).toBe(false);
  });
  it("unknown ops and extensions", () => {
    expect(supportsOp("img.compress", "mp4", FULL)).toBe(false);
    expect(supportsOp("convert:mp3", "xyz", FULL)).toBe(false);
    expect(supportsOp("convert:mp3", "", FULL)).toBe(false);
  });
});

describe("buildArgs: exact commands", () => {
  it("wav -> mp3", () => {
    expect(buildArgs("convert:mp3", "wav", {}, FULL)).toEqual({
      args: ["-i", "in0.wav", "-vn", "-map", "0:a:0", "-c:a", "libmp3lame", "-q:a", "2", "-f", "mp3", "out.mp3"],
      inputName: "in0.wav",
      outputName: "out.mp3",
      outputExt: "mp3",
      mime: "audio/mpeg",
      kind: "audio",
    });
  });

  it.each([
    ["m4a", ["-c:a", "aac", "-b:a", "192k", "-f", "ipod"], "audio/mp4"],
    ["wav", ["-c:a", "pcm_s16le", "-f", "wav"], "audio/wav"],
    ["flac", ["-c:a", "flac", "-f", "flac"], "audio/flac"],
    ["ogg", ["-c:a", "libvorbis", "-q:a", "5", "-f", "ogg"], "audio/ogg"],
    ["opus", ["-c:a", "libopus", "-b:a", "128k", "-f", "opus"], "audio/ogg;codecs=opus"],
    ["aiff", ["-c:a", "pcm_s16be", "-f", "aiff"], "audio/aiff"],
    ["wma", ["-c:a", "wmav2", "-b:a", "192k", "-f", "asf"], "audio/x-ms-wma"],
    ["aac", ["-c:a", "aac", "-b:a", "192k", "-f", "adts"], "audio/aac"],
  ])("mp3 -> %s", (t, tail, mime) => {
    const b = buildArgs(`convert:${t}`, "mp3", {}, FULL);
    expect(b.args).toEqual(["-i", "in0.mp3", "-vn", "-map", "0:a:0", ...tail, `out.${t}`]);
    expect(b.outputName).toBe(`out.${t}`);
    expect(b.mime).toBe(mime);
    expect(b.kind).toBe("audio");
  });

  it("video -> mp3 extracts audio (-vn, first audio stream)", () => {
    const b = buildArgs("convert:mp3", "mp4", {}, FULL);
    expect(b.args.slice(0, 5)).toEqual(["-i", "in0.mp4", "-vn", "-map", "0:a:0"]);
  });

  it("video -> mp4: x264 + aac, even-dimension scale, optional audio map, faststart", () => {
    const b = buildArgs("convert:mp4", "mov", {}, FULL);
    expect(b.args).toEqual([
      "-i", "in0.mov", "-map", "0:v:0", "-map", "0:a:0?",
      "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2",
      "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p",
      "-c:a", "aac", "-b:a", "128k",
      "-movflags", "+faststart", "-f", "mp4", "out.mp4",
    ]);
    expect(b.mime).toBe("video/mp4");
    expect(b.kind).toBe("video");
  });

  it("video -> mkv / mov / webm / avi / wmv use the right codecs and muxers", () => {
    const mkv = buildArgs("convert:mkv", "mp4", {}, FULL).args;
    expect(mkv).toContain("libx264");
    expect(mkv.slice(-3)).toEqual(["-f", "matroska", "out.mkv"]);
    expect(mkv).not.toContain("-movflags");
    const mov = buildArgs("convert:mov", "mp4", {}, FULL).args;
    expect(mov.slice(-3)).toEqual(["-f", "mov", "out.mov"]);
    const webm = buildArgs("convert:webm", "mp4", {}, FULL).args;
    expect(webm).toEqual(expect.arrayContaining(["-c:v", "libvpx", "-deadline", "realtime", "-cpu-used", "8", "-crf", "10", "-b:v", "1M", "-c:a", "libvorbis"]));
    expect(webm.slice(-3)).toEqual(["-f", "webm", "out.webm"]);
    const avi = buildArgs("convert:avi", "mp4", {}, FULL).args;
    expect(avi).toEqual(expect.arrayContaining(["-c:v", "mpeg4", "-q:v", "5", "-c:a", "libmp3lame"]));
    expect(avi.slice(-3)).toEqual(["-f", "avi", "out.avi"]);
    const wmv = buildArgs("convert:wmv", "mp4", {}, FULL);
    expect(wmv.args).toEqual(expect.arrayContaining(["-c:v", "wmv2", "-c:a", "wmav2"]));
    expect(wmv.args.slice(-3)).toEqual(["-f", "asf", "out.wmv"]);
    expect(wmv.mime).toBe("video/x-ms-wmv");
  });

  it("video -> gif: palette chain, fps 12, width <= 480, no audio, loop forever", () => {
    const b = buildArgs("convert:gif", "mp4", {}, FULL);
    expect(b.args).toEqual([
      "-i", "in0.mp4", "-an",
      "-vf", "fps=12,scale='min(480,iw)':-2:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse",
      "-loop", "0", "-f", "gif", "out.gif",
    ]);
    expect(b.mime).toBe("image/gif");
  });

  it("aud.normalize: loudnorm + 48 kHz, re-encoded with the input's own format", () => {
    const b = buildArgs("aud.normalize", "flac", {}, FULL);
    expect(b.args).toEqual([
      "-i", "in0.flac", "-vn", "-map", "0:a:0",
      "-af", "loudnorm=I=-16:TP=-1.5:LRA=11", "-ar", "48000",
      "-c:a", "flac", "-f", "flac", "out.flac",
    ]);
    expect(b.outputExt).toBe("flac");
    expect(buildArgs("aud.normalize", "m4a", {}, FULL).args.slice(-3)).toEqual(["-f", "ipod", "out.m4a"]);
  });

  it("aud.trim: -ss before -i, -t = end - start, defaults, empty end", () => {
    expect(buildArgs("aud.trim", "mp3", { start: "0:10", end: "0:25.5" }, FULL).args.slice(0, 5)).toEqual(["-ss", "10", "-i", "in0.mp3", "-t"]);
    expect(buildArgs("aud.trim", "mp3", { start: "0:10", end: "0:25.5" }, FULL).args[5]).toBe("15.5");
    const noEnd = buildArgs("aud.trim", "mp3", { start: "30" }, FULL).args;
    expect(noEnd.slice(0, 4)).toEqual(["-ss", "30", "-i", "in0.mp3"]);
    expect(noEnd).not.toContain("-t");
    const defaults = buildArgs("aud.trim", "wav", {}, FULL).args;
    expect(defaults.slice(0, 4)).toEqual(["-ss", "0", "-i", "in0.wav"]);
    expect(defaults).not.toContain("-t");
    // Arabic-Indic digits
    expect(buildArgs("aud.trim", "wav", { start: "٠:١٠", end: "٠:٢٠" }, FULL).args.slice(0, 6)).toEqual(["-ss", "10", "-i", "in0.wav", "-t", "10"]);
  });

  it("vid.trim: input-side -ss, output-side -t, ultrafast re-encode, same container", () => {
    const b = buildArgs("vid.trim", "mkv", { start: "5", end: "9" }, FULL);
    expect(b.args.slice(0, 6)).toEqual(["-ss", "5", "-i", "in0.mkv", "-t", "4"]);
    expect(b.args).toEqual(expect.arrayContaining(["-preset", "ultrafast", "-c:v", "libx264", "-c:a", "aac"]));
    expect(b.args.slice(-3)).toEqual(["-f", "matroska", "out.mkv"]);
    expect(b.outputExt).toBe("mkv");
    const w = buildArgs("vid.trim", "webm", { start: "0", end: "1" }, FULL);
    expect(w.args).toContain("libvpx");
    expect(w.args.slice(-3)).toEqual(["-f", "webm", "out.webm"]);
    const a = buildArgs("vid.trim", "avi", { start: "0", end: "1" }, FULL);
    expect(a.args).toContain("mpeg4");
    expect(a.args.slice(-3)).toEqual(["-f", "avi", "out.avi"]);
    // m4v keeps its extension but uses the mp4 muxer
    const m = buildArgs("vid.trim", "m4v", { start: "0", end: "1" }, FULL);
    expect(m.outputExt).toBe("m4v");
    expect(m.args.slice(-3)).toEqual(["-f", "mp4", "out.m4v"]);
  });

  it("vid.mute copies the video stream and drops audio", () => {
    expect(buildArgs("vid.mute", "mp4", {}, FULL).args).toEqual([
      "-i", "in0.mp4", "-map", "0:v:0", "-c:v", "copy", "-an", "-movflags", "+faststart", "-f", "mp4", "out.mp4",
    ]);
    expect(buildArgs("vid.mute", "wmv", {}, FULL).args.slice(-3)).toEqual(["-f", "asf", "out.wmv"]);
    expect(buildArgs("vid.mute", "flv", {}, FULL).args.slice(-3)).toEqual(["-f", "flv", "out.flv"]);
    expect(buildArgs("vid.mute", "mkv", {}, FULL).args).not.toContain("-movflags");
  });

  it("vid.snapshot: one PNG frame at the given time", () => {
    const b = buildArgs("vid.snapshot", "mp4", { time: "1:05" }, FULL);
    expect(b.args).toEqual(["-ss", "65", "-i", "in0.mp4", "-map", "0:v:0", "-frames:v", "1", "-an", "-update", "1", "-f", "image2", "out.png"]);
    expect(b.outputExt).toBe("png");
    expect(b.mime).toBe("image/png");
    expect(b.kind).toBe("image");
    expect(buildArgs("vid.snapshot", "mp4", {}, FULL).args.slice(0, 2)).toEqual(["-ss", "0"]);
  });

  it("vid.speed: setpts + atempo chain, optional audio, even scale", () => {
    const b = buildArgs("vid.speed", "mp4", { speed: "2" }, FULL);
    expect(b.args).toEqual(expect.arrayContaining(["-vf", "setpts=PTS/2,scale=trunc(iw/2)*2:trunc(ih/2)*2", "-af", "atempo=2"]));
    expect(b.args).toEqual(expect.arrayContaining(["-map", "0:a:0?"]));
    expect(buildArgs("vid.speed", "mp4", { speed: "4" }, FULL).args).toContain("atempo=2,atempo=2");
    expect(buildArgs("vid.speed", "mp4", { speed: "0.5" }, FULL).args).toContain("atempo=0.5");
    expect(buildArgs("vid.speed", "mp4", { speed: "0.75" }, FULL).args).toEqual(expect.arrayContaining(["setpts=PTS/0.75,scale=trunc(iw/2)*2:trunc(ih/2)*2"]));
    expect(buildArgs("vid.speed", "mp4", { speed: "1.25" }, FULL).args).toContain("atempo=1.25");
    expect(buildArgs("vid.speed", "mp4", {}, FULL).args).toContain("atempo=2"); // default
  });

  it("vid.compress: crf per level, 1280 width cap for medium/small, even scale for high", () => {
    const crf = (level: string, ext = "mp4") => {
      const a = buildArgs("vid.compress", ext, { level }, FULL).args;
      return a[a.indexOf("-crf") + 1];
    };
    expect([crf("high"), crf("medium"), crf("small")]).toEqual(["24", "28", "33"]);
    expect([crf("high", "webm"), crf("medium", "webm"), crf("small", "webm")]).toEqual(["30", "36", "42"]);
    const medium = buildArgs("vid.compress", "mp4", { level: "medium" }, FULL).args;
    expect(medium).toContain("scale='trunc(min(1280,iw)/2)*2':-2");
    const high = buildArgs("vid.compress", "mp4", { level: "high" }, FULL).args;
    expect(high).toContain("scale=trunc(iw/2)*2:trunc(ih/2)*2");
    expect(high).not.toContain("scale='trunc(min(1280,iw)/2)*2':-2");
    expect(buildArgs("vid.compress", "mp4", {}, FULL).args).toContain("28"); // default medium
    expect(buildArgs("vid.compress", "mp4", { level: "bogus" }, FULL).args).toContain("28");
    const avi = buildArgs("vid.compress", "avi", { level: "small" }, FULL).args;
    expect(avi).toEqual(expect.arrayContaining(["-c:v", "mpeg4", "-q:v", "12"]));
  });

  it("drops the scale filter when the core has none (never emits a filter that cannot run)", () => {
    const b = buildArgs("convert:mp4", "mov", {}, without({ filters: ["scale"] })).args;
    expect(b).not.toContain("-vf");
  });
});

describe("buildArgs: errors", () => {
  const code = (fn: () => unknown) => {
    try {
      fn();
    } catch (e) {
      return e instanceof ConvertError ? e.code : `other:${String(e)}`;
    }
    return "no error";
  };

  it("invalidTime for unparsable or empty ranges", () => {
    expect(code(() => buildArgs("aud.trim", "wav", { start: "x" }, FULL))).toBe("invalidTime");
    expect(code(() => buildArgs("aud.trim", "wav", { start: "10", end: "5" }, FULL))).toBe("invalidTime");
    expect(code(() => buildArgs("vid.trim", "mp4", { start: "0", end: "0" }, FULL))).toBe("invalidTime");
    expect(code(() => buildArgs("vid.snapshot", "mp4", { time: "soon" }, FULL))).toBe("invalidTime");
  });
  it("invalidSpeed", () => {
    expect(code(() => buildArgs("vid.speed", "mp4", { speed: "fast" }, FULL))).toBe("invalidSpeed");
    expect(code(() => buildArgs("vid.speed", "mp4", { speed: "100" }, FULL))).toBe("invalidSpeed");
    expect(code(() => buildArgs("vid.speed", "mp4", { speed: "0" }, FULL))).toBe("invalidSpeed");
  });
  it("unsupported combinations", () => {
    expect(code(() => buildArgs("convert:mp3", "mp3", {}, FULL))).toBe("unsupported");
    expect(code(() => buildArgs("convert:mp3", "pdf", {}, FULL))).toBe("unsupported");
    expect(code(() => buildArgs("vid.trim", "wmv", {}, FULL))).toBe("unsupported");
    expect(code(() => buildArgs("img.compress", "mp4", {}, FULL))).toBe("unsupported");
    expect(code(() => buildArgs("convert:mp3", "wav", {}, without({ encoders: ["libmp3lame"] })))).toBe("unsupported");
  });
});

describe("atempoChain", () => {
  it.each([
    [2, [2]],
    [4, [2, 2]],
    [8, [2, 2, 2]],
    [1.5, [1.5]],
    [1.25, [1.25]],
    [0.75, [0.75]],
    [0.5, [0.5]],
    [0.25, [0.5, 0.5]],
    [1, [1]],
  ])("x%s -> %j", (speed, stages) => {
    expect(atempoChain(speed)).toEqual(stages);
  });

  it("every stage is within [0.5, 2] and the product equals the speed", () => {
    for (const s of [0.26, 0.3, 0.5, 0.6, 0.99, 1, 1.01, 1.7, 2, 2.5, 3, 4, 5.5, 7.9, 8]) {
      const chain = atempoChain(s);
      for (const st of chain) {
        expect(st).toBeGreaterThanOrEqual(0.5);
        expect(st).toBeLessThanOrEqual(2);
      }
      expect(chain.reduce((a, b) => a * b, 1)).toBeCloseTo(s, 3);
    }
  });

  it("rejects non-positive speeds", () => {
    expect(() => atempoChain(0)).toThrow(ConvertError);
    expect(() => atempoChain(-1)).toThrow(ConvertError);
    expect(() => atempoChain(Number.NaN)).toThrow(ConvertError);
  });
});

describe("toolOptions", () => {
  it("declares the option schema per operation", () => {
    expect(toolOptions("aud.trim").map((o) => o.key)).toEqual(["start", "end"]);
    expect(toolOptions("vid.trim").map((o) => o.key)).toEqual(["start", "end"]);
    expect(toolOptions("vid.snapshot").map((o) => o.key)).toEqual(["time"]);
    expect(toolOptions("vid.speed")).toEqual([{ key: "speed", kind: "choice", default: "2", choices: SPEEDS }]);
    expect(toolOptions("vid.compress")).toEqual([{ key: "level", kind: "choice", default: "medium", choices: LEVELS }]);
    for (const op of ["aud.normalize", "vid.mute", "convert:mp3", "convert:mp4"]) expect(toolOptions(op), op).toEqual([]);
  });

  it("every default is accepted by buildArgs", () => {
    for (const op of TOOL_IDS) {
      const ext = op.startsWith("aud.") ? "mp3" : "mp4";
      const defaults = Object.fromEntries(toolOptions(op).map((o) => [o.key, o.default]));
      expect(() => buildArgs(op, ext, defaults, FULL), op).not.toThrow();
    }
  });

  it("speed choices are all valid", () => {
    for (const speed of SPEEDS) expect(() => buildArgs("vid.speed", "mp4", { speed }, FULL)).not.toThrow();
  });
});
