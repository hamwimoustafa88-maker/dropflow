// General tools that work on ANY file: gen.hash (MD5 + SHA family) and gen.base64. Light manifest: the work loads
// lazily from ./general.impl.

import type { Converter, Job, ProgressFn, ToolOption } from "../types";

const MIB = 1024 * 1024;

const HASH_OPTIONS: readonly ToolOption[] = [
  { key: "algorithm", kind: "choice", default: "sha-256", choices: ["sha-256", "sha-1", "sha-384", "sha-512", "md5", "all"] },
];

const BASE64_OPTIONS: readonly ToolOption[] = [
  { key: "mode", kind: "choice", default: "encode", choices: ["encode", "decode"] },
  { key: "urlSafe", kind: "bool", default: false },
];

export const generalConverter: Converter = {
  id: "general",
  operations: ["gen.hash", "gen.base64"],
  supports(operation) {
    // Any extension, including none.
    return operation === "gen.hash" || operation === "gen.base64";
  },
  options(operation) {
    if (operation === "gen.hash") return HASH_OPTIONS;
    if (operation === "gen.base64") return BASE64_OPTIONS;
    return [];
  },
  runtime: () => "worker",
  suffix: (operation) => (operation === "gen.hash" ? "hash" : "base64"),
  maxBytes: (operation) => (operation === "gen.hash" ? 1024 * MIB : 300 * MIB),
  async run(job: Job, onProgress: ProgressFn, signal: AbortSignal) {
    const impl = await import("./general.impl");
    return impl.runGeneral(job, onProgress, signal);
  },
};
