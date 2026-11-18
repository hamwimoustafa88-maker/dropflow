import type { Metadata } from "next";
import { hasLocale } from "next-intl";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { notFound } from "next/navigation";
import { SubpageShell } from "@/components/SubpageShell";
import { routing } from "@/i18n/routing";
import cli from "@/data/cli.json";
import { pageMetadata } from "@/lib/seo";

// The `dropflow` command line: one card per command with a copyable example in a dark terminal block. The command list
// lives in src/data/cli.json, kept in sync with the real parser by a .NET test (CliDocsTests).

type Props = { params: Promise<{ locale: string }> };

export function generateStaticParams() {
  return routing.locales.map((locale) => ({ locale }));
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "cli" });
  const m = await getTranslations({ locale, namespace: "meta" });
  return pageMetadata({ locale, path: "/cli", title: t("metaTitle"), description: t("metaDescription"), imageAlt: m("ogAlt") });
}

export default async function Page({ params }: Props) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  setRequestLocale(locale);
  const t = await getTranslations({ locale, namespace: "cli" });
  const ar = locale === "ar";
  return (
    <SubpageShell locale={locale}>
      <div aria-hidden="true" className="blobs" />
      <section className="relative mx-auto max-w-5xl px-4 pb-20 pt-14 sm:px-6 lg:pt-24" data-testid="cli-page">
        <h1 className="text-5xl font-bold tracking-tight sm:text-7xl">
          <span className="text-gradient">dropflow</span> {t("title")}
        </h1>
        <p className="mt-4 text-xl text-muted">{t("lead")}</p>

        <ul className="mt-12 grid gap-4 md:grid-cols-2">
          {cli.commands.map((c) => (
            <li key={c.verb} className="glass rounded-4xl p-5" data-testid="cli-command">
              <h2 className="font-mono text-lg font-semibold" dir="ltr">
                {c.verb}
              </h2>
              <p className="mt-1 text-muted">{ar ? c.ar : c.en}</p>
              <pre dir="ltr" className="mt-4 overflow-x-auto rounded-2xl bg-term-bg px-4 py-3 text-sm text-term-fg">
                <code>
                  <span className="text-term-ok">$ </span>
                  {c.example}
                </code>
              </pre>
            </li>
          ))}
        </ul>

        <h2 className="mt-16 text-3xl font-bold tracking-tight">{t("exitCodes")}</h2>
        <dl className="glass mt-6 grid max-w-xl overflow-hidden rounded-3xl">
          {cli.exitCodes.map((e) => (
            <div key={e.code} className="flex items-center gap-4 border-b border-border/70 px-5 py-3 last:border-b-0">
              <dt className="font-mono font-bold text-primary">{e.code}</dt>
              <dd>{ar ? e.ar : e.en}</dd>
            </div>
          ))}
        </dl>
        <p className="mt-8 text-muted">{t("note")}</p>
      </section>
    </SubpageShell>
  );
}
