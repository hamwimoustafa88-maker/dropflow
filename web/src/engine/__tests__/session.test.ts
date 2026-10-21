import { describe, expect, it, vi } from "vitest";
import { createRunner, runtimeFor } from "../runner";
import { Session, isTextResult, type JobRunner } from "../session";
import { ConvertError, type Converter } from "../types";

function fakeConverter(over: Partial<Converter> = {}): Converter {
  return {
    id: "fake",
    operations: ["convert:png"],
    supports: () => true,
    options: () => [],
    runtime: () => "worker",
    suffix: () => "converted",
    run: async () => [],
    ...over,
  };
}

const registryOf = (c: Converter | undefined) => ({ find: () => c });
const mkSession = (runner: JobRunner, c: Converter | undefined = fakeConverter(), concurrency = 2) =>
  new Session({ registry: registryOf(c), runner, concurrency, createUrl: (b) => `blob:test/${(b as File).name}`, revokeUrl: () => {} });

const png = (name: string, type = "image/png") => new File(["data"], name, { type });

describe("Session", () => {
  it("names results `name (converted).ext`, never the input name, and keeps them unique", async () => {
    const s = mkSession(async () => [png("out.png")]);
    s.submit({ operation: "convert:png", files: [png("photo.jpg", "image/jpeg")], options: {}, multi: false });
    s.submit({ operation: "convert:png", files: [png("photo.jpg", "image/jpeg")], options: {}, multi: false });
    await s.idle();
    const names = s.getSnapshot().results.map((r) => r.name);
    expect(names).toEqual(["photo (converted).png", "photo (converted) (2).png"]);
    expect(names).not.toContain("photo.jpg");
    expect(s.getSnapshot().results[0].sourceName).toBe("photo.jpg");
  });

  it("numbers multi-file results and keeps one job per file for non-multi tools", async () => {
    const runner = vi.fn<JobRunner>(async () => [png("page-1.png"), png("page-2.png"), png("page-3.png")]);
    const s = mkSession(runner);
    const ids = s.submit({ operation: "pdf.to-images", files: [png("doc.pdf", "application/pdf"), png("two.pdf", "application/pdf")], options: {}, multi: false });
    expect(ids).toHaveLength(2);
    await s.idle();
    expect(runner).toHaveBeenCalledTimes(2);
    expect(s.getSnapshot().results.map((r) => r.name)).toEqual([
      "doc (converted 1).png",
      "doc (converted 2).png",
      "doc (converted 3).png",
      "two (converted 1).png",
      "two (converted 2).png",
      "two (converted 3).png",
    ]);
  });

  it("multi tools get ONE job with all files and are named after the first input", async () => {
    const runner = vi.fn<JobRunner>(async () => [new File(["z"], "merged.pdf", { type: "application/pdf" })]);
    const s = mkSession(runner, fakeConverter({ suffix: () => "merged" }));
    const ids = s.submit({ operation: "pdf.merge", files: [png("a.pdf"), png("b.pdf"), png("c.pdf")], options: {}, multi: true });
    expect(ids).toHaveLength(1);
    await s.idle();
    expect(runner.mock.calls[0][1].files.map((f) => f.name)).toEqual(["a.pdf", "b.pdf", "c.pdf"]);
    expect(s.getSnapshot().results.map((r) => r.name)).toEqual(["a (merged).pdf"]);
  });

  it("keeps folder paths for converters that keep names (archive extraction) and flattens the display name", async () => {
    const s = mkSession(
      async () => [new File(["1"], "docs/a.txt", { type: "text/plain" }), new File(["2"], "docs/a.txt", { type: "text/plain" })],
      fakeConverter({ keepOutputNames: () => true }),
    );
    s.submit({ operation: "arc.extract", files: [png("pack.zip")], options: {}, multi: false });
    await s.idle();
    const r = s.getSnapshot().results;
    expect(r.map((x) => x.path)).toEqual(["docs/a.txt", "docs/a (2).txt"]);
    expect(r.map((x) => x.name)).toEqual(["a.txt", "a (2).txt"]);
  });

  it("reports ConvertError codes and params to the UI and keeps other jobs running", async () => {
    const s = mkSession(async (_c, job) => {
      if (job.files[0].name === "bad.png") throw new ConvertError("corrupt", { x: 1 });
      return [png("ok.png")];
    });
    s.submit({ operation: "convert:png", files: [png("bad.png"), png("good.png")], options: {}, multi: false });
    await s.idle();
    const [bad, good] = s.getSnapshot().jobs;
    expect(bad.state).toBe("error");
    expect(bad.error?.code).toBe("corrupt");
    expect(bad.error?.params).toEqual({ x: 1 });
    expect(good.state).toBe("done");
    expect(s.getSnapshot().results).toHaveLength(1);
  });

  it("refuses inputs above the converter's maxBytes before running anything", async () => {
    const runner = vi.fn<JobRunner>(async () => [png("x.png")]);
    const s = mkSession(runner, fakeConverter({ maxBytes: () => 2 }));
    s.submit({ operation: "convert:png", files: [new File(["12345"], "big.png")], options: {}, multi: false });
    await s.idle();
    expect(runner).not.toHaveBeenCalled();
    expect(s.getSnapshot().jobs[0].error?.code).toBe("tooLarge");
  });

  it("an empty output is an error, an unsupported file is an error, never silent success", async () => {
    const s = mkSession(async () => []);
    s.submit({ operation: "convert:png", files: [png("a.png")], options: {}, multi: false });
    await s.idle();
    expect(s.getSnapshot().jobs[0].error?.code).toBe("emptyResult");
    const none = new Session({ registry: registryOf(undefined), runner: async () => [png("x.png")], createUrl: () => "blob:x", revokeUrl: () => {} });
    none.submit({ operation: "convert:png", files: [png("a.png")], options: {}, multi: false });
    expect(none.getSnapshot().jobs[0].error?.code).toBe("unsupported");
  });

  it("cancel stops a running job and drops its result", async () => {
    let started!: () => void;
    const go = new Promise<void>((r) => (started = r));
    const s = mkSession((_c, _job, _p, signal) =>
      new Promise<File[]>((resolve, reject) => {
        started();
        signal.addEventListener("abort", () => reject(new DOMException("Cancelled", "AbortError")));
      }),
    );
    const [id] = s.submit({ operation: "convert:png", files: [png("a.png")], options: {}, multi: false });
    await go;
    s.cancel(id);
    await s.idle();
    expect(s.getSnapshot().jobs[0].state).toBe("cancelled");
    expect(s.getSnapshot().results).toEqual([]);
  });

  it("subscribers get a NEW snapshot object on every change (useSyncExternalStore contract)", async () => {
    const s = mkSession(async () => [png("o.png")]);
    const seen = new Set<unknown>();
    s.subscribe(() => seen.add(s.getSnapshot()));
    const before = s.getSnapshot();
    s.submit({ operation: "convert:png", files: [png("a.png")], options: {}, multi: false });
    await s.idle();
    expect(s.getSnapshot()).not.toBe(before);
    expect(seen.size).toBeGreaterThan(1);
    expect(s.getSnapshot()).toBe(s.getSnapshot());
  });

  it("removing / clearing results revokes their URLs", async () => {
    const revoked: string[] = [];
    const s = new Session({
      registry: registryOf(fakeConverter()),
      runner: async () => [png("o.png")],
      createUrl: () => "blob:x",
      revokeUrl: (u) => revoked.push(u),
    });
    s.submit({ operation: "convert:png", files: [png("a.png")], options: {}, multi: false });
    await s.idle();
    s.clearResults();
    expect(revoked).toEqual(["blob:x"]);
    expect(s.getSnapshot().results).toEqual([]);
  });

  it("flags text results for 'copy as text'", () => {
    expect(isTextResult(new File(["a"], "a.txt", { type: "text/plain;charset=utf-8" }))).toBe(true);
    expect(isTextResult(new File(["a"], "a.png", { type: "image/png" }))).toBe(false);
  });
});

