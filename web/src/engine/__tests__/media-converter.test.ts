import { readFileSync } from "node:fs";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MediaCaps } from "../caps";
import { CORE_VERSION, buildMediaCaps } from "../lib/ffmpeg-probe";
import { ConvertError, isAbort } from "../types";

// The engine (real @ffmpeg/ffmpeg + a Web Worker) cannot exist in Node: it is replaced by a recording fake.
// The real ffmpeg commands are exercised in media-ffmpeg-real.test.ts.
const mocks = vi.hoisted(() => ({ probeMedia: vi.fn(), getEngine: vi.fn(), terminateEngine: vi.fn() }));
vi.mock("../converters/media-engine", () => mocks);

const fx = (name: string) => readFileSync(new URL(`./fixtures/ffmpeg-${name}.txt`, import.meta.url), "utf8");
const FULL: MediaCaps = buildMediaCaps(CORE_VERSION, {
  encoders: fx("encoders"),
  decoders: fx("decoders"),
  muxers: fx("muxers"),
  demuxers: fx("demuxers"),
  filters: fx("filters"),
});
const CACHE_KEY = `dropflow.media.caps.v1.${CORE_VERSION}`;

function memoryStorage(initial: Record<string, string> = {}) {
  const m = new Map(Object.entries(initial));
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => void m.set(k, v),
    removeItem: (k: string) => void m.delete(k),
    clear: () => m.clear(),
    _map: m,
  };
}

async function fresh() {
  vi.resetModules();
  const caps = await import("../caps");
  const media = await import("../converters/media");
  return { caps, media };
}

beforeEach(() => {
  mocks.probeMedia.mockReset();
  mocks.getEngine.mockReset();
  mocks.terminateEngine.mockReset();
  vi.stubGlobal("localStorage", memoryStorage());
});
afterEach(() => {
  vi.unstubAllGlobals();
});

describe("mediaConverter manifest", () => {
  it("is truthful: nothing is supported until the core has been probed", async () => {
    const { caps, media } = await fresh();
    expect(caps.getMediaCaps()).toBeNull();
    expect(media.mediaConverter.supports("convert:mp3", "wav")).toBe(false);
    expect(media.mediaConverter.supports("vid.trim", "mp4")).toBe(false);
    caps.setMediaCaps(FULL);
    expect(media.mediaConverter.supports("convert:mp3", "wav")).toBe(true);
    expect(media.mediaConverter.supports("vid.trim", "mp4")).toBe(true);
    expect(media.mediaConverter.supports("convert:mp3", "mp3")).toBe(false);
    expect(media.mediaConverter.supports("convert:mp3", "docx")).toBe(false);
    expect(media.mediaConverter.supports("img.compress", "mp4")).toBe(false);
    caps.setMediaCaps({ ...FULL, encoders: FULL.encoders.filter((e) => e !== "libmp3lame") });
    expect(media.mediaConverter.supports("convert:mp3", "wav")).toBe(false);
    expect(media.mediaConverter.supports("convert:flac", "wav")).toBe(true);
  });

  it("declares operations, options, runtime, suffixes and limits", async () => {
    const { media } = await fresh();
    const c = media.mediaConverter;
    expect(c.id).toBe("media");
    expect(c.operations).toEqual(
      expect.arrayContaining(["convert:mp3", "convert:mp4", "convert:gif", "aud.normalize", "aud.trim", "vid.trim", "vid.mute", "vid.snapshot", "vid.speed", "vid.compress"]),
    );
    expect(new Set(c.operations).size).toBe(c.operations.length);
    expect(c.operations.every((o) => o.startsWith("convert:") || /^(aud|vid)\./.test(o))).toBe(true);
    expect(c.options("vid.trim").map((o) => o.key)).toEqual(["start", "end"]);
    expect(c.options("convert:mp3")).toEqual([]);
    expect(c.runtime("convert:mp3", "wav")).toBe("main");
    expect(c.runtime("vid.mute", "mp4")).toBe("main");
    expect(c.suffix("convert:mp3")).toBe("converted");
    expect(c.suffix("vid.trim")).toBe("trimmed");
    expect(c.suffix("aud.trim")).toBe("trimmed");
    expect(c.suffix("vid.compress")).toBe("compressed");
    expect(c.suffix("vid.snapshot")).toBe("frame");
    expect(c.suffix("vid.mute")).toBe("muted");
    expect(c.suffix("vid.speed")).toBe("speed");
    expect(c.suffix("aud.normalize")).toBe("normalized");
    expect(c.maxBytes?.("convert:mp3")).toBe(700 * 1024 * 1024);
    expect(c.keepOutputNames).toBeUndefined();
  });

  it("importing the manifest never touches the engine", async () => {
    await fresh();
    expect(mocks.probeMedia).not.toHaveBeenCalled();
    expect(mocks.getEngine).not.toHaveBeenCalled();
  });
});

