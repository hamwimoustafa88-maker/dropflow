import { describe, expect, it, vi } from "vitest";
import { WorkerPool, type WorkerCommand, type WorkerLike, type WorkerReply } from "../pool";
import { ConvertError, isAbort } from "../types";

class FakeWorker implements WorkerLike {
  onmessage: ((event: { data: unknown }) => void) | null = null;
  onerror: ((event: unknown) => void) | null = null;
  terminated = false;
  sent: WorkerCommand[] = [];
  constructor(private readonly behaviour: (w: FakeWorker, cmd: WorkerCommand) => void) {}
  postMessage(message: unknown): void {
    const cmd = message as WorkerCommand;
    this.sent.push(cmd);
    this.behaviour(this, cmd);
  }
  terminate(): void {
    this.terminated = true;
  }
  reply(r: WorkerReply): void {
    this.onmessage?.({ data: r });
  }
}

const req = (id: string) => ({ id, converterId: "images", operation: "convert:png", files: [new File(["x"], "a.jpg")], options: {} });
const out = new File(["y"], "a.png");
const tick = (ms = 0) => new Promise((r) => setTimeout(r, ms));

describe("WorkerPool", () => {
  it("runs a job, forwards progress and returns outputs", async () => {
    const pool = new WorkerPool(
      () =>
        new FakeWorker((w, cmd) => {
          if (cmd.type === "run") {
            w.reply({ type: "progress", id: cmd.id, fraction: 0.5 });
            w.reply({ type: "done", id: cmd.id, outputs: [out] });
          }
        }),
      2,
    );
    const progress = vi.fn();
    const files = await pool.run(req("j1"), progress, new AbortController().signal);
    expect(files).toEqual([out]);
    expect(progress).toHaveBeenCalledWith(0.5, undefined);
  });

  it("reuses an idle worker and spawns lazily up to maxSize", async () => {
    let spawned = 0;
    const pool = new WorkerPool(
      () => {
        spawned++;
        return new FakeWorker((w, cmd) => {
          if (cmd.type === "run") queueMicrotask(() => w.reply({ type: "done", id: cmd.id, outputs: [out] }));
        });
      },
      2,
    );
    await pool.run(req("a"), () => {}, new AbortController().signal);
    await pool.run(req("b"), () => {}, new AbortController().signal);
    expect(spawned).toBe(1);
    await Promise.all([pool.run(req("c"), () => {}, new AbortController().signal), pool.run(req("d"), () => {}, new AbortController().signal), pool.run(req("e"), () => {}, new AbortController().signal)]);
    expect(spawned).toBe(2);
    expect(pool.size).toBe(2);
  });

  it("queues work beyond maxSize and never runs more than maxSize at once", async () => {
    let live = 0;
    let peak = 0;
    const pool = new WorkerPool(
      () =>
        new FakeWorker((w, cmd) => {
          if (cmd.type !== "run") return;
          live++;
          peak = Math.max(peak, live);
          setTimeout(() => {
            live--;
            w.reply({ type: "done", id: cmd.id, outputs: [out] });
          }, 5);
        }),
      2,
    );
    await Promise.all(Array.from({ length: 7 }, (_, i) => pool.run(req(`j${i}`), () => {}, new AbortController().signal)));
    expect(peak).toBe(2);
  });

  it("maps worker errors back to ConvertError / Error", async () => {
    const pool = new WorkerPool(
      () =>
        new FakeWorker((w, cmd) => {
          if (cmd.type !== "run") return;
          if (cmd.id === "known") w.reply({ type: "error", id: cmd.id, aborted: false, code: "corrupt", params: { a: 1 }, message: "corrupt: x" });
          else w.reply({ type: "error", id: cmd.id, aborted: false, message: "plain" });
        }),
      1,
    );
    const known = await pool.run(req("known"), () => {}, new AbortController().signal).catch((e) => e);
    expect(known).toBeInstanceOf(ConvertError);
    expect((known as ConvertError).code).toBe("corrupt");
    expect((known as ConvertError).params).toEqual({ a: 1 });
    const plain = await pool.run(req("plain"), () => {}, new AbortController().signal).catch((e) => e);
    expect(plain).toBeInstanceOf(Error);
    expect(plain).not.toBeInstanceOf(ConvertError);
  });

  it("cooperative cancel: sends a cancel message and rejects with AbortError when the worker acknowledges", async () => {
    let worker!: FakeWorker;
    const pool = new WorkerPool(() => (worker = new FakeWorker((w, cmd) => {
      if (cmd.type === "cancel") w.reply({ type: "error", id: cmd.id, aborted: true, message: "Cancelled" });
    })), 1);
    const ac = new AbortController();
    const p = pool.run(req("c1"), () => {}, ac.signal);
    await tick();
    ac.abort();
    const err = await p.catch((e) => e);
    expect(isAbort(err)).toBe(true);
    expect(worker.sent.some((m) => m.type === "cancel" && m.id === "c1")).toBe(true);
    expect(worker.terminated).toBe(false);
  });

  it("hard cancel: a worker that ignores cancel is terminated and replaced", async () => {
    const workers: FakeWorker[] = [];
    const pool = new WorkerPool(
      () => {
        const w = new FakeWorker((self, cmd) => {
          if (cmd.type === "run" && workers.length > 1) self.reply({ type: "done", id: cmd.id, outputs: [out] });
          // the first worker never answers (a stuck job)
        });
        workers.push(w);
        return w;
      },
      1,
      20,
    );
    const ac = new AbortController();
    const stuck = pool.run(req("stuck"), () => {}, ac.signal);
    await tick();
    ac.abort();
    const err = await stuck.catch((e) => e);
    expect(isAbort(err)).toBe(true);
    expect(workers[0].terminated).toBe(true);
    // the pool recovers with a fresh worker
    const ok = await pool.run(req("next"), () => {}, new AbortController().signal);
    expect(ok).toEqual([out]);
    expect(workers).toHaveLength(2);
  });

  it("aborting while waiting for a slot rejects without ever starting", async () => {
    const pool = new WorkerPool(() => new FakeWorker(() => {}), 1);
    const first = new AbortController();
    void pool.run(req("busy"), () => {}, first.signal).catch(() => {});
    await tick();
    const ac = new AbortController();
    const waiting = pool.run(req("waiter"), () => {}, ac.signal);
    await tick();
    ac.abort();
    expect(isAbort(await waiting.catch((e) => e))).toBe(true);
    first.abort();
    pool.dispose();
  });

  it("an already-aborted signal rejects immediately; a crashing worker rejects the active job", async () => {
    const pool = new WorkerPool(() => new FakeWorker(() => {}), 1);
    const ac = new AbortController();
    ac.abort();
    expect(isAbort(await pool.run(req("x"), () => {}, ac.signal).catch((e) => e))).toBe(true);

    let w!: FakeWorker;
    const pool2 = new WorkerPool(() => (w = new FakeWorker(() => {})), 1);
    const p = pool2.run(req("y"), () => {}, new AbortController().signal);
    await tick();
    w.onerror?.({ message: "script failed" });
    const err = await p.catch((e) => e);
    expect(err).toBeInstanceOf(ConvertError);
    expect((err as ConvertError).code).toBe("workerFailed");
    expect(w.terminated).toBe(true);
  });
});