describe("runner", () => {
  it("routes by runtime: main converters run in-place, worker converters go to the pool, any main file forces main", async () => {
    const pool = { run: vi.fn(async () => [new File(["w"], "w.png")]) };
    const runner = createRunner(pool);
    const main = fakeConverter({ runtime: () => "main", run: vi.fn(async () => [new File(["m"], "m.png")]) });
    const worker = fakeConverter({ runtime: (_op, ext) => (ext === "svg" ? "main" : "worker"), run: vi.fn(async () => [new File(["m2"], "m2.png")]) });
    const job = (name: string, ...more: string[]) => ({ id: "j", operation: "convert:png", files: [name, ...more].map((n) => new File(["x"], n)), options: {} });

    const ac = new AbortController();
    expect((await runner(main, job("a.mp4"), () => {}, ac.signal))[0].name).toBe("m.png");
    expect(pool.run).not.toHaveBeenCalled();
    expect((await runner(worker, job("a.png"), () => {}, ac.signal))[0].name).toBe("w.png");
    expect(pool.run).toHaveBeenCalledTimes(1);
    expect(runtimeFor(worker, job("a.png", "b.svg"))).toBe("main");
    await runner(worker, job("a.png", "b.svg"), () => {}, ac.signal);
    expect(pool.run).toHaveBeenCalledTimes(1);
  });
});
