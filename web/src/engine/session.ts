// A Session owns the job queue and the list of results for one visit of /app. It is plain TypeScript (no React, no
// DOM besides optional object URLs) so the whole run/cancel/naming logic is unit-tested with a fake runner. The UI
// subscribes with useSyncExternalStore.
//
// Rules enforced here (not in the converters): bounded concurrency, hard `maxBytes` guard, results are NEW files named
// `name (suffix).ext` and made unique among all results - an input is never overwritten or replaced.

import { extensionOf, extOf, keptName, outputName } from "./names";
import { JobQueue, type TaskState } from "./queue";
import type { Registry } from "./registry";
import { ConvertError, isAbort, type Converter, type Job, type OptionValues, type ProgressFn } from "./types";
import { entryBaseName, entryPathFromFileName } from "./lib/zip-safe";

export type JobRunner = (converter: Converter, job: Job, onProgress: ProgressFn, signal: AbortSignal) => Promise<File[]>;

export interface JobError {
  code?: string;
  params?: Record<string, string | number>;
  message: string;
}

export interface JobView {
  id: string;
  operation: string;
  inputNames: string[];
  state: TaskState;
  progress: number;
  message?: string;
  error?: JobError;
  outputCount: number;
}

export interface ResultFile {
  id: string;
  jobId: string;
  /** Display / download name (base name). */
  name: string;
  /** Path inside "download all" (equals name unless the converter keeps folders, e.g. archive extraction). */
  path: string;
  file: File;
  size: number;
  url: string;
  isText: boolean;
  isImage: boolean;
  sourceName: string;
}

export interface SessionSnapshot {
  jobs: readonly JobView[];
  results: readonly ResultFile[];
}

export interface SubmitInput {
  operation: string;
  files: readonly File[];
  options: OptionValues;
  /** Catalog `multi`: one job with all files instead of one job per file. */
  multi: boolean;
}

export interface SessionDeps {
  registry: Pick<Registry, "find">;
  runner: JobRunner;
  concurrency?: number;
  createUrl?: (blob: Blob) => string;
  revokeUrl?: (url: string) => void;
}

const TEXT_LIMIT = 2 * 1024 * 1024;

export function isTextResult(file: Blob): boolean {
  return file.type.startsWith("text/") && file.size <= TEXT_LIMIT;
}

export function toJobError(error: unknown): JobError {
  if (error instanceof ConvertError) return { code: error.code, params: error.params, message: error.message };
  return { code: "unknown", message: error instanceof Error ? error.message : String(error) };
}

export class Session {
  private jobs: JobView[] = [];
  private results: ResultFile[] = [];
  private readonly queue: JobQueue<File[]>;
  private readonly meta = new Map<string, { converter: Converter; operation: string; inputs: readonly File[] }>();
  private readonly listeners = new Set<() => void>();
  private snapshot: SessionSnapshot = { jobs: [], results: [] };
  private seq = 0;
  private readonly createUrl: (blob: Blob) => string;
  private readonly revokeUrl: (url: string) => void;

  constructor(private readonly deps: SessionDeps) {
    this.createUrl = deps.createUrl ?? ((b) => URL.createObjectURL(b));
    this.revokeUrl = deps.revokeUrl ?? ((u) => URL.revokeObjectURL(u));
    this.queue = new JobQueue<File[]>(deps.concurrency ?? 2);
    this.queue.subscribe((snap) => this.onTask(snap.id, snap.state, snap.progress, snap.message, snap.result, snap.error));
  }

  subscribe = (fn: () => void): (() => void) => {
    this.listeners.add(fn);
    return () => {
      this.listeners.delete(fn);
    };
  };

  getSnapshot = (): SessionSnapshot => this.snapshot;

