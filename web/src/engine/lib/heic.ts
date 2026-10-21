// HEIC / HEIF input with libheif (+ libde265), compiled to WebAssembly by `libheif-js` (LGPL-3.0, used unmodified and
// loaded as a separate, replaceable file). Only Safari decodes HEIC natively, so DropFlow Web decodes it here, on the
// device. The 2 MB module comes from the site's own origin (/vendor/heif/libheif-bundle.mjs, copied by
// scripts/vendor.mjs) the first time a HEIC file is opened.

import { ConvertError } from "../types";
import type { Rgba } from "./tiff";
import { importVendor } from "./vendor-import";

export const HEIF_BASE = "/vendor/heif/";

interface HeifImage {
  get_width(): number;
  get_height(): number;
  display(target: { data: Uint8ClampedArray; width: number; height: number }, done: (result: { data: Uint8ClampedArray } | null) => void): void;
  free(): void;
}
interface HeifLib {
  HeifDecoder: new () => { decode(bytes: Uint8Array): HeifImage[] };
}
type HeifModuleArgs = {
  onRuntimeInitialized?: () => void;
  onAbort?: (reason: unknown) => void;
  print?: (s: string) => void;
  printErr?: (s: string) => void;
};
type HeifFactory = (args: HeifModuleArgs) => HeifLib;

let loading: Promise<HeifLib> | null = null;

export interface HeifLoadOptions {
  /** Tests: import the ES-module build from node_modules instead of /vendor. */
  importer?: () => Promise<{ default: unknown }>;
}

/**
 * Loads libheif once: the ES-module build of libheif-js (wasm inside), served from /vendor/heif and imported at runtime.
 * The module object IS the library (emscripten MODULARIZE); it is usable once the runtime has initialised.
 */
export function loadHeif(opts: HeifLoadOptions = {}): Promise<HeifLib> {
  if (!loading) {
    loading = (async () => {
      const mod = await (opts.importer ? opts.importer() : importVendor<{ default: unknown }>(`${HEIF_BASE}libheif-bundle.mjs`));
      const factory = mod.default as HeifFactory;
      const holder: { lib?: HeifLib } = {};
      await new Promise<void>((resolve, reject) => {
        const args: HeifModuleArgs = {
          onRuntimeInitialized: () => resolve(),
          onAbort: (r) => reject(new Error(String(r))),
          print: () => {},
          printErr: () => {},
        };
        holder.lib = factory(args);
      });
      // The wasm is inside the module, so the runtime can initialise before the factory has attached HeifDecoder: let it return.
      await Promise.resolve();
      const lib = holder.lib;
      if (!lib || typeof lib.HeifDecoder !== "function") throw new Error("libheif did not initialise");
      return lib;
    })();
    loading.catch(() => {
      loading = null; // allow a retry (offline first attempt...)
    });
  }
  return loading;
}

/** Decodes the primary image of a HEIC/HEIF file into RGBA (rotation/mirroring stored in the file are applied). */
export async function decodeHeic(bytes: Uint8Array, opts: HeifLoadOptions = {}): Promise<Rgba> {
  let lib: HeifLib;
  try {
    lib = await loadHeif(opts);
  } catch (e) {
    throw new ConvertError("engineLoadFailed", {}, e instanceof Error ? e.message : String(e));
  }
  let images: HeifImage[];
  try {
    images = new lib.HeifDecoder().decode(bytes);
  } catch (e) {
    throw new ConvertError("decodeFailed", {}, e instanceof Error ? e.message : String(e));
  }
  if (!images || images.length === 0) throw new ConvertError("decodeFailed", {}, "no image in HEIF");
  const image = images[0];
  try {
    const width = image.get_width();
    const height = image.get_height();
    if (!(width > 0 && height > 0)) throw new ConvertError("decodeFailed");
    const target = { data: new Uint8ClampedArray(width * height * 4), width, height };
    const result = await new Promise<{ data: Uint8ClampedArray } | null>((resolve) => image.display(target, resolve));
    if (!result) throw new ConvertError("decodeFailed", {}, "HEIF display failed");
    return { width, height, data: result.data };
  } finally {
    for (const img of images) {
      try {
        img.free();
      } catch {
        /* already freed */
      }
    }
  }
}
