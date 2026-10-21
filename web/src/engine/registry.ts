// The converter registry: maps (operation, input extension) to the converter that REALLY implements it, mirroring the
// desktop ToolRegistry. The wheel shows exactly the operations that have a converter here - nothing aspirational.
//
// Every converter module is a light manifest (heavy engines load on first run), so importing this file is cheap and it
// is safe in the UI bundle, in the Web Worker and in Node tests.

import { aiConverter } from "./converters/ai";
import { archiveConverter } from "./converters/archive";
import { generalConverter } from "./converters/general";
import { imagesConverter } from "./converters/images";
import { mediaConverter } from "./converters/media";
import { pdfConverter } from "./converters/pdf";
import { photoConverter } from "./converters/photo";
import { subtitlesConverter } from "./converters/subtitles";
import { normalizeExt } from "./names";
import type { Converter } from "./types";

export class Registry {
  readonly converters: readonly Converter[];

  constructor(converters: readonly Converter[]) {
    this.converters = converters;
  }

  /** The converter that handles `operation` for an input with this extension, if any. */
  find(operation: string, ext: string): Converter | undefined {
    const e = normalizeExt(ext);
    return this.converters.find((c) => c.supports(operation, e));
  }

  byId(id: string): Converter | undefined {
    return this.converters.find((c) => c.id === id);
  }

  /** Operation ids that some converter declares (regardless of runtime capabilities). */
  get implementedOperations(): ReadonlySet<string> {
    return new Set(this.converters.flatMap((c) => c.operations));
  }
}

export const registry = new Registry([
  imagesConverter,
  photoConverter,
  aiConverter,
  pdfConverter,
  mediaConverter,
  subtitlesConverter,
  archiveConverter,
  generalConverter,
]);
