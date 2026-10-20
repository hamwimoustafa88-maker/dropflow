import { hasLocale, useLocale, useTranslations } from "next-intl";
import { setRequestLocale } from "next-intl/server";
import { notFound } from "next/navigation";
import { routing, type Locale } from "@/i18n/routing";
import { Header } from "@/components/Header";
import { Logo } from "@/components/Logo";
import { WheelDemo } from "@/components/WheelDemo";
import { CategoryExplorer } from "@/components/CategoryExplorer";
import { name, stats, toolsOf } from "@/lib/catalog";

type Props = { params: Promise<{ locale: string }> };

export function generateStaticParams() {
  return routing.locales.map((locale) => ({ locale }));
}

function SectionHeading({ id, kicker, title, subtitle }: { id?: string; kicker?: string; title: string; subtitle?: string }) {
  return (
    <div className="max-w-2xl">
      {kicker ? <p className="text-sm font-semibold text-accent">{kicker}</p> : null}
      <h2 id={id} className="mt-1 text-3xl font-bold leading-tight tracking-tight sm:text-4xl">
        {title}
      </h2>
      {subtitle ? <p className="mt-3 text-lg text-muted">{subtitle}</p> : null}
    </div>
  );
}

function SoonButton({ children, primary = false }: { children: React.ReactNode; primary?: boolean }) {
  return (
    <span
      aria-disabled="true"
      className={`inline-flex min-h-12 items-center justify-center rounded-xl px-5 text-base font-semibold ${
        primary ? "bg-primary text-on-primary opacity-90" : "border border-border bg-surface text-fg"
      }`}
    >
      {children}
    </span>
  );
}

function Hero() {
  const t = useTranslations("hero");
  return (
    <section id="top" className="hero-glow">
      <div className="mx-auto grid max-w-6xl items-center gap-10 px-4 py-14 sm:px-6 lg:grid-cols-2 lg:py-24">
        <div>
          <p className="inline-flex rounded-full border border-border bg-surface px-3.5 py-1.5 text-sm font-medium text-muted">{t("badge")}</p>
          <h1 className="mt-5 text-5xl font-bold leading-[1.08] tracking-tight sm:text-6xl lg:text-7xl">
            <span>{t("titleA")}</span> <span className="text-primary">{t("titleB")}</span> <span>{t("titleC")}</span>
          </h1>
          <p className="mt-6 max-w-xl text-lg leading-relaxed text-muted sm:text-xl">{t("subtitle")}</p>
          <div className="mt-8 flex flex-wrap gap-3">
            <SoonButton primary>{t("ctaWindows")}</SoonButton>
            <SoonButton>{t("ctaWeb")}</SoonButton>
          </div>
          <p className="mt-4 text-sm text-muted">{t("status")}</p>
        </div>
        <WheelDemo />
      </div>
    </section>
  );
}

function Stats() {
  const t = useTranslations("stats");
  const items = [
    { n: stats.conversions, label: t("conversions"), desc: t("conversionsDesc") },
    { n: stats.tools, label: t("tools"), desc: t("toolsDesc") },
    { n: stats.actions, label: t("actions"), desc: t("actionsDesc") },
  ];
  return (
    <section aria-label="DropFlow in numbers" className="mx-auto max-w-6xl px-4 sm:px-6">
      <dl className="grid gap-4 md:grid-cols-3">
        {items.map((s) => (
          <div key={s.label} className="flex flex-col rounded-3xl border border-border bg-surface p-6">
            <dt className="order-2 mt-1 text-lg font-semibold">{s.label}</dt>
            <dd className="order-1 text-5xl font-bold tracking-tight text-primary">{s.n}</dd>
            <dd className="order-3 mt-2 text-sm text-muted">{s.desc}</dd>
          </div>
        ))}
      </dl>
    </section>
  );
}

