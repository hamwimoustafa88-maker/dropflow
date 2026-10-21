import { defineRouting } from "next-intl/routing";

export const routing = defineRouting({
  locales: ["ar", "en"],
  defaultLocale: "ar",
  localePrefix: "always",
  // The site sets no cookies at all (see the Privacy Policy): the language comes from the URL, or from Accept-Language for "/".
  localeCookie: false,
});

export type Locale = (typeof routing.locales)[number];
export const rtlLocales: readonly Locale[] = ["ar"];
