// Job queue with BOUNDED concurrency, cancellation and progress (pure, no DOM). Mirrors the desktop JobQueue:
// at most `concurrency` tasks run at once, the rest wait in FIFO order; cancelling a waiting task means it never
// starts, cancelling a running task aborts its signal. One failing task never affects the others.

export type TaskState = "waiting" | "running" | "done" | "error" | "cancelled";

export interface Task<R> {
  id: string;
  /** `report` takes a fraction 0..1. The promise should reject with an AbortError when `signal` aborts. */
  run(report: (fraction: number, message?: string) => void, signal: AbortSignal): Promise<R>;
}

export interface TaskSnapshot<R> {
  id: string;
  state: TaskState;
  progress: number;
  message?: string;
  result?: R;
  error?: unknown;
}

interface Entry<R> {
  task: Task<R>;
  snap: TaskSnapshot<R>;
  controller: AbortController;
}

export class JobQueue<R> {
  private readonly entries = new Map<string, Entry<R>>();
  private readonly pending: Entry<R>[] = [];
  private running = 0;
  private readonly listeners = new Set<(s: TaskSnapshot<R>) => void>();
  private idleWaiters: Array<() => void> = [];

  constructor(private concurrency: number) {
    if (!(concurrency >= 1)) throw new RangeError("concurrency must be >= 1");
  }

  setConcurrency(n: number): void {
    this.concurrency = Math.max(1, Math.floor(n));
    this.pump();
  }

  subscribe(fn: (s: TaskSnapshot<R>) => void): () => void {
    this.listeners.add(fn);
    return () => {
      this.listeners.delete(fn);
    };
  }

  add(task: Task<R>): void {
    if (this.entries.has(task.id)) throw new Error(`duplicate task id ${task.id}`);
    const entry: Entry<R> = { task, snap: { id: task.id, state: "waiting", progress: 0 }, controller: new AbortController() };
    this.entries.set(task.id, entry);
    this.pending.push(entry);
    this.emit(entry);
    this.pump();
  }

  get(id: string): TaskSnapshot<R> | undefined {
    return this.entries.get(id)?.snap;
  }

  get runningCount(): number {
    return this.running;
  }

  get waitingCount(): number {
    return this.pending.length;
  }

  cancel(id: string): void {
    const entry = this.entries.get(id);
    if (!entry) return;
    if (entry.snap.state === "waiting") {
      const i = this.pending.indexOf(entry);
      if (i >= 0) this.pending.splice(i, 1);
      this.set(entry, { state: "cancelled" });
      this.checkIdle();
    } else if (entry.snap.state === "running") {
      this.set(entry, { state: "cancelled" });
      entry.controller.abort();
    }
  }

  cancelAll(): void {
    for (const id of [...this.entries.keys()]) this.cancel(id);
  }

  /** Resolves when nothing is waiting or running. */
  idle(): Promise<void> {
    if (this.running === 0 && this.pending.length === 0) return Promise.resolve();
    return new Promise((resolve) => this.idleWaiters.push(resolve));
  }

  private pump(): void {
    while (this.running < this.concurrency && this.pending.length > 0) {
      const entry = this.pending.shift()!;
      if (entry.snap.state !== "waiting") continue;
      this.running++;
      this.set(entry, { state: "running" });
      void this.start(entry);
    }
    this.checkIdle();
  }

  private async start(entry: Entry<R>): Promise<void> {
    try {
      const result = await entry.task.run((fraction, message) => {
        if (entry.snap.state !== "running") return;
        const p = Number.isFinite(fraction) ? Math.min(1, Math.max(0, fraction)) : entry.snap.progress;
        this.set(entry, { progress: Math.max(entry.snap.progress, p), message });
      }, entry.controller.signal);
      if (entry.snap.state === "running") this.set(entry, { state: "done", progress: 1, result });
    } catch (error) {
      if (entry.snap.state === "running") {
        this.set(entry, { state: entry.controller.signal.aborted ? "cancelled" : "error", error });
      }
    } finally {
      this.running--;
      this.pump();
    }
  }

  private set(entry: Entry<R>, patch: Partial<TaskSnapshot<R>>): void {
    entry.snap = { ...entry.snap, ...patch };
    this.emit(entry);
  }

  private emit(entry: Entry<R>): void {
    for (const l of this.listeners) l(entry.snap);
  }

  private checkIdle(): void {
    if (this.running === 0 && this.pending.length === 0 && this.idleWaiters.length > 0) {
      const waiters = this.idleWaiters;
      this.idleWaiters = [];
      waiters.forEach((w) => w());
    }
  }
}
