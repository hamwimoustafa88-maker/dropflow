// Minimal main-thread client for the ffmpeg.wasm worker, speaking the SAME message protocol as the `FFmpeg` class of
// @ffmpeg/ffmpeg 0.12.x (`{ id, type, data }` requests, `{ id, type, data }` replies, `ERROR`, `LOG`, `PROGRESS`).
//
// Why not use the package's FFmpeg class? It creates its worker with `new Worker(new URL(classWorkerURL, import.meta.url))`,
// which Next 16 / Turbopack cannot resolve at build time ("Can't resolve <dynamic>"), and the package's own worker
// imports its core through a dynamic `import(url)` that bundlers rewrite. So we run the package's UNMODIFIED worker
// files, copied to /vendor/ffmpeg by scripts/vendor.mjs and loaded as plain same-origin URLs, and only replace the
// 100-line client. Worker + core come from the site's own origin; nothing is fetched from a CDN.

type Handler = { resolve: (data: unknown) => void; reject: (reason: unknown) => void };
type LogEvent = { type: string; message: string };
type ProgressEvent = { progress: number; time: number };

export interface FFmpegLoadConfig {
  /** Absolute URL of the vendored worker (public/vendor/ffmpeg/worker.js). */
  classWorkerURL: string;
  /** Absolute URL of ffmpeg-core.js (ES module build). */
  coreURL: string;
  /** Absolute URL of ffmpeg-core.wasm. */
  wasmURL: string;
}

/** The few things of `Worker` we use, so tests can substitute a fake. */
export interface WorkerPort {
  postMessage(message: unknown, transfer?: Transferable[]): void;
  terminate(): void;
  onmessage: ((event: { data: { id?: number; type: string; data: unknown } }) => void) | null;
  onerror: ((event: unknown) => void) | null;
}

export const ERROR_TERMINATED = "called FFmpeg.terminate()";
export const ERROR_NOT_LOADED = "ffmpeg is not loaded, call `await ffmpeg.load()` first";

let nextId = 0;

export class FFmpeg {
  loaded = false;
  private worker: WorkerPort | null = null;
  private readonly handlers = new Map<number, Handler>();
  private logListeners: Array<(e: LogEvent) => void> = [];
  private progressListeners: Array<(e: ProgressEvent) => void> = [];

  constructor(private readonly createWorker: (url: string) => WorkerPort = (url) => new Worker(url, { type: "module" }) as unknown as WorkerPort) {}

  on(event: "log", cb: (e: LogEvent) => void): void;
  on(event: "progress", cb: (e: ProgressEvent) => void): void;
  on(event: "log" | "progress", cb: ((e: LogEvent) => void) & ((e: ProgressEvent) => void)): void {
    if (event === "log") this.logListeners.push(cb);
    else this.progressListeners.push(cb);
  }

  off(event: "log", cb: (e: LogEvent) => void): void;
  off(event: "progress", cb: (e: ProgressEvent) => void): void;
  off(event: "log" | "progress", cb: ((e: LogEvent) => void) & ((e: ProgressEvent) => void)): void {
    if (event === "log") this.logListeners = this.logListeners.filter((f) => f !== cb);
    else this.progressListeners = this.progressListeners.filter((f) => f !== cb);
  }

  async load(config: FFmpegLoadConfig): Promise<boolean> {
    if (!this.worker) {
      const worker = this.createWorker(config.classWorkerURL);
      this.worker = worker;
      worker.onmessage = ({ data: { id, type, data } }) => {
        if (type === "LOG") {
          this.logListeners.forEach((f) => f(data as LogEvent));
          return;
        }
        if (type === "PROGRESS") {
          this.progressListeners.forEach((f) => f(data as ProgressEvent));
          return;
        }
        if (id === undefined) return;
        const handler = this.handlers.get(id);
        if (!handler) return;
        this.handlers.delete(id);
        if (type === "ERROR") handler.reject(new Error(String(data)));
        else handler.resolve(data);
      };
      worker.onerror = (event) => {
        const message = event && typeof event === "object" && "message" in event ? String((event as { message: unknown }).message) : "ffmpeg worker error";
        this.failAll(new Error(message));
      };
    }
    const first = (await this.send("LOAD", { coreURL: config.coreURL, wasmURL: config.wasmURL })) as boolean;
    this.loaded = true;
    return first;
  }

  exec(args: string[], timeout = -1): Promise<number> {
    return this.send("EXEC", { args, timeout }) as Promise<number>;
  }

  async writeFile(path: string, data: Uint8Array | string): Promise<boolean> {
    const transfer: Transferable[] = data instanceof Uint8Array ? [data.buffer as ArrayBuffer] : [];
    return (await this.send("WRITE_FILE", { path, data }, transfer)) as boolean;
  }

  async readFile(path: string): Promise<Uint8Array | string> {
    return (await this.send("READ_FILE", { path, encoding: "binary" })) as Uint8Array;
  }

  async deleteFile(path: string): Promise<boolean> {
    return (await this.send("DELETE_FILE", { path })) as boolean;
  }

  /** Rejects every pending call and kills the worker (the only way to cancel a running command). */
  terminate(): void {
    this.failAll(new Error(ERROR_TERMINATED));
    if (this.worker) {
      this.worker.terminate();
      this.worker = null;
    }
    this.loaded = false;
  }

  private failAll(reason: unknown): void {
    for (const h of this.handlers.values()) h.reject(reason);
    this.handlers.clear();
  }

  private send(type: string, data: unknown, transfer: Transferable[] = []): Promise<unknown> {
    const worker = this.worker;
    if (!worker) return Promise.reject(new Error(ERROR_NOT_LOADED));
    return new Promise((resolve, reject) => {
      const id = nextId++;
      this.handlers.set(id, { resolve, reject });
      try {
        worker.postMessage({ id, type, data }, transfer);
      } catch (e) {
        this.handlers.delete(id);
        reject(e);
      }
    });
  }
}
