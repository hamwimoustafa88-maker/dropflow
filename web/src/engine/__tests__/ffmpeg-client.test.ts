import { describe, expect, it } from "vitest";
import { ERROR_NOT_LOADED, ERROR_TERMINATED, FFmpeg, type WorkerPort } from "../converters/ffmpeg-client";

// A fake of the VENDORED @ffmpeg/ffmpeg worker.js protocol: { id, type, data } in, { id, type, data } (or ERROR) out.
class FakeWorker implements WorkerPort {
  onmessage: WorkerPort["onmessage"] = null;
  onerror: WorkerPort["onerror"] = null;
  terminated = false;
  posted: { msg: { id: number; type: string; data: any }; transfer?: Transferable[] }[] = []; // eslint-disable-line @typescript-eslint/no-explicit-any
  auto: ((msg: { id: number; type: string; data: any }) => unknown | undefined) | null = null; // eslint-disable-line @typescript-eslint/no-explicit-any
  postMessage(message: unknown, transfer?: Transferable[]): void {
    const msg = message as { id: number; type: string; data: any }; // eslint-disable-line @typescript-eslint/no-explicit-any
    this.posted.push({ msg, transfer });
    const reply = this.auto?.(msg);
    if (reply !== undefined) queueMicrotask(() => this.onmessage?.({ data: { id: msg.id, type: msg.type, data: reply } }));
  }
  terminate(): void {
    this.terminated = true;
  }
  emit(type: string, data: unknown, id?: number): void {
    this.onmessage?.({ data: { id, type, data } });
  }
}

const cfg = { classWorkerURL: "http://x/vendor/ffmpeg/worker.js", coreURL: "http://x/vendor/ffmpeg/ffmpeg-core.js", wasmURL: "http://x/vendor/ffmpeg/ffmpeg-core.wasm" };

function make(auto?: FakeWorker["auto"]) {
  const worker = new FakeWorker();
  worker.auto = auto ?? ((m) => (m.type === "LOAD" ? true : m.type === "EXEC" ? 0 : true));
  const urls: string[] = [];
  const ff = new FFmpeg((url) => {
    urls.push(url);
    return worker;
  });
  return { ff, worker, urls };
}

describe("ffmpeg client (vendored worker protocol)", () => {
  it("creates the worker from the given URL and sends LOAD with exactly what worker.js reads", async () => {
    const { ff, worker, urls } = make();
    expect(ff.loaded).toBe(false);
    await ff.load(cfg);
    expect(urls).toEqual([cfg.classWorkerURL]);
    expect(worker.posted[0].msg.type).toBe("LOAD");
    expect(worker.posted[0].msg.data).toEqual({ coreURL: cfg.coreURL, wasmURL: cfg.wasmURL });
    expect(ff.loaded).toBe(true);
  });

  it("exec / readFile / deleteFile use the message types and payloads of the original FFmpeg class", async () => {
    const { ff, worker } = make((m) => (m.type === "READ_FILE" ? new Uint8Array([1, 2, 3]) : m.type === "EXEC" ? 0 : true));
    await ff.load(cfg);
    expect(await ff.exec(["-i", "in0.wav", "out.mp3"])).toBe(0);
    expect(await ff.readFile("out.mp3")).toEqual(new Uint8Array([1, 2, 3]));
    await ff.deleteFile("out.mp3");
    const sent = worker.posted.map((p) => [p.msg.type, p.msg.data]);
    expect(sent).toContainEqual(["EXEC", { args: ["-i", "in0.wav", "out.mp3"], timeout: -1 }]);
    expect(sent).toContainEqual(["READ_FILE", { path: "out.mp3", encoding: "binary" }]);
    expect(sent).toContainEqual(["DELETE_FILE", { path: "out.mp3" }]);
  });

  it("transfers the buffer when writing a file and uses unique ids", async () => {
    const { ff, worker } = make();
    await ff.load(cfg);
    const data = new Uint8Array(8);
    await ff.writeFile("in0.mp4", data);
    const write = worker.posted.find((p) => p.msg.type === "WRITE_FILE")!;
    expect(write.msg.data).toEqual({ path: "in0.mp4", data });
    expect(write.transfer).toEqual([data.buffer]);
    const ids = worker.posted.map((p) => p.msg.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it("rejects with the worker's ERROR message", async () => {
    const worker = new FakeWorker();
    const ff = new FFmpeg(() => worker);
    worker.auto = (m) => (m.type === "LOAD" ? true : undefined);
    await ff.load(cfg);
    const p = ff.exec(["bad"]);
    worker.emit("ERROR", "Error: FS error", worker.posted.at(-1)!.msg.id);
    await expect(p).rejects.toThrow("FS error");
  });

  it("dispatches LOG and PROGRESS events and supports off()", async () => {
    const { ff, worker } = make();
    await ff.load(cfg);
    const logs: string[] = [];
    const progress: number[] = [];
    const onLog = (e: { message: string }) => logs.push(e.message);
    ff.on("log", onLog);
    ff.on("progress", (e) => progress.push(e.progress));
    worker.emit("LOG", { type: "stderr", message: "hello" });
    worker.emit("PROGRESS", { progress: 0.5, time: 10 });
    ff.off("log", onLog);
    worker.emit("LOG", { type: "stderr", message: "ignored" });
    expect(logs).toEqual(["hello"]);
    expect(progress).toEqual([0.5]);
  });

  it("terminate() rejects pending calls, kills the worker and requires a new load()", async () => {
    const worker = new FakeWorker();
    const ff = new FFmpeg(() => worker);
    worker.auto = (m) => (m.type === "LOAD" ? true : undefined); // exec never answers
    await ff.load(cfg);
    const pending = ff.exec(["long"]);
    ff.terminate();
    await expect(pending).rejects.toThrow(ERROR_TERMINATED);
    expect(worker.terminated).toBe(true);
    expect(ff.loaded).toBe(false);
    await expect(ff.exec(["x"])).rejects.toThrow(ERROR_NOT_LOADED);
  });

  it("a worker error event fails the pending calls", async () => {
    const worker = new FakeWorker();
    const ff = new FFmpeg(() => worker);
    worker.auto = (m) => (m.type === "LOAD" ? true : undefined);
    await ff.load(cfg);
    const pending = ff.exec(["x"]);
    worker.onerror?.({ message: "script crashed" });
    await expect(pending).rejects.toThrow("script crashed");
  });

  it("the vendored worker file speaks the same protocol (guards against a package update changing it)", async () => {
    const { readFileSync } = await import("node:fs");
    const src = readFileSync(new URL("../../../public/vendor/ffmpeg/worker.js", import.meta.url), "utf8");
    const consts = readFileSync(new URL("../../../public/vendor/ffmpeg/const.js", import.meta.url), "utf8");
    for (const t of ["LOAD", "EXEC", "WRITE_FILE", "READ_FILE", "DELETE_FILE", "ERROR", "LOG", "PROGRESS"]) expect(consts).toContain(`"${t}"`);
    expect(src).toContain("coreURL");
    expect(src).toContain("wasmURL");
    expect(src).toContain("self.onmessage");
    expect(src).toMatch(/data: _data|data:\s*_data/);
  });
});