function How() {
  const t = useTranslations("how");
  const cards = ["convert", "tools", "actions"] as const;
  return (
    <section id="how" className="mx-auto max-w-6xl px-4 py-20 sm:px-6 lg:py-28">
      <SectionHeading title={t("title")} subtitle={t("subtitle")} />
      <div className="mt-10 grid gap-4 md:grid-cols-3">
        {cards.map((c) => (
          <article key={c} className="rounded-3xl border border-border bg-surface p-6">
            <kbd className="inline-flex min-h-9 items-center rounded-lg border border-border bg-surface-2 px-3 font-mono text-sm">{t(`${c}.key`)}</kbd>
            <h3 className="mt-4 text-xl font-semibold">{t(`${c}.title`)}</h3>
            <p className="mt-2 text-muted">{t(`${c}.text`)}</p>
          </article>
        ))}
      </div>
      <p className="mt-6 text-sm text-muted">{t("alt")}</p>
    </section>
  );
}

function Formats() {
  const t = useTranslations("explorer");
  return (
    <section id="formats" className="mx-auto max-w-6xl px-4 pb-20 sm:px-6 lg:pb-28">
      <SectionHeading title={t("title")} subtitle={t("subtitle")} />
      <div className="mt-10">
        <CategoryExplorer />
      </div>
    </section>
  );
}

function PdfSuite() {
  const t = useTranslations("pdf");
  const tc = useTranslations("common");
  const locale = useLocale() as Locale;
  const items = t.raw("convertItems") as string[];
  return (
    <section id="pdf" className="bg-surface-2/60 py-20 lg:py-28">
      <div className="mx-auto max-w-6xl px-4 sm:px-6">
        <SectionHeading title={t("title")} subtitle={t("subtitle")} />
        <div className="mt-10 grid gap-8 lg:grid-cols-2">
          <div>
            <h3 className="text-sm font-semibold uppercase tracking-wide text-muted">{t("convert")}</h3>
            <ul className="mt-3 grid gap-2 sm:grid-cols-2">
              {items.map((i) => (
                <li key={i} className="rounded-xl border border-border bg-surface px-4 py-3 text-sm font-medium">
                  {i}
                </li>
              ))}
            </ul>
          </div>
          <div>
            <h3 className="text-sm font-semibold uppercase tracking-wide text-muted">{t("organize")}</h3>
            <ul className="mt-3 grid gap-2 sm:grid-cols-2">
              {toolsOf("pdf").map((tool) => (
                <li key={tool.id} className="flex items-center justify-between gap-2 rounded-xl border border-border bg-surface px-4 py-3 text-sm">
                  <span>{name(tool, locale)}</span>
                  <span className="shrink-0 text-xs text-muted">{tc("phase", { n: tool.phase })}</span>
                </li>
              ))}
            </ul>
          </div>
        </div>
        <p className="mt-6 text-sm text-muted">{t("note")}</p>
      </div>
    </section>
  );
}

function BulletCard({ title, items }: { title: string; items: string[] }) {
  return (
    <article className="rounded-3xl border border-border bg-surface p-6 sm:p-8">
      <h3 className="text-xl font-semibold">{title}</h3>
      <ul className="mt-4 space-y-3">
        {items.map((i) => (
          <li key={i} className="flex gap-3 text-muted">
            <svg aria-hidden="true" width="20" height="20" viewBox="0 0 20 20" className="mt-1 shrink-0 text-accent" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="m4 10.5 4 4 8-9" />
            </svg>
            <span>{i}</span>
          </li>
        ))}
      </ul>
    </article>
  );
}

function Media() {
  const t = useTranslations("media");
  return (
    <section id="media" className="mx-auto max-w-6xl px-4 py-20 sm:px-6 lg:py-28">
      <SectionHeading title={t("title")} subtitle={t("subtitle")} />
      <div className="mt-10 grid gap-4 lg:grid-cols-2">
        <BulletCard title={t("encoder.title")} items={t.raw("encoder.items") as string[]} />
        <BulletCard title={t("stt.title")} items={t.raw("stt.items") as string[]} />
      </div>
    </section>
  );
}

