import type { Metadata } from "next";
import { IBM_Plex_Sans_Arabic, IBM_Plex_Mono } from "next/font/google";
import { notFound } from "next/navigation";
import { hasLocale, NextIntlClientProvider } from "next-intl";
import { getMessages, getTranslations, setRequestLocale } from "next-intl/server";
import { routing, rtlLocales } from "@/i18n/routing";
import { pageMetadata, siteMetadata } from "@/lib/seo";
import "../globals.css";

const plex = IBM_Plex_Sans_Arabic({
  subsets: ["arabic", "latin"],
  weight: ["400", "500", "600", "700"],
  variable: "--font-plex",
  display: "swap",
});

const plexMono = IBM_Plex_Mono({
  subsets: ["latin"],
  weight: ["500"],
  variable: "--font-plex-mono",
  display: "swap",
  // Only small labels use the mono face: not worth competing with the page's CSS and text font on a slow connection.
  preload: false,
});

type Props = {
  children: React.ReactNode;
  params: Promise<{ locale: string }>;
};

export function generateStaticParams() {
  return routing.locales.map((locale) => ({ locale }));
}

// The site is public and indexable (GPL release, ADR-0013): no robots meta. Pages refine title/description/canonical.
export async function generateMetadata({ params }: Pick<Props, "params">): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "meta" });
  return {
    ...siteMetadata,
    ...pageMetadata({ locale, path: "", title: t("title"), description: t("description"), imageAlt: t("ogAlt") }),
    keywords: t.raw("keywords") as string[],
  };
}

export default async function LocaleLayout({ children, params }: Props) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  setRequestLocale(locale);
  // The web app's strings (`app` namespace) ship only with /app (its own layout); the long legal texts (`privacy`, `terms`)
  // and the <head> metadata (`meta`) are rendered on the server and never sent to the browser as JSON.
  const SERVER_ONLY = new Set(["app", "privacy", "terms", "meta", "faq"]);
  const messages = Object.fromEntries(Object.entries(await getMessages()).filter(([key]) => !SERVER_ONLY.has(key)));

  return (
    <html
      lang={locale}
      dir={rtlLocales.includes(locale) ? "rtl" : "ltr"}
      className={`${plex.variable} ${plexMono.variable}`}
    >
      <body>
        <NextIntlClientProvider messages={messages}>{children}</NextIntlClientProvider>
      </body>
    </html>
  );
}
