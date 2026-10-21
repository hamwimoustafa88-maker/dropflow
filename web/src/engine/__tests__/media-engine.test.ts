import { readFileSync } from "node:fs";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CORE_VERSION, buildMediaCaps } from "../lib/ffmpeg-probe";

// A fake of the FFmpeg client (converters/ffmpeg-client.ts, same method surface as the @ffmpeg/ffmpeg class). It lets us test the engine wrapper's own logic:
// URLs, serialisation, cleanup, cancellation, recycling and crash recovery. Real ffmpeg runs in media-ffmpeg-real.test.ts.
const h = vi.hoisted(() => {
  type Cb = (e: never) => void;
  class FakeFFmpeg {
    static instances: FakeFFmpeg[] = [];
    static onLoad: ((f: FakeFFmpeg) => Promise<void> | void) | null = null;
    static onExec: ((f: FakeFFmpeg, args: string[], n: number) => Promise<number> | number) | null = null;
    loaded = false;
    terminated = false;
    loadConfig: Record<string, string> | null = null;
    files = new Map<string, Uint8Array>();
    deleted: string[] = [];
    execCalls: string[][] = [];
    private log: Cb[] = [];
    private prog: Cb[] = [];
    private pending = new Set<(e: Error) => void>();
    constructor() {
      FakeFFmpeg.instances.push(this);
    }
    on(ev: "log" | "progress", cb: Cb) {
      (ev === "log" ? this.log : this.prog).push(cb);
    }
    off(ev: "log" | "progress", cb: Cb) {
      if (ev === "log") this.log = this.log.filter((c) => c !== cb);
      else this.prog = this.prog.filter((c) => c !== cb);
    }
    emitLog(message: string) {
      this.log.forEach((c) => c({ type: "stderr", message } as never));
    }
    emitProgress(progress: number) {
      this.prog.forEach((c) => c({ progress, time: 0 } as never));
    }
    private guard<T>(p: Promise<T>): Promise<T> {
      return new Promise<T>((resolve, reject) => {
        this.pending.add(reject);
        p.then(resolve, reject).finally(() => this.pending.delete(reject));
      });
    }
    load(cfg: Record<string, string>) {
      this.loadConfig = cfg;
      return this.guard(
        (async () => {
          await FakeFFmpeg.onLoad?.(this);
          this.loaded = true;
          return true;
        })(),
      );
    }
    async writeFile(name: string, data: Uint8Array) {
      this.files.set(name, data);
      return true;
    }
    exec(args: string[]) {
      this.execCalls.push(args);
      const n = FakeFFmpeg.instances.reduce((a, f) => a + f.execCalls.length, 0);
      return this.guard(Promise.resolve(FakeFFmpeg.onExec ? FakeFFmpeg.onExec(this, args, n) : 0));
    }
    async readFile(name: string) {
      const d = this.files.get(name);
      if (!d) throw "FS error: ENOENT";
      return d;
    }
    async deleteFile(name: string) {
      this.deleted.push(name);
      this.files.delete(name);
      return true;
    }
    terminate() {
      this.terminated = true;
      this.loaded = false;
      this.pending.forEach((rej) => rej(new Error("called FFmpeg.terminate()")));
      this.pending.clear();
    }
  }
  return { FakeFFmpeg };
});
vi.mock("../converters/ffmpeg-client", () => ({ FFmpeg: h.FakeFFmpeg }));

const F = h.FakeFFmpeg;
const fx = (name: string) => readFileSync(new URL(`./fixtures/ffmpeg-${name}.txt`, import.meta.url), "utf8");

async function engine() {
  vi.resetModules();
  const mod = await import("../converters/media-engine");
  return { mod, e: mod.getEngine() };
}
const blob = (n = 10) => new Blob([new Uint8Array(n)]);
const noArgs = { inputs: [], outputs: [] };
const abortRejects = (p: Promise<unknown>) => expect(p).rejects.toMatchObject({ name: "AbortError" });

beforeEach(() => {
  F.instances = [];
  F.onLoad = null;
  F.onExec = null;
  vi.stubGlobal("location", { origin: "http://localhost:3200" });
});
afterEach(() => {
  vi.unstubAllGlobals();
});