function Privacy() {
  const t = useTranslations("privacy");
  const cards = t.raw("cards") as { title: string; text: string }[];
  return (
    <section id="privacy" className="bg-surface-2/60 py-20 lg:py-28">
      <div className="mx-auto max-w-6xl px-4 sm:px-6">
        <SectionHeading kicker={t("kicker")} title={t("title")} subtitle={t("text")} />
        <div className="mt-10 grid gap-4 md:grid-cols-3">
          {cards.map((c) => (
            <article key={c.title} className="rounded-3xl border border-border bg-surface p-6">
              <h3 className="text-lg font-semibold">{c.title}</h3>
              <p className="mt-2 text-muted">{c.text}</p>
            </article>
          ))}
        </div>
      </div>
    </section>
  );
}

const STATUS = ["next", "now", "later", "later", "later", "later", "later", "later", "later", "later"] as const;

function Roadmap() {
  const t = useTranslations("roadmap");
  const items = t.raw("items") as { title: string; text: string }[];
  return (
    <section id="roadmap" className="mx-auto max-w-6xl px-4 py-20 sm:px-6 lg:py-28">
      <SectionHeading title={t("title")} subtitle={t("subtitle")} />
      <ol className="mt-10 grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {items.map((it, i) => {
          const status = STATUS[i];
          return (
            <li key={it.title} className={`rounded-2xl border p-5 ${status === "now" ? "border-primary bg-primary-soft" : "border-border bg-surface"}`}>
              <div className="flex items-center justify-between gap-2">
                <span className="font-mono text-sm text-muted" dir="ltr">
                  {i < 9 ? i : "9+"}
                </span>
                <span className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${status === "now" ? "bg-primary text-on-primary" : "border border-border text-muted"}`}>
                  {t(`status.${status}`)}
                </span>
              </div>
              <h3 className="mt-2 font-semibold">{it.title}</h3>
              <p className="mt-1 text-sm text-muted">{it.text}</p>
            </li>
          );
        })}
      </ol>
    </section>
  );
}

function Cta() {
  const t = useTranslations("cta");
  return (
    <section className="mx-auto max-w-6xl px-4 pb-20 sm:px-6 lg:pb-28">
      <div className="rounded-4xl border border-border bg-surface p-8 text-center sm:p-14">
        <h2 className="text-3xl font-bold tracking-tight sm:text-4xl">{t("title")}</h2>
        <p className="mx-auto mt-3 max-w-md text-lg text-muted">{t("text")}</p>
        <div className="mt-8 flex flex-wrap justify-center gap-3">
          <SoonButton primary>
            {t("windows")} · {t("soon")}
          </SoonButton>
          <SoonButton>
            {t("web")} · {t("soon")}
          </SoonButton>
          <SoonButton>
            {t("source")} · {t("soon")}
          </SoonButton>
        </div>
      </div>
    </section>
  );
}

function Footer() {
  const t = useTranslations("footer");
  return (
    <footer className="border-t border-border">
      <div className="mx-auto flex max-w-6xl flex-col items-start justify-between gap-3 px-4 py-8 text-sm text-muted sm:flex-row sm:items-center sm:px-6">
        <Logo />
        <p>{t("tagline")}</p>
        <p>{t("license")}</p>
      </div>
    </footer>
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
      <main id="content">
        <Hero />
        <Stats />
        <How />
        <Formats />
        <PdfSuite />
        <Media />
        <Privacy />
        <Roadmap />
        <Cta />
      </main>
      <Footer />
    </>
  );
}

function SkipLink() {
  const t = useTranslations("common");
  return (
    <a href="#content" className="sr-only focus:not-sr-only focus:fixed focus:inset-s-4 focus:top-4 focus:z-50 focus:rounded-lg focus:bg-primary focus:px-4 focus:py-2 focus:text-on-primary">
      {t("skip")}
    </a>
  );
}
