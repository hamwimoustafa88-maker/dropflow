// Creates the real DropFlow Web Worker. Kept in its own file so only code that actually needs a worker (the /app
// route) pulls the worker bundle in; Next/Turbopack code-splits `new Worker(new URL(...))` into a separate chunk.

import { WorkerPool, type WorkerLike } from "./pool";

export function createWorker(): WorkerLike {
  return new Worker(new URL("./worker.ts", import.meta.url), { type: "module", name: "dropflow" }) as unknown as WorkerLike;
}

/** Pool size: leave a core for the UI and for ffmpeg's own worker, but allow at least 1 and at most 3 parallel jobs. */
export function defaultPoolSize(): number {
  const cores = typeof navigator !== "undefined" && navigator.hardwareConcurrency ? navigator.hardwareConcurrency : 2;
  return Math.min(3, Math.max(1, cores - 1));
}

export function createPool(): WorkerPool {
  return new WorkerPool(createWorker, defaultPoolSize());
}
