// Runtime import of an ES module that the site serves from its OWN origin under /vendor (copied from node_modules by
// scripts/vendor.mjs). Used for engines that must not be bundled (Tesseract.js, ONNX Runtime, libheif): bundling them
// would make Turbopack copy their multi-megabyte wasm into the app shell. Only same-origin /vendor paths are accepted.

export function importVendor<T>(path: string): Promise<T> {
  if (!/^\/vendor\/[\w./-]+\.m?js$/.test(path) || path.includes("..")) return Promise.reject(new Error(`not a vendor module: ${path}`));
  return import(/* webpackIgnore: true */ /* turbopackIgnore: true */ path) as Promise<T>;
}
