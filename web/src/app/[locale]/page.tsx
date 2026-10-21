import { hasLocale, useTranslations } from "next-intl";
import { setRequestLocale } from "next-intl/server";
import { notFound } from "next/navigation";
import { routing } from "@/i18n/routing";
import { Header } from "@/components/Header";
import { Logo } from "@/components/Logo";
import { WheelDemo } from "@/components/WheelDemo";
import { CategoryExplorer } from "@/components/CategoryExplorer";
import { Features } from "@/components/Features";
import { Marquee } from "@/components/Marquee";
import { SoonButton, WebButton } from "@/components/SoonButton";
import { Link } from "@/i18n/navigation";
import { stats } from "@/lib/catalog";

type Props = { params: Promise<{ locale: string }> };

export function generateStaticParams() {
  return routing.locales.map((locale) => ({ locale }));
}

function Hero() {
  const t = useTranslations("hero");
  return (
    <section id="top" className="relative overflow-hidden">
      <div aria-hidden="true" className="bg-grid absolute inset-0" />
      <div aria-hidden="true" className="hero-glow absolute inset-0" />
      <div className="relative mx-auto grid max-w-6xl items-center gap-14 px-4 pb-20 pt-14 sm:px-6 lg:grid-cols-[1fr_1fr] lg:gap-8 lg:pb-28 lg:pt-24">
        <div>
          <p className="glass inline-flex items-center gap-2 rounded-full px-4 py-1.5 text-sm font-medium">
            <span aria-hidden="true" className="size-2 rounded-full bg-accent" />
            {t("badge")}
          </p>
          <h1 className="mt-6 text-6xl font-bold leading-[1.1] tracking-tight sm:text-7xl xl:text-8xl">
            <span className="block">{t("titleA")}</span>
            <span className="text-gradient block">{t("titleB")}</span>
            <span className="block">{t("titleC")}</span>
          </h1>
          <p className="mt-6 text-xl text-muted sm:text-2xl">{t("subtitle")}</p>
          <div className="mt-9 flex flex-wrap gap-3">
            <SoonButton kind="windows" primary />
            <WebButton />
          </div>
        </div>
        <WheelDemo />
      </div>
    </section>
  );
}

function Numbers() {
  const t = useTranslations("numbers");
  const items = [
    { n: stats.conversions, label: t("conversions") },
    { n: stats.tools, label: t("tools") },
    { n: stats.actions, label: t("actions") },
  ];
  return (
    <section className="mx-auto max-w-6xl px-4 sm:px-6">
      <dl className="glass grid grid-cols-3 rounded-4xl py-8 text-center">
        {items.map((s) => (
          <div key={s.label} className="flex flex-col px-2 not-first:border-s not-first:border-border">
            <dd className="text-gradient order-1 text-5xl font-bold tabular-nums tracking-tight sm:text-7xl">{s.n}</dd>
            <dt className="order-2 mt-1 text-sm font-medium text-muted sm:text-lg">{s.label}</dt>
          </div>
        ))}
      </dl>
    </section>
  );
}

function Formats() {
  const t = useTranslations("formats");
  return (
    <section id="formats" className="mx-auto max-w-6xl px-4 pb-16 sm:px-6 lg:pb-24">
      <h2 className="reveal text-4xl font-bold tracking-tight sm:text-6xl">{t("title")}</h2>
      <div className="mt-8">
        <CategoryExplorer />
      </div>
    </section>
  );
}

function Cta() {
  const t = useTranslations("cta");
  return (
    <section className="relative overflow-hidden">
      <div aria-hidden="true" className="hero-glow absolute inset-0 opacity-70" />
      <div className="relative mx-auto max-w-6xl px-4 py-20 text-center sm:px-6 lg:py-32">
        <h2 className="reveal text-gradient text-5xl font-bold tracking-tight sm:text-7xl">{t("title")}</h2>
        <div className="mt-10 flex flex-wrap justify-center gap-3">
          <SoonButton kind="windows" primary />
          <WebButton />
        </div>
      </div>
    </section>
  );
}

function Footer() {
  const t = useTranslations("footer");
  return (
    <footer className="border-t border-border">
      <div className="mx-auto flex max-w-6xl flex-wrap items-center justify-between gap-x-6 gap-y-3 px-4 py-8 text-sm text-muted sm:px-6">
        <Logo />
        <nav aria-label={t("legal")} className="flex flex-wrap items-center gap-x-5">
          <Link href="/privacy" prefetch={false} className="inline-flex min-h-11 items-center transition-colors duration-200 hover:text-fg" data-testid="footer-privacy">
            {t("privacy")}
          </Link>
          <Link href="/terms" prefetch={false} className="inline-flex min-h-11 items-center transition-colors duration-200 hover:text-fg" data-testid="footer-terms">
            {t("terms")}
          </Link>
        </nav>
        <p>{t("license")}</p>
      </div>
    </footer>
  );
}

function SkipLink() {
  const t = useTranslations("common");
  return (
    <a href="#features" className="sr-only focus:not-sr-only focus:fixed focus:inset-s-4 focus:top-4 focus:z-50 focus:rounded-lg focus:bg-primary focus:px-4 focus:py-2 focus:text-on-primary">
      {t("skip")}
    </a>
  );
}

export default async function Home({ params }: Props) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  setRequestLocale(locale);

  return (
    <>
      <SkipLink />
      <Header />
      <main>
        <Hero />
        <Marquee />
        <Numbers />
        <Features />
        <Formats />
        <Cta />
      </main>
      <Footer />
    </>
  );
}
