import type { Metadata } from "next";
import { hasLocale } from "next-intl";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { notFound } from "next/navigation";
import { Downloads } from "@/components/DownloadButtons";
import { SubpageShell } from "@/components/SubpageShell";
import { Link } from "@/i18n/navigation";
import { routing } from "@/i18n/routing";
import { PAIRS, formatName, pairBySlug, related } from "@/lib/pairs";
import { jsonLdString, pageMetadata } from "@/lib/seo";
import { SITE_URL } from "@/lib/site";

// One popular conversion ("HEIC to JPG"): a headline, three short steps, the way to do it (browser app if it can, the
// download otherwise) and related conversions. Static: generated for every pair x locale at build time.

type Props = { params: Promise<{ locale: string; pair: string }> };

export function generateStaticParams() {
  return routing.locales.flatMap((locale) => PAIRS.map((p) => ({ locale, pair: p.slug })));
}

export const dynamicParams = false;

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { locale, pair: slug } = await params;
  const pair = pairBySlug(slug);
  if (!hasLocale(routing.locales, locale) || !pair) return {};
  const t = await getTranslations({ locale, namespace: "pairs" });
  const m = await getTranslations({ locale, namespace: "meta" });
  const names = { from: formatName(pair.from, locale), to: formatName(pair.to, locale) };
  return pageMetadata({
    locale,
    path: `/convert/${pair.slug}`,
    title: t("metaTitle", names),
    description: t(pair.web ? "metaDescriptionWeb" : "metaDescription", names),
    imageAlt: m("ogAlt"),
  });
}

export default async function Page({ params }: Props) {
  const { locale, pair: slug } = await params;
  const pair = pairBySlug(slug);
  if (!hasLocale(routing.locales, locale) || !pair) notFound();
  setRequestLocale(locale);
  const t = await getTranslations({ locale, namespace: "pairs" });
  const names = { from: formatName(pair.from, locale), to: formatName(pair.to, locale) };
  const steps = [t("step1", names), t("step2", names), t("step3", names)];
  const howTo = {
    "@context": "https://schema.org",
    "@type": "HowTo",
    name: t("title", names),
    inLanguage: locale,
    url: `${SITE_URL}/${locale}/convert/${pair.slug}`,
    tool: { "@type": "SoftwareApplication", name: "DropFlow", "@id": `${SITE_URL}/#software` },
    step: steps.map((text, i) => ({ "@type": "HowToStep", position: i + 1, text })),
  };

  return (
    <SubpageShell locale={locale}>
      <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: jsonLdString(howTo) }} />
      <div aria-hidden="true" className="blobs" />
      <section className="relative mx-auto max-w-4xl px-4 pb-16 pt-14 text-center sm:px-6 lg:pt-24" data-testid="pair-page">
        <p className="glass inline-flex items-center gap-2 rounded-full px-4 py-1.5 text-sm font-medium">
          <span aria-hidden="true" className="size-2 rounded-full bg-accent" />
          {t("badge")}
        </p>
        <h1 className="mt-6 text-5xl font-bold leading-[1.15] tracking-tight sm:text-7xl">
          {t("titleA")} <span className="text-gradient">{names.from}</span> {t("titleB")} <span className="text-gradient">{names.to}</span>
        </h1>
        <p className="mt-5 text-xl text-muted sm:text-2xl">{t("lead")}</p>

        <ol className="mx-auto mt-12 grid max-w-3xl gap-3 text-start sm:grid-cols-3">
          {steps.map((s, i) => (
            <li key={i} className="glass flex items-center gap-3 rounded-3xl p-4">
              <span aria-hidden="true" className="step-dot flex size-10 shrink-0 items-center justify-center rounded-xl text-sm font-bold text-on-primary">
                {i + 1}
              </span>
              <span className="font-semibold">{s}</span>
            </li>
          ))}
        </ol>

        <div className="mt-12 flex flex-col items-center gap-6">
          {pair.web ? (
            <Link href="/app" prefetch={false} className="btn-primary inline-flex min-h-13 items-center rounded-2xl px-7 text-lg font-semibold" data-testid="pair-web">
              {t("tryWeb")}
            </Link>
          ) : null}
          <Downloads center />
        </div>
      </section>

      <section className="relative mx-auto max-w-4xl px-4 pb-20 sm:px-6">
        <h2 className="text-2xl font-bold tracking-tight">{t("related")}</h2>
        <ul className="mt-4 flex flex-wrap gap-2">
          {related(pair).map((p) => (
            <li key={p.slug}>
              <Link href={`/convert/${p.slug}`} prefetch={false} className="glass inline-flex min-h-11 items-center rounded-full px-4 font-medium transition-colors duration-200 hover:border-primary">
                {t("short", { from: formatName(p.from, locale), to: formatName(p.to, locale) })}
              </Link>
            </li>
          ))}
        </ul>
      </section>
    </SubpageShell>
  );
}
