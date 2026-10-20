import { useTranslations } from "next-intl";
import { Logo } from "./Logo";
import { LocaleSwitcher } from "./LocaleSwitcher";

const links = ["how", "formats", "pdf", "media", "privacy", "roadmap"] as const;

export function Header() {
  const t = useTranslations("nav");
  const tc = useTranslations("common");
  return (
    <header className="sticky top-0 z-30 border-b border-border bg-bg/85 backdrop-blur">
      <div className="mx-auto flex h-16 max-w-6xl items-center justify-between gap-4 px-4 sm:px-6">
        <a href="#top" aria-label="DropFlow">
          <Logo />
        </a>
        <nav aria-label={tc("mainNav")} className="hidden items-center gap-6 text-sm font-medium text-muted lg:flex">
          {links.map((l) => (
            <a key={l} href={`#${l}`} className="rounded-md py-2 transition-colors duration-200 hover:text-fg">
              {t(l)}
            </a>
          ))}
        </nav>
        <div className="flex items-center gap-3">
          <span className="hidden rounded-full border border-border px-3 py-1.5 text-xs font-semibold text-muted sm:inline">
            {tc("soon")}
          </span>
          <LocaleSwitcher />
        </div>
      </div>
    </header>
  );
}
