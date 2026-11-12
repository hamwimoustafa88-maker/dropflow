// The proxy's matcher as a testable value. Next.js needs `config.matcher` in src/proxy.ts to be a literal it can read at build
// time, so the same string lives here and a test checks both are equal.
export const PROXY_MATCHER = "/((?!(?:ar|en)(?:/|$)|api|trpc|_next|_vercel|vendor|icons|.*\\..*).*)";

/** Whether the proxy runs for a request path (Next.js anchors the matcher to the whole path). */
export function proxyRuns(path: string): boolean {
  return new RegExp(`^${PROXY_MATCHER}$`).test(path);
}
