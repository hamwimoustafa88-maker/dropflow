// Decides WHERE a job runs: "worker" converters go to the Web Worker pool, "main" converters (canvas/DOM work, or
// engines that own a worker themselves such as pdf.js) run on the UI thread. A multi-file job runs on
// the main thread as soon as any of its files needs it.

import { extensionOf } from "./names";
import type { WorkerPool } from "./pool";
import type { JobRunner } from "./session";
import type { Converter, Job } from "./types";

export function runtimeFor(converter: Converter, job: Pick<Job, "operation" | "files">): "worker" | "main" {
  return job.files.some((f) => converter.runtime(job.operation, extensionOf(f.name)) === "main") ? "main" : "worker";
}

export function createRunner(pool: Pick<WorkerPool, "run">): JobRunner {
  return (converter, job, onProgress, signal) => {
    if (runtimeFor(converter, job) === "main") return converter.run(job, onProgress, signal);
    return pool.run(
      { id: job.id, converterId: converter.id, operation: job.operation, files: job.files, options: job.options },
      onProgress,
      signal,
    );
  };
}
