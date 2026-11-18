import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { PAIRS, formatName } from "@/lib/pairs";
import { GitHubLink } from "./GitHubLink";
import { LocaleSwitcher } from "./LocaleSwitcher";
import { Logo } from "./Logo";
import { SiteCredit } from "./SiteCredit";

// Shared frame for the small static pages (popular conversions, command line): a slim header that links home, and a footer
// with the popular conversions and the credit. Server-rendered, no script beyond the locale switcher.
export function SubpageShell({ locale, children }: { locale: string; children: React.ReactNode }) {
  const t = useTranslations("legal");
  const tp = useTranslations("pairs");
  return (
    <>
      <header className="sticky top-0 z-30 border-b border-border/60 bg-bg/70 backdrop-blur-xl">
        <div className="mx-auto flex h-16 max-w-6xl items-center justify-between gap-4 px-4 sm:px-6">
          <Link href="/" aria-label={t("home")} data-testid="subpage-home">
            <Logo />
          </Link>
          <div className="flex items-center gap-2 sm:gap-4">
            <GitHubLink />
            <LocaleSwitcher />
          </div>
        </div>
      </header>
      <main className="relative overflow-x-clip">{children}</main>
      <footer className="border-t border-border">
        <nav aria-label={tp("popular")} className="mx-auto max-w-6xl px-4 py-10 sm:px-6" data-testid="popular-links">
          <p className="text-sm font-semibold text-muted">{tp("popular")}</p>
          <ul className="mt-3 flex flex-wrap gap-2">
            {PAIRS.map((p) => (
              <li key={p.slug}>
                <Link href={`/convert/${p.slug}`} prefetch={false} className="inline-flex min-h-11 items-center rounded-full border border-border px-3.5 text-sm font-medium transition-colors duration-200 hover:border-primary hover:text-primary">
                  {tp("short", { from: formatName(p.from, locale), to: formatName(p.to, locale) })}
                </Link>
              </li>
            ))}
          </ul>
        </nav>
        <SiteCredit className="border-t border-border/70 px-4 py-4 sm:px-6" />
      </footer>
    </>
  );
}
