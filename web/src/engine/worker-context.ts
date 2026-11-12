// What a Web Worker must know about the page before running a job. A worker has its OWN module state, so capabilities
// the page detected at runtime (which canvas encoders really work...) would otherwise stay at their conservative
// defaults inside the worker: "Compress" on a WebP then re-encoded to JPEG (no WebP encoder "known"), and
// resize/crop/edit wrote PNG instead of WebP. The pool sends the page's caps with every job; the worker adopts them.

import { setImageCaps } from "./caps";
import type { WorkerCommand } from "./pool";

export function applyRunCommand(msg: WorkerCommand): void {
  if (msg.type === "run" && msg.imageCaps) setImageCaps(msg.imageCaps);
}
