import type { Metadata, Viewport } from "next";
import { hasLocale, NextIntlClientProvider } from "next-intl";
import { getMessages, getTranslations, setRequestLocale } from "next-intl/server";
import { notFound } from "next/navigation";
import { routing } from "@/i18n/routing";
import { pageMetadata } from "@/lib/seo";
import "./app.css";

type Props = {
  children: React.ReactNode;
  params: Promise<{ locale: string }>;
};

// Browser chrome colour = the page background (--bg in globals.css). The site is always light.
export const viewport: Viewport = {
  themeColor: "#f7f8fc",
  colorScheme: "light",
};

export async function generateMetadata({ params }: Pick<Props, "params">): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "app.meta" });
  const m = await getTranslations({ locale, namespace: "meta" });
  return {
    ...pageMetadata({ locale, path: "/app", title: t("title"), description: t("description"), imageAlt: m("ogAlt") }),
    // The manifest is linked from /app only; Arabic is the default locale (manifest.webmanifest), English has its own.
    manifest: locale === "ar" ? "/manifest.webmanifest" : `/manifest.${locale}.webmanifest`,
    icons: { icon: [{ url: "/icon.svg", type: "image/svg+xml" }], apple: "/icons/apple-touch-icon.png" },
    other: { "mobile-web-app-capable": "yes" },
  };
}

// The app strings live in their own namespace and are only shipped with /app: the landing layout filters them out.
export default async function AppLayout({ children, params }: Props) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  setRequestLocale(locale);
  const messages = await getMessages({ locale });
  return (
    <NextIntlClientProvider locale={locale} messages={{ app: messages.app, common: messages.common, credit: messages.credit }}>
      {children}
    </NextIntlClientProvider>
  );
}
