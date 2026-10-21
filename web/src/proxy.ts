import createMiddleware from "next-intl/middleware";
import { routing } from "./i18n/routing";

// Redirects "/" to the visitor's language (Accept-Language), falling back to Arabic.
export default createMiddleware(routing);

export const config = {
  // Static trees (/vendor engines, /icons) and anything with a file extension are never locale-redirected.
  matcher: "/((?!api|trpc|_next|_vercel|vendor|icons|.*\\..*).*)",
};
