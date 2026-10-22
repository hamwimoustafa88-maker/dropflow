import { hasLocale, useLocale, useTranslations } from "next-intl";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { notFound } from "next/navigation";
import { routing } from "@/i18n/routing";
import { Header } from "@/components/Header";
import { Logo } from "@/components/Logo";
import { WheelDemo } from "@/components/WheelDemo";
import { CategoryExplorer } from "@/components/CategoryExplorer";
import { Features } from "@/components/Features";
import { About } from "@/components/About";
import { Developers } from "@/components/Developers";
import { Faq, faqItems, type FaqItem } from "@/components/Faq";
import { Marquee } from "@/components/Marquee";
import { Downloads } from "@/components/DownloadButtons";
import { Link } from "@/i18n/navigation";
import { exploreCategories, stats } from "@/lib/catalog";
import type { Locale } from "@/i18n/routing";
import { jsonLdString, softwareJsonLd } from "@/lib/seo";
import { REPO_URL } from "@/lib/site";
import { WhatsAppButton } from "@/components/WhatsAppButton";

type Props = { params: Promise<{ locale: string }> };

export function generateStaticParams() {
  return routing.locales.map((locale) => ({ locale }));
}

function Hero() {
  const t = useTranslations("hero");
  // overflow-x-clip (not hidden): an opened download list may extend below the hero.
  return (
    <section id="top" className="relative overflow-x-clip">
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
          <div className="mt-9">
            <Downloads id="download" note />
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
  const locale = useLocale() as Locale;
  return (
    <section id="formats" className="mx-auto max-w-6xl px-4 pb-16 sm:px-6 lg:pb-24">
      <h2 className="reveal text-4xl font-bold tracking-tight sm:text-6xl">{t("title")}</h2>
      <div className="mt-8">
        <CategoryExplorer categories={exploreCategories(locale)} />
      </div>
    </section>
  );
}

function Cta() {
  const t = useTranslations("cta");
  return (
    <section className="relative overflow-x-clip">
      <div aria-hidden="true" className="hero-glow absolute inset-0 opacity-70" />
      <div className="relative mx-auto max-w-6xl px-4 py-20 text-center sm:px-6 lg:py-32">
        <h2 className="reveal text-gradient pb-3 text-5xl font-bold tracking-tight sm:text-7xl">{t("title")}</h2>
        <div className="mt-10">
          <Downloads center />
        </div>
        <div className="mt-8 flex flex-wrap items-center justify-center gap-x-4 gap-y-3">
          <p className="text-lg font-medium text-muted">{t("ask")}</p>
          <WhatsAppButton variant="outline" testId="cta-whatsapp" />
        </div>
      </div>
    </section>
  );
}

// The footer is deliberately small: logo + one line, Privacy, Terms, ONE "Open source" link and ONE contact button (WhatsApp).
// Issues, contributing, security, code-signing policy and the licence live in the Developers section (#developers).
function Footer() {
  const t = useTranslations("footer");
  const link = "inline-flex min-h-11 items-center rounded-full px-3.5 font-medium transition-colors duration-200 hover:bg-surface-2 hover:text-fg";
  return (
    <footer className="border-t border-border">
      <div className="mx-auto grid max-w-6xl gap-6 px-4 py-10 sm:px-6 md:grid-cols-[1fr_auto_auto] md:items-center md:gap-8">
        <div className="grid justify-items-start gap-1.5">
          <Logo />
          <p className="text-sm text-muted" data-testid="footer-tagline">
            {t("tagline")}
          </p>
        </div>
        <nav aria-label={t("legal")} className="-mx-3.5 flex flex-wrap items-center text-sm text-muted md:mx-0" data-testid="footer-nav">
          <Link href="/privacy" prefetch={false} className={link} data-testid="footer-privacy">
            {t("privacy")}
          </Link>
          <Link href="/terms" prefetch={false} className={link} data-testid="footer-terms">
            {t("terms")}
          </Link>
          <a href={REPO_URL} target="_blank" rel="noopener noreferrer" className={link} data-testid="footer-github">
            {t("opensource")}
          </a>
        </nav>
        <WhatsAppButton testId="footer-whatsapp" className="justify-self-start md:justify-self-end" />
      </div>
      <p className="border-t border-border/70 px-4 py-4 text-center text-xs text-muted sm:px-6" data-testid="footer-credit">
        {t("credit")}
      </p>
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
  const t = await getTranslations({ locale, namespace: "meta" });
  const tf = await getTranslations({ locale, namespace: "faq" });
  const faq = faqItems(tf.raw("items") as FaqItem[]);

  return (
    <>
      {/* Structured data for search engines: a data block, never executed (so the CSP has nothing to allow). */}
      <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: jsonLdString(softwareJsonLd(locale, t("description"), faq)) }} />
      <SkipLink />
      <Header />
      <main>
        <Hero />
        <Marquee />
        <Numbers />
        <Features />
        <Formats />
        <About />
        <Developers />
        <Faq />
        <Cta />
      </main>
      <Footer />
    </>
  );
}
