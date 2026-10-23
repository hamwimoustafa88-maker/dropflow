import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { ISSUES_URL, LEGAL_LINKS, WHATSAPP_NUMBER, type LegalLinkKey } from "@/lib/site";
import { GitHubLink, GitHubMark } from "./GitHubLink";
import { LocaleSwitcher } from "./LocaleSwitcher";
import { Logo } from "./Logo";
import { SiteCredit } from "./SiteCredit";
import { WhatsAppButton } from "./WhatsAppButton";

// A readable document page (privacy policy, terms of use). All text lives in messages/<locale>.json under the `privacy` /
// `terms` namespace; nothing is hard-coded here:
//   { title, lead, summary: string[], sections: [{ id, h, subs: [{ h, tone?, p?, ul?, p2?, links? }] }] }
// A section (h2) groups sub-sections (h3). `tone` turns a sub-section into a callout: "promise" (a key promise) or "warn" (a limit).
// `links` are keys of LEGAL_LINKS (the URLs live in code, the labels in `legal.links`). Static and server-rendered: no script.

export type LegalDoc = "privacy" | "terms";
type Sub = { h: string; tone?: "promise" | "warn"; p?: string[]; ul?: string[]; p2?: string[]; links?: LegalLinkKey[] };
type Group = { id: string; h: string; subs: Sub[] };

const num = (i: number) => String(i + 1).padStart(2, "0");

function Icon({ d, className = "size-5" }: { d: string; className?: string }) {
  return (
    <svg aria-hidden="true" focusable="false" viewBox="0 0 24 24" className={className} fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
      <path d={d} />
    </svg>
  );
}
const CHECK = "M5 12.5l4.5 4.5L19 7.5";
const SHIELD = "M12 3l7 3v5c0 4.5-3 8.5-7 10-4-1.5-7-5.5-7-10V6l7-3zM9 12l2 2 4-4";
const ALERT = "M12 3.5l9.5 16.5h-19L12 3.5zM12 10v4.5M12 17.5v.01";
const CAL = "M4 6.5h16V20H4V6.5zM4 10.5h16M8 3.5v4M16 3.5v4";
const ARROW = "M7 17L17 7M9 7h8v8";

const TONE = {
  promise: { box: "border-primary/30 bg-primary-soft/60", dot: "bg-primary", icon: "text-primary", path: SHIELD },
  warn: { box: "border-warn-border bg-warn-soft text-warn", dot: "bg-warn", icon: "text-warn", path: ALERT },
} as const;

function TocList({ items, onlyDesktop = false }: { items: { id: string; h: string }[]; onlyDesktop?: boolean }) {
  return (
    <ol className={onlyDesktop ? "grid gap-1" : "grid gap-1 px-2 pb-3"}>
      {items.map((it, i) => (
        <li key={it.id}>
          <a href={`#${it.id}`} className="flex min-h-11 items-center gap-3 rounded-xl px-3 py-1.5 text-base font-medium text-muted transition-colors duration-200 hover:bg-primary-soft hover:text-fg">
            <span dir="ltr" className="inline-flex size-7 shrink-0 items-center justify-center rounded-lg bg-surface-2 text-xs font-bold text-primary">
              {num(i)}
            </span>
            <span className="min-w-0">{it.h}</span>
          </a>
        </li>
      ))}
    </ol>
  );
}

