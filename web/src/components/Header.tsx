import { useTranslations } from "next-intl";
import { GitHubLink } from "./GitHubLink";
import { Logo } from "./Logo";
import { LocaleSwitcher } from "./LocaleSwitcher";

const links = ["features", "formats", "developers"] as const;

export function Header() {
  const t = useTranslations("nav");
  const tc = useTranslations("common");
  return (
    <header className="sticky top-0 z-30 border-b border-border/60 bg-bg/70 backdrop-blur-xl">
      <div className="mx-auto flex h-16 max-w-6xl items-center justify-between gap-4 px-4 sm:px-6">
        <a href="#top" aria-label="DropFlow">
          <Logo />
        </a>
        <div className="flex items-center gap-2 sm:gap-6">
          <nav aria-label={tc("mainNav")} className="hidden items-center gap-6 text-sm font-medium text-muted sm:flex">
            {links.map((l) => (
              <a key={l} href={`#${l}`} className="rounded-md py-2 transition-colors duration-200 hover:text-fg">
                {t(l)}
              </a>
            ))}
          </nav>
          <GitHubLink />
          <LocaleSwitcher />
        </div>
      </div>
    </header>
  );
}
