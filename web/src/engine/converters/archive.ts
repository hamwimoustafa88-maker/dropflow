// Archives: create a ZIP from any files (arc.zip) and extract zip / tar / gz / tgz (arc.extract).
// 7z and RAR are deliberately NOT claimed: there is no license-clean in-browser reader for them in this build, so the
// wheel never offers them (the Windows app does). Light manifest: fflate loads lazily from ./archive.impl.

import type { Converter, Job, ProgressFn } from "../types";
import { normalizeExt } from "../names";

const EXTRACTABLE: readonly string[] = ["zip", "tar", "gz", "tgz"];
const GIB = 1024 * 1024 * 1024;

export const archiveConverter: Converter = {
  id: "archive",
  operations: ["arc.zip", "arc.extract"],
  supports(operation, ext) {
    if (operation === "arc.zip") return true; // any file, with or without an extension
    if (operation === "arc.extract") return EXTRACTABLE.includes(normalizeExt(ext));
    return false;
  },
  options: () => [],
  runtime: () => "worker",
  suffix: (operation) => (operation === "arc.extract" ? "extracted" : "archive"),
  maxBytes: (operation) => (operation === "arc.extract" ? GIB : 2 * GIB),
  keepOutputNames: (operation) => operation === "arc.extract",
  async run(job: Job, onProgress: ProgressFn, signal: AbortSignal) {
    const impl = await import("./archive.impl");
    return impl.runArchive(job, onProgress, signal);
  },
};
