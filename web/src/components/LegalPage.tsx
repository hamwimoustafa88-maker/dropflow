import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { ISSUES_URL, LEGAL_LINKS, WHATSAPP_URL, type LegalLinkKey } from "@/lib/site";
import { GitHubLink } from "./GitHubLink";
import { LocaleSwitcher } from "./LocaleSwitcher";
import { Logo } from "./Logo";

// A plain, readable document page (privacy policy, terms of use). All text lives in messages/<locale>.json under the
// `privacy` / `terms` namespace as { title, lead, sections: [{ h, p?, ul?, p2?, links? }] }; nothing is hard-coded here.
// `links` are keys of LEGAL_LINKS (the URLs live in code, the labels in `legal.links`).

export type LegalDoc = "privacy" | "terms";
type Section = { h: string; p?: string[]; ul?: string[]; p2?: string[]; links?: LegalLinkKey[] };

export function LegalPage({ doc }: { doc: LegalDoc }) {
  const t = useTranslations(doc);
  const tl = useTranslations("legal");
  const tc = useTranslations("common");
  const sections = t.raw("sections") as Section[];
  const other: LegalDoc = doc === "privacy" ? "terms" : "privacy";

  return (
    <>
      <header className="sticky top-0 z-30 border-b border-border/60 bg-bg/70 backdrop-blur-xl">
        <div className="mx-auto flex h-16 max-w-3xl items-center justify-between gap-4 px-4 sm:px-6">
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

      <main id="content" className="mx-auto max-w-3xl px-4 pb-20 pt-12 sm:px-6 sm:pt-16" data-testid={`legal-${doc}`}>
        <article>
          <h1 className="text-4xl font-bold leading-tight tracking-tight sm:text-5xl">{t("title")}</h1>
          <p className="mt-3 text-sm font-medium text-muted">
            {tl("effective")}: <time dateTime="2026-10-21">{tl("date")}</time>
          </p>
          <p className="mt-6 text-lg leading-8">{t("lead")}</p>

          {sections.map((s, i) => (
            <section key={i} aria-labelledby={`${doc}-s${i}`} className="mt-10">
              <h2 id={`${doc}-s${i}`} className="text-2xl font-bold tracking-tight">
                {s.h}
              </h2>
              {s.p?.map((text, j) => (
                <p key={j} className="mt-3 leading-8">
                  {text}
                </p>
              ))}
              {s.ul ? (
                <ul className="mt-3 list-disc space-y-2 ps-6 leading-8 marker:text-primary">
                  {s.ul.map((text, j) => (
                    <li key={j}>{text}</li>
                  ))}
                </ul>
              ) : null}
              {s.p2?.map((text, j) => (
                <p key={j} className="mt-3 leading-8">
                  {text}
                </p>
              ))}
              {s.links ? (
                <ul className="mt-3 flex flex-wrap gap-x-6">
                  {s.links.map((key) => (
                    <li key={key}>
                      <a
                        href={LEGAL_LINKS[key]}
                        target="_blank"
                        rel="noopener noreferrer"
                        className="inline-flex min-h-11 items-center font-semibold text-primary underline decoration-2 underline-offset-4"
                      >
                        {tl(`links.${key}`)}
                      </a>
                    </li>
                  ))}
                </ul>
              ) : null}
            </section>
          ))}

          <section aria-labelledby={`${doc}-contact`} className="mt-10">
            <h2 id={`${doc}-contact`} className="text-2xl font-bold tracking-tight">
              {tl("contactTitle")}
            </h2>
            <p className="mt-3 leading-8">{tl("contactText")}</p>
            <p className="mt-3">
              <a
                href={ISSUES_URL}
                target="_blank"
                rel="noopener noreferrer"
                dir="ltr"
                className="inline-flex min-h-11 items-center break-all font-semibold text-primary underline decoration-2 underline-offset-4"
              >
                {ISSUES_URL}
              </a>
            </p>
            <p className="mt-1">
              <a
                href={WHATSAPP_URL}
                target="_blank"
                rel="noopener noreferrer"
                data-testid="legal-whatsapp"
                className="inline-flex min-h-11 items-center font-semibold text-primary underline decoration-2 underline-offset-4"
              >
                {tl("whatsapp")}
              </a>
            </p>
          </section>
        </article>

        <nav aria-label={tc("mainNav")} className="mt-14 flex flex-wrap items-center gap-x-6 gap-y-2 border-t border-border pt-6 text-sm font-medium">
          <Link href="/" className="inline-flex min-h-11 items-center text-primary underline underline-offset-4">
            {tl("home")}
          </Link>
          <Link href={`/${other}`} className="inline-flex min-h-11 items-center text-primary underline underline-offset-4" data-testid="legal-other">
            {tl(other)}
          </Link>
          <span className="text-muted">{tl("rights")}</span>
        </nav>
      </main>
    </>
  );
}
