import type { Metadata } from "next";
import { routing } from "@/i18n/routing";
import { GPL_URL, RELEASES_URL, REPO_URL, SITE_URL } from "./site";

// Search metadata shared by every page: canonical + hreflang (ar, en, x-default -> Arabic), Open Graph and Twitter card.
// Next merges metadata shallowly, so each page passes its own title/description and gets the whole set back.

/** Every indexable page, as the path after the locale ("" = landing). The sitemap lists each one in every locale. */
export const PAGES = ["", "/app", "/privacy", "/terms"] as const;
export type PagePath = (typeof PAGES)[number];

const OG_LOCALE: Record<string, string> = { ar: "ar_AR", en: "en_US" };

/** hreflang map for a page; relative paths resolve against metadataBase. */
export function languages(path: PagePath, base = ""): Record<string, string> {
  return {
    ...Object.fromEntries(routing.locales.map((l) => [l, `${base}/${l}${path}`])),
    "x-default": `${base}/${routing.defaultLocale}${path}`,
  };
}

export function pageMetadata({ locale, path, title, description, imageAlt }: { locale: string; path: PagePath; title: string; description: string; imageAlt: string }): Metadata {
  const url = `/${locale}${path}`;
  // The share image is generated per locale by [locale]/opengraph-image.tsx; naming it explicitly keeps it on every page,
  // because a page that sets `openGraph` replaces the parent's object.
  const image = { url: `/${locale}/opengraph-image`, width: 1200, height: 630, alt: imageAlt };
  return {
    title,
    description,
    alternates: { canonical: url, languages: languages(path) },
    openGraph: {
      type: "website",
      siteName: "DropFlow",
      locale: OG_LOCALE[locale],
      alternateLocale: routing.locales.filter((l) => l !== locale).map((l) => OG_LOCALE[l]),
      url,
      title,
      description,
      images: [image],
    },
    twitter: { card: "summary_large_image", title, description, images: [image.url] },
  };
}

/** Site-wide metadata set once in the locale layout. */
export const siteMetadata: Metadata = {
  metadataBase: new URL(SITE_URL),
  applicationName: "DropFlow",
  authors: [{ name: "ScoutHub" }],
  creator: "ScoutHub",
  publisher: "ScoutHub",
  category: "technology",
};

/** schema.org description of the product and its source code (rendered as JSON-LD on the landing page). */
export function softwareJsonLd(locale: string, description: string, faq: { q: string; a: string }[] = []) {
  const app = `${SITE_URL}/#software`;
  // Made by the ScoutHub teams; the lead developer is named as a member, not as the author.
  const author = {
    "@type": "Organization",
    "@id": `${SITE_URL}/#scouthub`,
    name: "ScoutHub",
    member: { "@type": "Person", name: "Moustafa Al-Hamwi", jobTitle: "Lead developer", address: { "@type": "PostalAddress", addressCountry: "LB" } },
  };
  return {
    "@context": "https://schema.org",
    "@graph": [
      {
        "@type": "SoftwareApplication",
        "@id": app,
        name: "DropFlow",
        url: `${SITE_URL}/${locale}`,
        description,
        operatingSystem: "Windows 10, Windows 11, Web",
        applicationCategory: "UtilitiesApplication",
        offers: { "@type": "Offer", price: 0, priceCurrency: "USD" },
        isAccessibleForFree: true,
        license: GPL_URL,
        downloadUrl: RELEASES_URL,
        inLanguage: ["ar", "en"],
        image: `${SITE_URL}/${locale}/opengraph-image`,
        author,
        publisher: { "@id": `${SITE_URL}/#scouthub` },
      },
      {
        "@type": "SoftwareSourceCode",
        "@id": `${SITE_URL}/#source`,
        name: "DropFlow",
        codeRepository: REPO_URL,
        programmingLanguage: ["C#", "TypeScript"],
        license: GPL_URL,
        targetProduct: { "@id": app },
        author: { "@id": `${SITE_URL}/#scouthub` },
      },
      ...(faq.length
        ? [
            {
              "@type": "FAQPage",
              "@id": `${SITE_URL}/${locale}#faq`,
              inLanguage: locale,
              mainEntity: faq.map(({ q, a }) => ({ "@type": "Question", name: q, acceptedAnswer: { "@type": "Answer", text: a } })),
            },
          ]
        : []),
    ],
  };
}

/** JSON.stringify for an inline <script type="application/ld+json">: `<` is escaped so no text can close the tag. */
export function jsonLdString(data: unknown): string {
  return JSON.stringify(data).replace(/</g, "\\u003c");
}
