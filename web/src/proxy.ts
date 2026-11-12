import createMiddleware from "next-intl/middleware";
import { routing } from "./i18n/routing";

// Adds the language to a path that has none (/app -> /ar/app); "/" itself is a permanent redirect in next.config.ts.
export default createMiddleware(routing);

export const config = {
  // Runs ONLY for paths without a language: /ar/... and /en/... (every page a visitor or crawler really opens) never reach
  // the proxy, so a page view costs no middleware invocation on Vercel. Static trees (/vendor engines, /icons) and anything
  // with a file extension are never locale-redirected either. Keep in sync with PROXY_MATCHER in src/lib/proxy-matcher.ts.
  matcher: "/((?!(?:ar|en)(?:/|$)|api|trpc|_next|_vercel|vendor|icons|.*\\..*).*)",
};