describe("loadMediaCaps", () => {
  it("uses a cached probe for this core version without loading the engine", async () => {
    vi.stubGlobal("localStorage", memoryStorage({ [CACHE_KEY]: JSON.stringify(FULL) }));
    const { caps, media } = await fresh();
    const got = await media.loadMediaCaps();
    expect(got).toEqual(FULL);
    expect(caps.getMediaCaps()).toEqual(FULL);
    expect(mocks.probeMedia).not.toHaveBeenCalled();
  });

  it("probes once for concurrent calls, stores the result and publishes it", async () => {
    mocks.probeMedia.mockResolvedValue(FULL);
    const { caps, media } = await fresh();
    let notified = 0;
    caps.subscribeCaps(() => notified++);
    const [a, b] = await Promise.all([media.loadMediaCaps(), media.loadMediaCaps()]);
    expect(mocks.probeMedia).toHaveBeenCalledTimes(1);
    expect(a).toEqual(FULL);
    expect(b).toEqual(FULL);
    expect(caps.getMediaCaps()).toEqual(FULL);
    expect(notified).toBeGreaterThan(0);
    expect(JSON.parse(localStorage.getItem(CACHE_KEY)!)).toEqual(FULL);
    // already known: no second probe
    await media.loadMediaCaps();
    expect(mocks.probeMedia).toHaveBeenCalledTimes(1);
  });

  it.each([
    ["corrupt JSON", "{not json"],
    ["wrong shape", JSON.stringify({ coreVersion: CORE_VERSION })],
    ["other core version", JSON.stringify({ ...FULL, coreVersion: "0.0.1" })],
    ["empty lists", JSON.stringify({ ...FULL, encoders: [] })],
  ])("ignores a %s cache entry and probes", async (_name, raw) => {
    vi.stubGlobal("localStorage", memoryStorage({ [CACHE_KEY]: raw }));
    mocks.probeMedia.mockResolvedValue(FULL);
    const { media } = await fresh();
    expect(await media.loadMediaCaps()).toEqual(FULL);
    expect(mocks.probeMedia).toHaveBeenCalledTimes(1);
  });

  it("force re-probes even when caps are known and cached", async () => {
    vi.stubGlobal("localStorage", memoryStorage({ [CACHE_KEY]: JSON.stringify(FULL) }));
    mocks.probeMedia.mockResolvedValue({ ...FULL, filters: [...FULL.filters, "extra"] });
    const { caps, media } = await fresh();
    await media.loadMediaCaps();
    expect(mocks.probeMedia).not.toHaveBeenCalled();
    const forced = await media.loadMediaCaps({ force: true });
    expect(mocks.probeMedia).toHaveBeenCalledTimes(1);
    expect(forced.filters).toContain("extra");
    expect(caps.getMediaCaps()?.filters).toContain("extra");
  });

  it("works when localStorage is unavailable or throws", async () => {
    vi.stubGlobal("localStorage", {
      getItem: () => {
        throw new Error("denied");
      },
      setItem: () => {
        throw new Error("quota");
      },
    });
    mocks.probeMedia.mockResolvedValue(FULL);
    const { media } = await fresh();
    expect(await media.loadMediaCaps()).toEqual(FULL);
    vi.unstubAllGlobals();
    vi.stubGlobal("localStorage", undefined);
    const again = await fresh();
    mocks.probeMedia.mockResolvedValue(FULL);
    expect(await again.media.loadMediaCaps()).toEqual(FULL);
  });

  it("a failed probe rejects, leaves caps unset, and the next call retries", async () => {
    mocks.probeMedia.mockRejectedValueOnce(new ConvertError("engineLoadFailed")).mockResolvedValueOnce(FULL);
    const { caps, media } = await fresh();
    await expect(media.loadMediaCaps()).rejects.toMatchObject({ code: "engineLoadFailed" });
    expect(caps.getMediaCaps()).toBeNull();
    expect(await media.loadMediaCaps()).toEqual(FULL);
  });
});

