import { defineRouting } from "next-intl/routing";

export const routing = defineRouting({
  locales: ["ar", "en"],
  defaultLocale: "ar",
  localePrefix: "always",
  // The site sets no cookies at all (see the Privacy Policy): the language comes from the URL only. Arabic first:
  // "/" always goes to /ar (a permanent redirect in next.config.ts), whatever the browser's Accept-Language says.
  localeCookie: false,
  localeDetection: false,
  // hreflang is in each page's <head> and in sitemap.xml (x-default -> /ar); the proxy's `Link` header would say
  // x-default -> "/" instead, so it is turned off to keep one consistent signal.
  alternateLinks: false,
});

export type Locale = (typeof routing.locales)[number];
export const rtlLocales: readonly Locale[] = ["ar"];
