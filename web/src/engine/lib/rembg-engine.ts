// Background removal model runner: ONNX Runtime Web (MIT, wasm backend, one thread, no SharedArrayBuffer needed) with the
// U²-Net-P model (Apache-2.0, 4.4 MB). Runtime and model come from the site's own origin (/vendor/ort, /vendor/models;
// copied or downloaded-and-hash-checked by scripts/vendor.mjs). The picture never leaves the device.

import type * as OrtNS from "onnxruntime-web";
import { U2NETP, toMask, toTensor } from "./segmentation";
import { importVendor } from "./vendor-import";

export const ORT_BASE = "/vendor/ort/";
export const U2NETP_URL = "/vendor/models/u2netp.onnx";

type Ort = typeof OrtNS;

export interface RembgConfig {
  /** Tests: import onnxruntime-web from node_modules. */
  importer?: () => Promise<Ort>;
  /** The model as bytes (tests) or a same-origin URL (browser). */
  model: string | Uint8Array;
}

export const BROWSER_REMBG: RembgConfig = { model: U2NETP_URL };

let session: Promise<{ ort: Ort; session: OrtNS.InferenceSession }> | null = null;

function load(config: RembgConfig) {
  if (!session) {
    session = (async () => {
      const ort = config.importer ? await config.importer() : await importVendor<Ort>(`${ORT_BASE}ort.wasm.min.mjs`);
      if (!config.importer) ort.env.wasm.wasmPaths = ORT_BASE;
      ort.env.wasm.numThreads = 1; // no cross-origin isolation needed
      ort.env.wasm.proxy = false; // already off the UI thread (DropFlow worker)
      const options: OrtNS.InferenceSession.SessionOptions = { executionProviders: ["wasm"], graphOptimizationLevel: "all" };
      const s =
        typeof config.model === "string"
          ? await ort.InferenceSession.create(config.model, options)
          : await ort.InferenceSession.create(config.model, options);
      return { ort, session: s };
    })();
    session.catch(() => {
      session = null; // allow a retry
    });
  }
  return session;
}

/**
 * Runs the network on a size x size RGBA picture (already flattened onto white) and returns the 8-bit mask at network
 * resolution (255 = keep).
 */
export async function predictMask(rgba320: Uint8ClampedArray, config: RembgConfig = BROWSER_REMBG): Promise<Uint8ClampedArray> {
  const { ort, session: s } = await load(config);
  const n = U2NETP.size;
  const input = new ort.Tensor("float32", toTensor(rgba320, n), [1, 3, n, n]);
  const out = await s.run({ [s.inputNames[0]]: input });
  const first = out[s.outputNames[0]];
  const data = first.data as Float32Array;
  if (data.length !== n * n) throw new Error(`unexpected mask size ${data.length}`);
  const mask = toMask(data);
  for (const name of s.outputNames) out[name]?.dispose?.();
  input.dispose?.();
  return mask;
}

/** Releases the model (tests). */
export async function closeRembg(): Promise<void> {
  const s = session;
  session = null;
  if (s) await s.then((x) => x.session.release()).catch(() => {});
}
