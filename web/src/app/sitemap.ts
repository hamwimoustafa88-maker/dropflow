import type { MetadataRoute } from "next";
import { routing } from "@/i18n/routing";
import { languages, PAGES } from "@/lib/seo";
import { SITE_URL } from "@/lib/site";

// Every page in every locale, each with its hreflang alternates (ar, en, x-default -> Arabic).
export default function sitemap(): MetadataRoute.Sitemap {
  return PAGES.flatMap((path) =>
    routing.locales.map((locale) => ({
      url: `${SITE_URL}/${locale}${path}`,
      changeFrequency: path === "" || path === "/app" ? ("weekly" as const) : ("monthly" as const),
      priority: path === "" ? 1 : path === "/app" ? 0.9 : 0.3,
      alternates: { languages: languages(path, SITE_URL) },
    })),
  );
}
