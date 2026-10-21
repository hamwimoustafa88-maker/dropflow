// Web Worker pool for the "worker" runtime. Each worker runs ONE job at a time; the pool grows lazily up to `maxSize`
// and later requests wait their turn. Cancellation is cooperative first (a `cancel` message aborts the job's
// AbortSignal inside the worker) and hard second: if the worker has not settled within the grace period it is
// terminated and replaced, so even a long uninterruptible call (a huge pdf-lib save) can always be stopped.
//
// Only the protocol lives here (pure, testable with a fake Worker); the real Worker is created by ./worker-factory.

import { ConvertError, type OptionValues } from "./types";

export interface WorkerLike {
  postMessage(message: unknown): void;
  terminate(): void;
  onmessage: ((event: { data: unknown }) => void) | null;
  onerror: ((event: unknown) => void) | null;
}

export interface WorkRequest {
  id: string;
  converterId: string;
  operation: string;
  files: File[];
  options: OptionValues;
}

/** Messages worker -> main. */
export type WorkerReply =
  | { type: "progress"; id: string; fraction: number; message?: string }
  | { type: "done"; id: string; outputs: File[] }
  | { type: "error"; id: string; aborted: boolean; code?: string; params?: Record<string, string | number>; message: string };

/** Messages main -> worker. */
export type WorkerCommand = ({ type: "run" } & WorkRequest) | { type: "cancel"; id: string };

interface Active {
  id: string;
  handle: (reply: WorkerReply) => void;
  fail: (error: unknown) => void;
}

interface Slot {
  worker: WorkerLike;
  busy: boolean;
  active: Active | null;
}

interface Waiter {
  grant: (slot: Slot) => void;
}

function abortError(): DOMException {
  return new DOMException("Cancelled", "AbortError");
}

export class WorkerPool {
  private readonly slots: Slot[] = [];
  private readonly waiters: Waiter[] = [];

  constructor(
    private readonly factory: () => WorkerLike,
    private readonly maxSize: number,
    private readonly cancelGraceMs = 1500,
  ) {}

  get size(): number {
    return this.slots.length;
  }

  async run(request: WorkRequest, onProgress: (fraction: number, message?: string) => void, signal: AbortSignal): Promise<File[]> {
    if (signal.aborted) throw abortError();
    const slot = await this.acquire(signal);
    return new Promise<File[]>((resolve, reject) => {
      let finished = false;
      let timer: ReturnType<typeof setTimeout> | undefined;
      const finish = (fn: () => void) => {
        if (finished) return;
        finished = true;
        if (timer) clearTimeout(timer);
        signal.removeEventListener("abort", onAbort);
        this.release(slot);
        fn();
      };
      const onAbort = () => {
        try {
          slot.worker.postMessage({ type: "cancel", id: request.id } satisfies WorkerCommand);
        } catch {
          /* worker already gone */
        }
        timer = setTimeout(() => {
          // Hard cancel: the job did not stop by itself, so kill the whole worker and start a fresh one next time.
          this.discard(slot);
          finish(() => reject(abortError()));
        }, this.cancelGraceMs);
      };
      slot.active = {
        id: request.id,
        handle: (reply) => {
          if (reply.type === "progress") {
            onProgress(reply.fraction, reply.message);
          } else if (reply.type === "done") {
            finish(() => resolve(reply.outputs));
          } else {
            finish(() => {
              if (reply.aborted) reject(abortError());
              else if (reply.code) reject(new ConvertError(reply.code, reply.params ?? {}, reply.message));
              else reject(new Error(reply.message));
            });
          }
        },
        fail: (error) => {
          this.discard(slot);
          finish(() => reject(error));
        },
      };
      signal.addEventListener("abort", onAbort, { once: true });
      try {
        slot.worker.postMessage({ type: "run", ...request } satisfies WorkerCommand);
      } catch (e) {
        this.discard(slot);
        finish(() => reject(e));
      }
    });
  }

  /** Terminates every worker (used when the page is torn down). */
  dispose(): void {
    this.waiters.length = 0;
    for (const slot of [...this.slots]) this.discard(slot);
  }

  private acquire(signal: AbortSignal): Promise<Slot> {
    const idle = this.slots.find((s) => !s.busy);
    if (idle) {
      idle.busy = true;
      return Promise.resolve(idle);
    }
    if (this.slots.length < this.maxSize) {
      const slot = this.spawn();
      slot.busy = true;
      return Promise.resolve(slot);
    }
    return new Promise<Slot>((resolve, reject) => {
      const waiter: Waiter = {
        grant: (slot) => {
          signal.removeEventListener("abort", onAbort);
          resolve(slot);
        },
      };
      const onAbort = () => {
        const i = this.waiters.indexOf(waiter);
        if (i >= 0) this.waiters.splice(i, 1);
        reject(abortError());
      };
      signal.addEventListener("abort", onAbort, { once: true });
      this.waiters.push(waiter);
    });
  }

  private release(slot: Slot): void {
    slot.active = null;
    slot.busy = false;
    if (!this.slots.includes(slot)) return; // it was discarded: discard() already served the next waiter
    const next = this.waiters.shift();
    if (next) {
      slot.busy = true;
      next.grant(slot);
    }
  }

  private spawn(): Slot {
    const worker = this.factory();
    const slot: Slot = { worker, busy: false, active: null };
    worker.onmessage = (event) => {
      const reply = event.data as WorkerReply | null;
      if (!reply || typeof reply !== "object" || !("type" in reply)) return;
      if (slot.active && reply.id === slot.active.id) slot.active.handle(reply);
    };
    worker.onerror = (event) => {
      const detail = event && typeof event === "object" && "message" in event ? String((event as { message: unknown }).message) : "worker error";
      if (slot.active) slot.active.fail(new ConvertError("workerFailed", {}, detail));
      else this.discard(slot);
    };
    this.slots.push(slot);
    return slot;
  }

  private discard(slot: Slot): void {
    const i = this.slots.indexOf(slot);
    if (i < 0) return;
    this.slots.splice(i, 1);
    slot.busy = false;
    try {
      slot.worker.terminate();
    } catch {
      /* ignore */
    }
    const next = this.waiters.shift();
    if (next) {
      const fresh = this.spawn();
      fresh.busy = true;
      next.grant(fresh);
    }
  }
}