describe("loading", () => {
  it("loads the core from this origin only: absolute /vendor/ffmpeg URLs, no CDN", async () => {
    const { e } = await engine();
    await e.exec(["-version"], noArgs);
    expect(F.instances).toHaveLength(1);
    expect(F.instances[0].loadConfig).toEqual({
      classWorkerURL: "http://localhost:3200/vendor/ffmpeg/worker.js",
      coreURL: "http://localhost:3200/vendor/ffmpeg/ffmpeg-core.js",
      wasmURL: "http://localhost:3200/vendor/ffmpeg/ffmpeg-core.wasm",
    });
    for (const u of Object.values(F.instances[0].loadConfig!)) expect(new URL(u).origin).toBe("http://localhost:3200");
  });

  it("creates one instance and reuses it", async () => {
    const { e } = await engine();
    await e.exec(["-a"], noArgs);
    await e.exec(["-b"], noArgs);
    expect(F.instances).toHaveLength(1);
    expect(F.instances[0].execCalls).toEqual([["-a"], ["-b"]]);
    expect(e.loaded).toBe(true);
  });

  it("maps a failing load to engineLoadFailed and retries with a fresh instance next time", async () => {
    let first = true;
    F.onLoad = () => {
      if (first) {
        first = false;
        throw new Error("404 /vendor/ffmpeg/ffmpeg-core.wasm");
      }
    };
    const { e } = await engine();
    await expect(e.exec(["-a"], noArgs)).rejects.toMatchObject({ code: "engineLoadFailed" });
    expect(F.instances[0].terminated).toBe(true);
    await e.exec(["-a"], noArgs);
    expect(F.instances).toHaveLength(2);
    expect(F.instances[1].terminated).toBe(false);
  });

  it("aborting while the core is still downloading cancels the load", async () => {
    F.onLoad = () => new Promise(() => {}); // never finishes
    const { e } = await engine();
    const ac = new AbortController();
    const p = e.exec(["-a"], { ...noArgs, signal: ac.signal });
    await vi.waitFor(() => expect(F.instances).toHaveLength(1));
    ac.abort();
    await abortRejects(p);
    expect(F.instances[0].terminated).toBe(true);
  });
});

describe("exec", () => {
  it("writes Blob inputs, runs, reads outputs and cleans up the wasm file system", async () => {
    const { e } = await engine();
    F.onExec = (f) => {
      f.files.set("out.mp3", new Uint8Array([1, 2, 3]));
      f.emitLog("size=3kB");
      return 0;
    };
    const r = await e.exec(["-i", "in0.wav", "out.mp3"], { inputs: [{ name: "in0.wav", data: blob(8) }], outputs: ["out.mp3"] });
    expect(r.code).toBe(0);
    expect(r.outputs).toEqual([new Uint8Array([1, 2, 3])]);
    expect(r.log).toEqual(["size=3kB"]);
    const ff = F.instances[0];
    expect(ff.deleted.sort()).toEqual(["in0.wav", "out.mp3"]);
    expect(ff.files.size).toBe(0);
  });

  it("returns null for a missing or empty output, and for every output when ffmpeg failed", async () => {
    const { e } = await engine();
    F.onExec = (f) => {
      f.files.set("empty.bin", new Uint8Array(0));
      return 0;
    };
    const r = await e.exec(["x"], { inputs: [], outputs: ["missing.png", "empty.bin"] });
    expect(r.outputs).toEqual([null, null]);
    F.onExec = (f) => {
      f.files.set("out.mp3", new Uint8Array([9]));
      return 1;
    };
    const bad = await e.exec(["x"], { inputs: [], outputs: ["out.mp3"] });
    expect(bad.code).toBe(1);
    expect(bad.outputs).toEqual([null]);
  });

  it("forwards only sane progress fractions", async () => {
    const { e } = await engine();
    F.onExec = (f) => {
      [-1021678, 0.3, 1.7, 1, Number.NaN].forEach((p) => f.emitProgress(p));
      return 0;
    };
    const seen: number[] = [];
    await e.exec(["x"], { ...noArgs, onProgress: (p) => seen.push(p) });
    expect(seen).toEqual([0.3, 1]);
  });

  it("keeps only the last maxLogLines log lines", async () => {
    const { e } = await engine();
    F.onExec = (f) => {
      for (let i = 0; i < 1000; i++) f.emitLog(`l${i}`);
      return 0;
    };
    expect((await e.exec(["x"], noArgs)).log).toHaveLength(400);
    expect((await e.exec(["x"], { ...noArgs, maxLogLines: 5000 })).log).toHaveLength(1000);
    expect((await e.exec(["x"], noArgs)).log.at(-1)).toBe("l999");
  });

  it("detaches its listeners after each run", async () => {
    const { e } = await engine();
    F.onExec = (f) => {
      f.emitLog("a");
      return 0;
    };
    await e.exec(["x"], noArgs);
    const second = await e.exec(["x"], noArgs);
    expect(second.log).toEqual(["a"]); // not ["a","a"]: no stale listener from run 1
  });

  it("runs jobs strictly one at a time, in order", async () => {
    const { e } = await engine();
    const timeline: string[] = [];
    let release!: () => void;
    F.onExec = async (_f, args) => {
      timeline.push(`start ${args[0]}`);
      if (args[0] === "slow") await new Promise<void>((r) => (release = r));
      timeline.push(`end ${args[0]}`);
      return 0;
    };
    const a = e.exec(["slow"], noArgs);
    const b = e.exec(["fast"], noArgs);
    await vi.waitFor(() => expect(timeline).toEqual(["start slow"]));
    release();
    await Promise.all([a, b]);
    expect(timeline).toEqual(["start slow", "end slow", "start fast", "end fast"]);
  });

  it("a job aborted while queued rejects at once and does not disturb the running one", async () => {
    const { e } = await engine();
    let release!: () => void;
    F.onExec = async (_f, args) => {
      if (args[0] === "slow") await new Promise<void>((r) => (release = r));
      return 0;
    };
    const first = e.exec(["slow"], noArgs);
    await vi.waitFor(() => expect(F.instances[0]?.execCalls).toHaveLength(1));
    const ac = new AbortController();
    const queued = e.exec(["queued"], { ...noArgs, signal: ac.signal });
    ac.abort();
    await abortRejects(queued);
    release();
    await expect(first).resolves.toMatchObject({ code: 0 });
    expect(F.instances[0].terminated).toBe(false);
    expect(F.instances[0].execCalls).toEqual([["slow"]]);
    await e.exec(["after"], noArgs); // the queue still works
    expect(F.instances[0].execCalls.at(-1)).toEqual(["after"]);
  });

  it("an already-aborted signal does nothing at all", async () => {
    const { e } = await engine();
    const ac = new AbortController();
    ac.abort();
    await abortRejects(e.exec(["x"], { ...noArgs, signal: ac.signal }));
    expect(F.instances).toHaveLength(0);
  });
});

