// The web "converter" contract. It mirrors the desktop IConverter (src/DropFlow.Core/Operations/Operations.cs):
// an operation id is either `convert:<ext>` or a tool id from catalog/tools.json (e.g. "img.compress").

export type OptionValue = string | number | boolean;
export type OptionValues = Record<string, OptionValue>;

/** One input a tool needs before it can run. The UI renders a generic panel from these (like desktop ToolOption). */
export type ToolOption =
  | { key: string; kind: "choice"; default: string; choices: readonly string[] }
  | { key: string; kind: "number"; default: number; min?: number; max?: number; step?: number }
  | { key: string; kind: "text"; default: string; required?: boolean; placeholder?: string }
  | { key: string; kind: "bool"; default: boolean }
  | { key: string; kind: "password"; default: string; required?: boolean }
  | { key: string; kind: "range"; default: number; min: number; max: number; step?: number };

export interface Job {
  id: string;
  operation: string;
  /** Always one file, except for tools marked `multi: true` in the catalog (merge, to-pdf, zip...). */
  files: File[];
  options: OptionValues;
}

/** fraction is 0..1; message is an optional short English log line (never shown as UI copy). */
export type ProgressFn = (fraction: number, message?: string) => void;

/**
 * Expected failures (corrupt file, wrong password, unsupported input...) are thrown as ConvertError with a stable
 * `code`; the UI translates `app.err.<code>` (params interpolated). Anything else is treated as a bug.
 */
export class ConvertError extends Error {
  readonly code: string;
  readonly params: Record<string, string | number>;
  constructor(code: string, params: Record<string, string | number> = {}, detail?: string) {
    super(detail ? `${code}: ${detail}` : code);
    this.name = "ConvertError";
    this.code = code;
    this.params = params;
  }
}

export function throwIfAborted(signal: AbortSignal): void {
  if (signal.aborted) throw new DOMException("Cancelled", "AbortError");
}

export function isAbort(e: unknown): boolean {
  return (e instanceof DOMException && e.name === "AbortError") || (e instanceof Error && e.name === "AbortError");
}

export type Runtime = "worker" | "main";

export interface Converter {
  /** Stable id of the converter module, e.g. "images". */
  readonly id: string;
  /** Operation ids this converter implements: "convert:png", "img.compress"... */
  readonly operations: readonly string[];
  /**
   * True ONLY when this converter really handles `operation` for an input with this extension (lower-case, no dot).
   * Never true for converting a format to itself. Must be cheap and synchronous (the wheel calls it often);
   * capability-dependent operations (avif, ffmpeg targets) read `getImageCaps()` / `getMediaCaps()`.
   */
  supports(operation: string, ext: string): boolean;
  /** Options to ask the user for. Empty array means "run immediately with defaults". */
  options(operation: string): readonly ToolOption[];
  /** "worker": heavy pure work (runs in the DropFlow Web Worker). "main": needs the DOM/canvas, or drives its own worker (ffmpeg, pdf.js). */
  runtime(operation: string, ext: string): Runtime;
  /** The word inside the output name: `name (suffix).ext`, e.g. "converted", "compressed", "merged". English, ASCII, lower-case. */
  suffix(operation: string): string;
  /** Optional i18n key under `app.warn.*` for a short warning shown in the options panel / before running (e.g. "lossy"). */
  warning?(operation: string): string | undefined;
  /** Called (main thread) when the person runs the operation after seeing its warning, e.g. to show a first-use note once. */
  acknowledgeWarning?(operation: string): void;
  /** Optional hard per-file input limit (bytes). Beyond it the UI refuses with the "tooLarge" error before running. */
  maxBytes?(operation: string): number | undefined;
  /** When true the output files keep their own (sanitised) names instead of `name (suffix).ext` — used by archive extraction. */
  keepOutputNames?(operation: string): boolean;
  /**
   * Runs the operation. Must honour `signal` (check between steps; throw AbortError), report progress, never mutate
   * its inputs, and return NEW files. Each File's `name` is provisional: its extension is used, and (for multi-output
   * jobs) its stem is kept as a part label. Set `File.type` correctly — text results use "text/plain;charset=utf-8".
   */
  run(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]>;
}