describe("releaseMedia", () => {
  it("is a no-op when the engine was never used", async () => {
    const { media } = await fresh();
    media.releaseMedia();
    await new Promise((r) => setTimeout(r, 10));
    expect(mocks.terminateEngine).not.toHaveBeenCalled();
  });

  it("terminates the engine once it has been used", async () => {
    mocks.probeMedia.mockResolvedValue(FULL);
    const { media } = await fresh();
    await media.loadMediaCaps();
    media.releaseMedia();
    await vi.waitFor(() => expect(mocks.terminateEngine).toHaveBeenCalledTimes(1));
  });
});

describe("run() (engine faked)", () => {
  const signal = () => new AbortController().signal;
  const job = (operation: string, file: File, options: Record<string, string | number | boolean> = {}) => ({ id: "j", operation, files: [file], options });
  const wav = () => new File([new Uint8Array(2000)], "ملف صوتي.wav", { type: "audio/wav" });
  const engineWith = (res: Partial<{ code: number; outputs: (Uint8Array | null)[]; log: string[] }>) => {
    const exec = vi.fn(async (_args: string[], o: { onProgress?: (f: number) => void }) => {
      o.onProgress?.(0.4);
      o.onProgress?.(1);
      return { code: 0, outputs: [new Uint8Array(1500)], log: [], ...res };
    });
    mocks.getEngine.mockReturnValue({ exec });
    return exec;
  };

  it("builds the command, hands the input over under a safe name and returns a NEW file with a good name and type", async () => {
    const exec = engineWith({});
    const { caps, media } = await fresh();
    caps.setMediaCaps(FULL);
    const input = wav();
    const progress = vi.fn();
    const out = await media.mediaConverter.run(job("convert:mp3", input), progress, signal());

    expect(exec).toHaveBeenCalledTimes(1);
    const [args, opts] = exec.mock.calls[0] as unknown as [string[], { inputs: { name: string; data: Blob }[]; outputs: string[] }];
    expect(args).toEqual(["-i", "in0.wav", "-vn", "-map", "0:a:0", "-c:a", "libmp3lame", "-q:a", "2", "-f", "mp3", "out.mp3"]);
    expect(opts.inputs).toHaveLength(1);
    expect(opts.inputs[0].name).toBe("in0.wav"); // never the user's (Arabic) file name
    expect(opts.inputs[0].data).toBe(input);
    expect(opts.outputs).toEqual(["out.mp3"]);

    expect(out).toHaveLength(1);
    expect(out[0]).not.toBe(input);
    expect(out[0].name).toBe("ملف صوتي.mp3");
    expect(out[0].type).toBe("audio/mpeg");
    expect(out[0].size).toBe(1500);
    // progress: starts at 0, never reports 1 before the file exists, ends at exactly 1
    const values = progress.mock.calls.map((c) => c[0] as number);
    expect(values[0]).toBe(0);
    expect(values).toContain(0.4);
    expect(values).toContain(0.99);
    expect(values.at(-1)).toBe(1);
    expect(mocks.terminateEngine).not.toHaveBeenCalled();
  });

  it("passes tool options through (trim) and names the PNG snapshot", async () => {
    const exec = engineWith({});
    const { caps, media } = await fresh();
    caps.setMediaCaps(FULL);
    const mp4 = new File([new Uint8Array(100)], "clip.MP4");
    await media.mediaConverter.run(job("vid.trim", mp4, { start: "1", end: "3" }), vi.fn(), signal());
    expect((exec.mock.calls[0] as unknown as [string[]])[0].slice(0, 6)).toEqual(["-ss", "1", "-i", "in0.mp4", "-t", "2"]);
    const snap = await media.mediaConverter.run(job("vid.snapshot", mp4, { time: "2" }), vi.fn(), signal());
    expect(snap[0].name).toBe("clip.png");
    expect(snap[0].type).toBe("image/png");
  });

  it("loads the capability probe on demand when caps are still unknown", async () => {
    engineWith({});
    mocks.probeMedia.mockResolvedValue(FULL);
    const { media } = await fresh();
    const out = await media.mediaConverter.run(job("convert:flac", wav()), vi.fn(), signal());
    expect(mocks.probeMedia).toHaveBeenCalledTimes(1);
    expect(out[0].name).toBe("ملف صوتي.flac");
  });

  it("maps a failed ffmpeg run to a ConvertError code", async () => {
    engineWith({ code: 1, outputs: [null], log: ["Stream map '0:a:0' matches no streams.", "Aborted()"] });
    const { caps, media } = await fresh();
    caps.setMediaCaps(FULL);
    await expect(media.mediaConverter.run(job("convert:mp3", new File([new Uint8Array(10)], "a.mp4")), vi.fn(), signal())).rejects.toMatchObject({ code: "noAudio" });
    engineWith({ code: 1, outputs: [null], log: ["in0.mp3: Invalid argument"] });
    await expect(media.mediaConverter.run(job("convert:wav", new File([new Uint8Array(10)], "a.mp3")), vi.fn(), signal())).rejects.toMatchObject({ code: "decodeFailed" });
  });

  it("reports noFrame when a snapshot produced nothing", async () => {
    engineWith({ code: 0, outputs: [null], log: ["Aborted()"] });
    const { caps, media } = await fresh();
    caps.setMediaCaps(FULL);
    await expect(media.mediaConverter.run(job("vid.snapshot", new File([new Uint8Array(10)], "a.mp4"), { time: "99" }), vi.fn(), signal())).rejects.toMatchObject({ code: "noFrame" });
  });

  it("reports notSmaller when compression did not shrink the file", async () => {
    engineWith({ outputs: [new Uint8Array(5000)] });
    const { caps, media } = await fresh();
    caps.setMediaCaps(FULL);
    const small = new File([new Uint8Array(4000)], "a.mp4");
    await expect(media.mediaConverter.run(job("vid.compress", small, { level: "small" }), vi.fn(), signal())).rejects.toMatchObject({ code: "notSmaller" });
    // a smaller result is fine
    engineWith({ outputs: [new Uint8Array(3000)] });
    const ok = await media.mediaConverter.run(job("vid.compress", small, { level: "small" }), vi.fn(), signal());
    expect(ok[0].size).toBe(3000);
    expect(ok[0].name).toBe("a.mp4");
  });

  it("refuses files over 700 MB before touching the engine", async () => {
    const exec = engineWith({});
    const { caps, media } = await fresh();
    caps.setMediaCaps(FULL);
    const huge = new File([new Uint8Array(10)], "huge.mp4");
    Object.defineProperty(huge, "size", { value: 701 * 1024 * 1024 });
    await expect(media.mediaConverter.run(job("convert:mp3", huge), vi.fn(), signal())).rejects.toMatchObject({ code: "tooLarge", params: { max: "700 MB" } });
    expect(exec).not.toHaveBeenCalled();
  });

  it("restarts the engine after a big input (a grown wasm heap is never returned), even when the job fails", async () => {
    engineWith({});
    const { caps, media } = await fresh();
    caps.setMediaCaps(FULL);
    const big = new File([new Uint8Array(10)], "big.mp4");
    Object.defineProperty(big, "size", { value: 100 * 1024 * 1024 });
    await media.mediaConverter.run(job("convert:mp3", big), vi.fn(), signal());
    expect(mocks.terminateEngine).toHaveBeenCalledTimes(1);
    engineWith({ code: 1, outputs: [null], log: ["boom"] });
    await expect(media.mediaConverter.run(job("convert:mp3", big), vi.fn(), signal())).rejects.toMatchObject({ code: "encodeFailed" });
    expect(mocks.terminateEngine).toHaveBeenCalledTimes(2);
  });

  it("an already-aborted signal stops before the engine; invalid options and unsupported inputs are ConvertErrors", async () => {
    const exec = engineWith({});
    const { caps, media } = await fresh();
    caps.setMediaCaps(FULL);
    const ac = new AbortController();
    ac.abort();
    await expect(media.mediaConverter.run(job("convert:mp3", wav()), vi.fn(), ac.signal)).rejects.toSatisfy(isAbort);
    expect(exec).not.toHaveBeenCalled();
    await expect(media.mediaConverter.run(job("aud.trim", wav(), { start: "oops" }), vi.fn(), signal())).rejects.toMatchObject({ code: "invalidTime" });
    await expect(media.mediaConverter.run(job("convert:mp3", new File([new Uint8Array(10)], "x.pdf")), vi.fn(), signal())).rejects.toMatchObject({ code: "unsupported" });
    await expect(media.mediaConverter.run({ id: "j", operation: "convert:mp3", files: [], options: {} }, vi.fn(), signal())).rejects.toMatchObject({ code: "noFiles" });
    expect(exec).not.toHaveBeenCalled();
  });
});
