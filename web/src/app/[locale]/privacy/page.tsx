import type { Metadata } from "next";
import { hasLocale } from "next-intl";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { notFound } from "next/navigation";
import { LegalPage } from "@/components/LegalPage";
import { routing } from "@/i18n/routing";
import { pageMetadata } from "@/lib/seo";

type Props = { params: Promise<{ locale: string }> };

export function generateStaticParams() {
  return routing.locales.map((locale) => ({ locale }));
}

// Canonical, hreflang, Open Graph and Twitter tags come from pageMetadata (shared with every page).
export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "privacy.meta" });
  const m = await getTranslations({ locale, namespace: "meta" });
  return pageMetadata({ locale, path: "/privacy", title: t("title"), description: t("description"), imageAlt: m("ogAlt") });
}

export default async function Page({ params }: Props) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  setRequestLocale(locale);
  return <LegalPage doc="privacy" />;
}
