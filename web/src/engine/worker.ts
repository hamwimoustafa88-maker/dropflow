// DropFlow Web Worker entry. It receives one job at a time from the WorkerPool, looks the converter up BY ID (the
// worker has its own, default capability state, so it must not re-run `supports`), runs it and posts the outputs
// back. Nothing here can reach the network: converters never call fetch with data, and the page CSP forbids it.

import { registry } from "./registry";
import { ConvertError, isAbort, type Job } from "./types";
import type { WorkerCommand, WorkerReply } from "./pool";

const controllers = new Map<string, AbortController>();

function post(reply: WorkerReply): void {
  (self as unknown as { postMessage(message: unknown): void }).postMessage(reply);
}

self.onmessage = async (event: MessageEvent<WorkerCommand>) => {
  const msg = event.data;
  if (msg.type === "cancel") {
    controllers.get(msg.id)?.abort();
    return;
  }
  if (msg.type !== "run") return;

  const controller = new AbortController();
  controllers.set(msg.id, controller);
  try {
    const converter = registry.byId(msg.converterId);
    if (!converter) throw new ConvertError("unsupported", {}, `unknown converter ${msg.converterId}`);
    const job: Job = { id: msg.id, operation: msg.operation, files: msg.files, options: msg.options };
    const outputs = await converter.run(
      job,
      (fraction, message) => post({ type: "progress", id: msg.id, fraction, message }),
      controller.signal,
    );
    post({ type: "done", id: msg.id, outputs });
  } catch (error) {
    if (isAbort(error) || controller.signal.aborted) {
      post({ type: "error", id: msg.id, aborted: true, message: "Cancelled" });
    } else if (error instanceof ConvertError) {
      post({ type: "error", id: msg.id, aborted: false, code: error.code, params: error.params, message: error.message });
    } else {
      post({ type: "error", id: msg.id, aborted: false, message: error instanceof Error ? error.message : String(error) });
    }
  } finally {
    controllers.delete(msg.id);
  }
};