function SubSection({ s }: { s: Sub }) {
  const tl = useTranslations("legal");
  const tone = s.tone ? TONE[s.tone] : null;
  return (
    <div className={tone ? `relative rounded-3xl border-2 p-5 sm:p-7 ${tone.box}` : ""} data-tone={s.tone}>
      {tone ? <Icon d={tone.path} className={`absolute inset-e-5 top-5 size-7 opacity-80 ${tone.icon}`} /> : null}
      <h3 className={`flex items-center gap-3 text-xl font-bold leading-snug sm:text-2xl ${tone ? "pe-10" : ""}`}>
        <span aria-hidden="true" className="h-7 w-1.5 shrink-0 rounded-full bg-linear-to-b from-primary to-accent" />
        <span className="min-w-0">{s.h}</span>
      </h3>
      <div className="mt-4 max-w-176 space-y-4 text-lg leading-9">
        {s.p?.map((text, j) => <p key={j}>{text}</p>)}
        {s.ul ? (
          <ul className="space-y-3">
            {s.ul.map((text, j) => (
              <li key={j} className="relative ps-8">
                <span aria-hidden="true" className={`absolute inset-s-1 top-[0.8rem] size-2.5 rounded-full ${tone ? tone.dot : "bg-accent"}`} />
                {text}
              </li>
            ))}
          </ul>
        ) : null}
        {s.p2?.map((text, j) => <p key={j}>{text}</p>)}
      </div>
      {s.links ? (
        <ul className="mt-5 flex flex-wrap gap-3">
          {s.links.map((key) => (
            <li key={key}>
              <a
                href={LEGAL_LINKS[key]}
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex min-h-11 items-center gap-2 rounded-full border-2 border-primary/30 bg-surface px-5 text-base font-semibold text-primary transition-colors duration-200 hover:border-primary hover:bg-primary-soft"
              >
                {tl(`links.${key}`)}
                <Icon d={ARROW} className="size-4 rtl:-scale-x-100" />
              </a>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

export function LegalPage({ doc }: { doc: LegalDoc }) {
  const t = useTranslations(doc);
  const tl = useTranslations("legal");
  const tc = useTranslations("common");
  const groups = t.raw("sections") as Group[];
  const summary = t.raw("summary") as string[];
  const other: LegalDoc = doc === "privacy" ? "terms" : "privacy";
  const toc = [...groups.map((g) => ({ id: g.id, h: g.h })), { id: "contact", h: tl("contactTitle") }];

  return (
    <>
      <header className="sticky top-0 z-30 border-b border-border/60 bg-bg/70 backdrop-blur-xl">
        <div className="mx-auto flex h-16 max-w-6xl items-center justify-between gap-4 px-4 sm:px-6">
          <Link href="/" aria-label={tl("home")} data-testid="legal-home">
            <Logo />
          </Link>
          <div className="flex items-center gap-2 sm:gap-4">
            <Link href="/" className="hidden min-h-11 items-center rounded-md px-1 text-sm font-medium text-muted transition-colors duration-200 hover:text-fg sm:inline-flex">
              {tl("home")}
            </Link>
            <GitHubLink />
            <LocaleSwitcher />
          </div>
        </div>
      </header>

      <main id="content" data-testid={`legal-${doc}`}>
        <div className="relative overflow-x-clip">
          <div aria-hidden="true" className="bg-grid absolute inset-0" />
          <div aria-hidden="true" className="hero-glow absolute inset-0" />
          <div className="relative mx-auto max-w-6xl px-4 pb-12 pt-12 sm:px-6 sm:pb-16 sm:pt-20">
            <p className="glass inline-flex items-center gap-2 rounded-full px-4 py-1.5 text-sm font-semibold text-primary">
              <Icon d={SHIELD} className="size-4" />
              {tl(`eyebrow.${doc}`)}
            </p>
            <h1 className="mt-6 text-5xl font-bold leading-[1.15] tracking-tight sm:text-6xl lg:text-7xl">{t("title")}</h1>
            <p className="mt-6 max-w-4xl text-xl leading-9 text-muted sm:text-2xl sm:leading-10">{t("lead")}</p>
            <p className="mt-6 inline-flex items-center gap-2 rounded-full border border-border bg-surface px-4 py-1.5 text-sm font-medium text-muted">
              <Icon d={CAL} className="size-4 text-accent" />
              {tl("updated")}: <time dateTime="2026-10-21">{tl("date")}</time>
            </p>

            <section aria-labelledby={`${doc}-summary`} data-testid="legal-summary" className="mt-10 rounded-4xl border border-primary/20 bg-surface p-6 shadow-xl shadow-primary/10 sm:p-8">
              <p id={`${doc}-summary`} className="flex items-center gap-2.5 text-lg font-bold text-primary">
                <Icon d={SHIELD} className="size-6" />
                {tl("summaryTitle")}
              </p>
              <ul className="mt-5 grid gap-3 sm:grid-cols-2">
                {summary.map((text, i) => (
                  <li key={i} className="flex items-start gap-3 rounded-2xl bg-primary-soft/50 p-4 text-lg font-medium leading-8">
                    <span aria-hidden="true" className="mt-1 inline-flex size-6 shrink-0 items-center justify-center rounded-full bg-ok text-on-ok">
                      <Icon d={CHECK} className="size-4" />
                    </span>
                    <span className="min-w-0">{text}</span>
                  </li>
                ))}
              </ul>
            </section>
          </div>
        </div>

        <div className="mx-auto grid max-w-6xl gap-8 px-4 pb-20 sm:px-6 lg:grid-cols-[17rem_minmax(0,1fr)] lg:gap-16">
          <aside className="lg:sticky lg:top-24 lg:self-start" data-testid="legal-toc">
            <details className="rounded-2xl border border-border bg-surface lg:hidden">
              <summary className="flex min-h-12 cursor-pointer list-none items-center justify-between gap-3 px-4 text-base font-bold [&::-webkit-details-marker]:hidden">
                {tl("toc")}
                <Icon d="M6 9l6 6 6-6" className="size-5 text-muted" />
              </summary>
              <nav aria-label={tl("toc")}>
                <TocList items={toc} />
              </nav>
            </details>
            <nav aria-label={tl("toc")} className="hidden rounded-3xl border border-border bg-surface p-3 lg:block">
              <p className="px-3 pb-2 pt-2 text-base font-bold">{tl("toc")}</p>
              <TocList items={toc} onlyDesktop />
            </nav>
          </aside>

          <article className="min-w-0">
            {groups.map((g, i) => (
              <section key={g.id} id={g.id} aria-labelledby={`${g.id}-h`} className="scroll-mt-24 border-t border-border pt-12 first:border-t-0 first:pt-0 not-first:mt-14">
                <div className="flex items-center gap-4">
                  <span dir="ltr" aria-hidden="true" className="inline-flex size-12 shrink-0 items-center justify-center rounded-2xl bg-primary text-lg font-bold text-on-primary">
                    {num(i)}
                  </span>
                  <h2 id={`${g.id}-h`} className="min-w-0 text-3xl font-bold leading-tight tracking-tight sm:text-4xl">
                    {g.h}
                  </h2>
                </div>
                <div className="mt-8 grid gap-10">
                  {g.subs.map((s, j) => (
                    <SubSection key={j} s={s} />
                  ))}
                </div>
              </section>
            ))}

            <section id="contact" aria-labelledby={`${doc}-contact`} className="scroll-mt-24 border-t border-border pt-12 not-first:mt-14">
              <div className="flex items-center gap-4">
                <span dir="ltr" aria-hidden="true" className="inline-flex size-12 shrink-0 items-center justify-center rounded-2xl bg-primary text-lg font-bold text-on-primary">
                  {num(groups.length)}
                </span>
                <h2 id={`${doc}-contact`} className="min-w-0 text-3xl font-bold leading-tight tracking-tight sm:text-4xl">
                  {tl("contactTitle")}
                </h2>
              </div>
              <div className="glass mt-8 rounded-4xl p-6 sm:p-8">
                <p className="max-w-176 text-lg leading-9">{tl("contactText")}</p>
                <div className="mt-6 flex flex-wrap items-center gap-3">
                  <WhatsAppButton testId="legal-whatsapp" />
                  <a
                    href={ISSUES_URL}
                    target="_blank"
                    rel="noopener noreferrer"
                    data-testid="legal-issues"
                    className="inline-flex min-h-12 items-center gap-2.5 rounded-2xl border-2 border-border bg-surface px-5 text-base font-semibold transition-colors duration-200 hover:border-primary hover:bg-primary-soft"
                  >
                    <GitHubMark />
                    {tl("issues")}
                  </a>
                  <span dir="ltr" className="text-base font-medium text-muted">
                    {WHATSAPP_NUMBER}
                  </span>
                </div>
              </div>
            </section>

            <Link
              href={`/${other}`}
              data-testid="legal-other"
              className="group mt-14 flex min-h-20 items-center justify-between gap-4 rounded-3xl border-2 border-border bg-surface p-5 transition-colors duration-200 hover:border-primary sm:p-6"
            >
              <span className="grid gap-1">
                <span className="text-sm font-medium text-muted">{tl("alsoRead")}</span>
                <span className="text-xl font-bold sm:text-2xl">{tl(other)}</span>
              </span>
              <Icon d="M5 12h14M13 6l6 6-6 6" className="size-6 shrink-0 text-primary transition-transform duration-200 group-hover:translate-x-1 rtl:-scale-x-100 rtl:group-hover:-translate-x-1" />
            </Link>

            <nav aria-label={tc("mainNav")} className="mt-8 flex flex-wrap items-center gap-x-6 gap-y-2 text-sm font-medium">
              <Link href="/" className="inline-flex min-h-11 items-center text-primary underline underline-offset-4">
                {tl("home")}
              </Link>
            </nav>
            <SiteCredit className="mt-10 border-t border-border pt-6" />
          </article>
        </div>
      </main>
    </>
  );
}
