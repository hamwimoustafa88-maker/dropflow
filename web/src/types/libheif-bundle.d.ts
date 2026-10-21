// libheif-js ships typings for its CommonJS build only; the tests import the ES-module build (wasm inside) directly.
declare module "libheif-js/libheif-wasm/libheif-bundle.mjs" {
  const factory: unknown;
  export default factory;
}