  /** Queues the work. Returns the job ids (one per file, or a single one for multi tools). */
  submit(input: SubmitInput): string[] {
    const groups: (readonly File[])[] = input.multi ? [input.files] : input.files.map((f) => [f]);
    const ids: string[] = [];
    for (const files of groups) {
      const converter = this.deps.registry.find(input.operation, extensionOf(files[0].name));
      const id = `job-${++this.seq}`;
      ids.push(id);
      const view: JobView = {
        id,
        operation: input.operation,
        inputNames: files.map((f) => f.name),
        state: "waiting",
        progress: 0,
        outputCount: 0,
      };
      this.jobs = [...this.jobs, view];
      if (!converter) {
        this.jobs = this.jobs.map((j) => (j.id === id ? { ...j, state: "error", error: { code: "unsupported", message: "no converter" } } : j));
        continue;
      }
      this.meta.set(id, { converter, operation: input.operation, inputs: files });
      const job: Job = { id, operation: input.operation, files: [...files], options: input.options };
      this.queue.add({
        id,
        run: async (report, signal) => {
          const max = converter.maxBytes?.(input.operation);
          if (max !== undefined && files.some((f) => f.size > max)) throw new ConvertError("tooLarge", { max });
          return this.deps.runner(converter, job, report, signal);
        },
      });
    }
    this.publish();
    return ids;
  }

  cancel(jobId: string): void {
    this.queue.cancel(jobId);
  }

  cancelAll(): void {
    this.queue.cancelAll();
  }

  /** Removes finished/failed/cancelled job rows (running and waiting ones stay). */
  clearFinishedJobs(): void {
    this.jobs = this.jobs.filter((j) => j.state === "waiting" || j.state === "running");
    this.publish();
  }

  /** Dismisses one finished/failed/cancelled job row. */
  removeJob(id: string): void {
    this.jobs = this.jobs.filter((j) => j.id !== id || j.state === "waiting" || j.state === "running");
    this.publish();
  }

  removeResult(id: string): void {
    const r = this.results.find((x) => x.id === id);
    if (r) this.revokeUrl(r.url);
    this.results = this.results.filter((x) => x.id !== id);
    this.publish();
  }

  clearResults(): void {
    this.results.forEach((r) => this.revokeUrl(r.url));
    this.results = [];
    this.publish();
  }

  /** Cancels everything and drops all state. */
  reset(): void {
    this.queue.cancelAll();
    this.clearResults();
    this.jobs = [];
    this.publish();
  }

  idle(): Promise<void> {
    return this.queue.idle();
  }

  private onTask(id: string, state: TaskState, progress: number, message: string | undefined, result: File[] | undefined, error: unknown): void {
    const view = this.jobs.find((j) => j.id === id);
    if (!view) return;
    let next: JobView = { ...view, state, progress, message };
    if (state === "done") {
      const meta = this.meta.get(id);
      const outputs = result ?? [];
      if (!meta || outputs.length === 0) {
        next = { ...next, state: "error", error: { code: "emptyResult", message: "no output" } };
      } else {
        this.addResults(id, meta, outputs);
        next = { ...next, outputCount: outputs.length };
      }
    } else if (state === "error") {
      next = { ...next, error: isAbort(error) ? undefined : toJobError(error) };
      if (!isAbort(error) && !(error instanceof ConvertError)) console.warn("DropFlow job failed", error);
    }
    if (state === "done" || state === "error" || state === "cancelled") this.meta.delete(id);
    this.jobs = this.jobs.map((j) => (j.id === id ? next : j));
    this.publish();
  }

  private addResults(jobId: string, meta: { converter: Converter; operation: string; inputs: readonly File[] }, outputs: File[]): void {
    const taken = new Set(this.results.map((r) => r.path.toLowerCase()));
    const keep = meta.converter.keepOutputNames?.(meta.operation) === true;
    const suffix = meta.converter.suffix(meta.operation);
    const first = meta.inputs[0].name;
    outputs.forEach((file, i) => {
      let path: string;
      if (keep) {
        path = keptName(entryPathFromFileName(file.name), { taken });
      } else {
        path = outputName(first, suffix, extOf(file.name), i, outputs.length, { taken });
      }
      taken.add(path.toLowerCase());
      const name = entryBaseName(path);
      const named = new File([file], name, { type: file.type, lastModified: Date.now() });
      this.results = [
        ...this.results,
        {
          id: `${jobId}-r${i}`,
          jobId,
          name,
          path,
          file: named,
          size: named.size,
          url: this.createUrl(named),
          isText: isTextResult(named),
          isImage: /^image\/(png|jpeg|webp|gif|avif|bmp|x-icon|svg\+xml)$/.test(file.type),
          sourceName: first,
        },
      ];
    });
  }

  private publish(): void {
    this.snapshot = { jobs: this.jobs, results: this.results };
    this.listeners.forEach((l) => l());
  }
}
