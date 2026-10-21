// General tools that work on ANY file: gen.hash (MD5 + SHA family), gen.base64 and gen.rename (renamed COPIES of
// several files: a browser cannot rename files on disk). Light manifest: the work loads lazily from ./general.impl.

import type { Converter, Job, ProgressFn, ToolOption } from "../types";

const MIB = 1024 * 1024;

const HASH_OPTIONS: readonly ToolOption[] = [
  { key: "algorithm", kind: "choice", default: "sha-256", choices: ["sha-256", "sha-1", "sha-384", "sha-512", "md5", "all"] },
];

const BASE64_OPTIONS: readonly ToolOption[] = [
  { key: "mode", kind: "choice", default: "encode", choices: ["encode", "decode"] },
  { key: "urlSafe", kind: "bool", default: false },
];

const RENAME_OPTIONS: readonly ToolOption[] = [
  { key: "pattern", kind: "text", default: "{name}{ext}", required: true },
  { key: "find", kind: "text", default: "" },
  { key: "replace", kind: "text", default: "" },
  { key: "regex", kind: "bool", default: false },
  { key: "ignoreCase", kind: "bool", default: false },
  { key: "case", kind: "choice", default: "none", choices: ["none", "lower", "upper", "title", "sentence"] },
  { key: "trim", kind: "bool", default: false },
  { key: "start", kind: "number", default: 1, min: 0, max: 1_000_000, step: 1 },
];

const OPS = ["gen.hash", "gen.base64", "gen.rename"];

export const generalConverter: Converter = {
  id: "general",
  operations: OPS,
  supports(operation) {
    // Any extension, including none.
    return OPS.includes(operation);
  },
  options(operation) {
    if (operation === "gen.hash") return HASH_OPTIONS;
    if (operation === "gen.base64") return BASE64_OPTIONS;
    if (operation === "gen.rename") return RENAME_OPTIONS;
    return [];
  },
  runtime: () => "worker",
  suffix: (operation) => (operation === "gen.hash" ? "hash" : operation === "gen.rename" ? "renamed" : "base64"),
  keepOutputNames: (operation) => operation === "gen.rename",
  maxBytes: (operation) => (operation === "gen.hash" ? 1024 * MIB : operation === "gen.rename" ? 2048 * MIB : 300 * MIB),
  async run(job: Job, onProgress: ProgressFn, signal: AbortSignal) {
    const impl = await import("./general.impl");
    return impl.runGeneral(job, onProgress, signal);
  },
};