describe("cancel", () => {
  it("aborting a running command terminates ffmpeg and the next job gets a fresh instance", async () => {
    const { e } = await engine();
    F.onExec = (_f, args) => (args[0] === "hang" ? new Promise<number>(() => {}) : 0);
    const ac = new AbortController();
    const p = e.exec(["hang"], { inputs: [{ name: "in0.mp4", data: blob() }], outputs: ["out.mp4"], signal: ac.signal });
    await vi.waitFor(() => expect(F.instances[0]?.execCalls).toHaveLength(1));
    ac.abort();
    await abortRejects(p);
    expect(F.instances[0].terminated).toBe(true);
    expect(e.loaded).toBe(false);
    await e.exec(["ok"], noArgs);
    expect(F.instances).toHaveLength(2);
    expect(F.instances[1].execCalls).toEqual([["ok"]]);
  });
});

describe("the core's per-command leak (found against the real core: it dies after ~60 commands)", () => {
  it("recycles the instance every RECYCLE_AFTER commands, before it can die", async () => {
    const { mod, e } = await engine();
    for (let i = 0; i < mod.RECYCLE_AFTER; i++) await e.exec(["x"], noArgs);
    expect(F.instances).toHaveLength(1);
    await e.exec(["x"], noArgs); // command number RECYCLE_AFTER + 1 starts on a fresh instance
    expect(F.instances).toHaveLength(2);
    expect(F.instances[0].terminated).toBe(true);
    expect(F.instances[1].terminated).toBe(false);
    expect(mod.RECYCLE_AFTER).toBeLessThan(60);
  });

  it("retries once on a fresh instance when a REUSED instance crashes (wasm 'memory access out of bounds')", async () => {
    const { e } = await engine();
    await e.exec(["warmup"], noArgs);
    F.onExec = (f, args) => {
      if (args[0] === "job" && f === F.instances[0]) throw new Error("RuntimeError: memory access out of bounds");
      f.files.set("out.mp3", new Uint8Array([7, 7]));
      return 0;
    };
    const r = await e.exec(["job"], { inputs: [{ name: "in0.wav", data: blob() }], outputs: ["out.mp3"] });
    expect(r.outputs).toEqual([new Uint8Array([7, 7])]);
    expect(F.instances).toHaveLength(2);
    expect(F.instances[0].terminated).toBe(true);
    expect(F.instances[1].files.size).toBe(0);
    expect(F.instances[1].execCalls).toEqual([["job"]]);
  });

  it("does not retry a crash on a fresh instance: the file is too big, reported as outOfMemory", async () => {
    const { e } = await engine();
    F.onExec = () => {
      throw new Error("RuntimeError: memory access out of bounds");
    };
    await expect(e.exec(["job"], { inputs: [{ name: "in0.wav", data: blob() }], outputs: [] })).rejects.toMatchObject({ code: "outOfMemory" });
    expect(F.instances).toHaveLength(1);
    expect(F.instances[0].terminated).toBe(true);
  });

  it("gives up after one retry (a second crash is reported, not looped)", async () => {
    const { e } = await engine();
    await e.exec(["warmup"], noArgs);
    F.onExec = (_f, args) => {
      if (args[0] === "job") throw new Error("RuntimeError: memory access out of bounds");
      return 0;
    };
    await expect(e.exec(["job"], { inputs: [{ name: "in0.wav", data: blob() }], outputs: [] })).rejects.toMatchObject({ code: "outOfMemory" });
    expect(F.instances.length).toBeLessThanOrEqual(3);
  });

  it("cannot retry Uint8Array inputs (they were transferred), so the crash is reported", async () => {
    const { e } = await engine();
    await e.exec(["warmup"], noArgs);
    F.onExec = () => {
      throw new Error("RuntimeError: memory access out of bounds");
    };
    await expect(e.exec(["job"], { inputs: [{ name: "in0.wav", data: new Uint8Array(4) }], outputs: [] })).rejects.toMatchObject({ code: "outOfMemory" });
    expect(F.instances).toHaveLength(1);
  });

  it("maps genuine out-of-memory text and unknown errors, and always starts clean afterwards", async () => {
    const { e } = await engine();
    F.onExec = () => {
      throw "Cannot enlarge memory arrays to size 2147483648";
    };
    await expect(e.exec(["a"], noArgs)).rejects.toMatchObject({ code: "outOfMemory" });
    F.onExec = () => {
      throw new Error("something odd");
    };
    await expect(e.exec(["b"], noArgs)).rejects.toMatchObject({ code: "encodeFailed" });
    F.onExec = null;
    await e.exec(["c"], noArgs);
    expect(F.instances.at(-1)!.terminated).toBe(false);
  });
});

