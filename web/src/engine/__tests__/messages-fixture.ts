// The merged UI messages (messages/{en,ar}.json -> `app` namespace). Domain tests check their ops/options/errors
// against the real message files, not against scratch fragments.
import { readFileSync } from "node:fs";

export type Tree = { [k: string]: string | Tree };

function load(locale: "en" | "ar"): Tree {
  const url = new URL(`../../../messages/${locale}.json`, import.meta.url);
  return (JSON.parse(readFileSync(url, "utf8")) as { app: Tree }).app;
}

export const appMessages = { en: load("en"), ar: load("ar") };
