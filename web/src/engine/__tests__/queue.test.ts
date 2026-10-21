import { describe, expect, it } from "vitest";
import { JobQueue, type Task } from "../queue";

function deferred<T>() {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const tick = () => new Promise((r) => setTimeout(r, 0));

function makeTask(id: string, gate = deferred<string>(), log: string[] = []): { task: Task<string>; gate: ReturnType<typeof deferred<string>> } {
  return {
    gate,
    task: {
      id,
      run: async (report, signal) => {
        log.push(`start:${id}`);
        report(0.5);
        signal.addEventListener("abort", () => gate.reject(new DOMException("Cancelled", "AbortError")), { once: true });
        const out = await gate.promise;
        log.push(`end:${id}`);
        return out;
      },
    },
  };
}

describe("JobQueue", () => {
  it("never runs more than `concurrency` tasks at once and keeps FIFO order", async () => {
    const q = new JobQueue<string>(2);
    const log: string[] = [];
    const tasks = ["a", "b", "c", "d"].map((id) => makeTask(id, deferred<string>(), log));
    tasks.forEach((t) => q.add(t.task));
    await tick();
    expect(q.runningCount).toBe(2);
    expect(q.waitingCount).toBe(2);
    expect(log).toEqual(["start:a", "start:b"]);
    tasks[0].gate.resolve("A");
    await tick();
    expect(q.runningCount).toBe(2);
    expect(log).toContain("start:c");
    expect(log).not.toContain("start:d");
    tasks[1].gate.resolve("B");
    tasks[2].gate.resolve("C");
    tasks[3].gate.resolve("D");
    await q.idle();
    expect(["a", "b", "c", "d"].map((id) => q.get(id)!.state)).toEqual(["done", "done", "done", "done"]);
    expect(q.get("d")!.result).toBe("D");
    expect(q.get("a")!.progress).toBe(1);
  });

  it("concurrency never exceeds the bound even with many tasks", async () => {
    const q = new JobQueue<number>(3);
    let live = 0;
    let peak = 0;
    for (let i = 0; i < 20; i++) {
      q.add({
        id: `t${i}`,
        run: async () => {
          live++;
          peak = Math.max(peak, live);
          await new Promise((r) => setTimeout(r, 3));
          live--;
          return i;
        },
      });
    }
    await q.idle();
    expect(peak).toBe(3);
    expect(q.get("t19")!.result).toBe(19);
  });

  it("a failing task is isolated and reported", async () => {
    const q = new JobQueue<string>(1);
    q.add({ id: "bad", run: async () => Promise.reject(new Error("boom")) });
    q.add({ id: "good", run: async () => "ok" });
    await q.idle();
    expect(q.get("bad")!.state).toBe("error");
    expect((q.get("bad")!.error as Error).message).toBe("boom");
    expect(q.get("good")!.state).toBe("done");
  });

  it("cancelling a waiting task means it never starts", async () => {
    const q = new JobQueue<string>(1);
    const log: string[] = [];
    const a = makeTask("a", deferred<string>(), log);
    const b = makeTask("b", deferred<string>(), log);
    q.add(a.task);
    q.add(b.task);
    await tick();
    q.cancel("b");
    expect(q.get("b")!.state).toBe("cancelled");
    a.gate.resolve("A");
    await q.idle();
    expect(log).not.toContain("start:b");
  });

  it("cancelling a running task aborts its signal and frees the slot", async () => {
    const q = new JobQueue<string>(1);
    const log: string[] = [];
    const a = makeTask("a", deferred<string>(), log);
    const b = makeTask("b", deferred<string>(), log);
    q.add(a.task);
    q.add(b.task);
    await tick();
    q.cancel("a");
    expect(q.get("a")!.state).toBe("cancelled");
    await tick();
    expect(log).toContain("start:b");
    b.gate.resolve("B");
    await q.idle();
    expect(q.get("a")!.state).toBe("cancelled");
    expect(q.get("b")!.state).toBe("done");
  });

  it("cancelAll stops everything", async () => {
    const q = new JobQueue<string>(2);
    const ts = ["a", "b", "c"].map((id) => makeTask(id));
    ts.forEach((t) => q.add(t.task));
    await tick();
    q.cancelAll();
    await q.idle();
    expect(["a", "b", "c"].map((id) => q.get(id)!.state)).toEqual(["cancelled", "cancelled", "cancelled"]);
  });

  it("progress is clamped, monotonic and ignores garbage", async () => {
    const q = new JobQueue<string>(1);
    const seen: number[] = [];
    q.subscribe((s) => seen.push(s.progress));
    q.add({
      id: "p",
      run: async (report) => {
        report(0.4);
        report(-5);
        report(Number.NaN);
        report(7);
        return "x";
      },
    });
    await q.idle();
    expect(Math.max(...seen)).toBe(1);
    expect(seen.every((p, i) => i === 0 || p >= seen[i - 1])).toBe(true);
    expect(seen.every((p) => p >= 0 && p <= 1)).toBe(true);
  });

  it("rejects duplicate ids and bad concurrency", () => {
    expect(() => new JobQueue(0)).toThrow(RangeError);
    const q = new JobQueue<string>(1);
    q.add({ id: "x", run: async () => "1" });
    expect(() => q.add({ id: "x", run: async () => "2" })).toThrow();
  });
});