describe("probe", () => {
  it("runs the five listing commands and builds the capability lists from their output", async () => {
    const { e } = await engine();
    F.onExec = (f, args) => {
      const kind = args[1].slice(1);
      for (const l of fx(kind).split("\n")) f.emitLog(l);
      return 0;
    };
    const caps = await e.probe();
    expect(F.instances[0].execCalls).toEqual([
      ["-hide_banner", "-encoders"],
      ["-hide_banner", "-decoders"],
      ["-hide_banner", "-muxers"],
      ["-hide_banner", "-demuxers"],
      ["-hide_banner", "-filters"],
    ]);
    expect(caps).toEqual(
      buildMediaCaps(CORE_VERSION, { encoders: fx("encoders"), decoders: fx("decoders"), muxers: fx("muxers"), demuxers: fx("demuxers"), filters: fx("filters") }),
    );
    expect(caps.encoders).toContain("libx264");
    expect(caps.demuxers).toContain("webm");
  });

  it("does not truncate long listings (the real decoders list has ~490 lines)", async () => {
    const { e } = await engine();
    F.onExec = (f, args) => {
      if (args[1] === "-decoders") {
        for (let i = 0; i < 900; i++) f.emitLog(` V....D codec${i}              Codec number ${i}`);
        f.emitLog(" A....D lastdecoder           Last");
      } else f.emitLog(fx(args[1].slice(1)));
      return 0;
    };
    const caps = await e.probe();
    expect(caps.decoders).toHaveLength(901);
    expect(caps.decoders.at(-1)).toBe("lastdecoder");
  });
});

describe("terminateEngine", () => {
  it("kills the running instance and a new one is created on demand", async () => {
    const { mod, e } = await engine();
    await e.exec(["a"], noArgs);
    mod.terminateEngine();
    expect(F.instances[0].terminated).toBe(true);
    await e.exec(["b"], noArgs);
    expect(F.instances).toHaveLength(2);
  });

  it("is harmless before the engine exists", async () => {
    vi.resetModules();
    const mod = await import("../converters/media-engine");
    expect(() => mod.terminateEngine()).not.toThrow();
  });
});
