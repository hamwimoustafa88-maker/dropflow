import { useTranslations } from "next-intl";
import { CONTRIBUTING_URL, REPO_URL } from "@/lib/site";
import { GitHubMark } from "./GitHubLink";

// "Open source · for developers": one compact card, three short points and two links. Plain links, nothing is fetched.
const POINTS = ["license", "stack", "issues"] as const;

export function Developers() {
  const t = useTranslations("developers");
  return (
    <section id="developers" aria-labelledby="developers-title" className="mx-auto max-w-6xl px-4 pb-16 sm:px-6 lg:pb-24">
      <div className="reveal glass grid gap-8 rounded-4xl p-6 sm:p-10 lg:grid-cols-[1.3fr_1fr] lg:items-center">
        <div>
          <p className="inline-flex items-center gap-2 text-sm font-semibold text-accent">
            <GitHubMark className="size-4" />
            {t("eyebrow")}
          </p>
          <h2 id="developers-title" className="mt-3 text-3xl font-bold leading-tight tracking-tight sm:text-5xl">
            {t("title")}
          </h2>
          <div className="mt-8 flex flex-wrap gap-3">
            <a
              href={REPO_URL}
              target="_blank"
              rel="noopener noreferrer"
              data-testid="dev-repo"
              className="inline-flex min-h-13 items-center gap-3 rounded-2xl bg-primary px-5 text-base font-semibold text-on-primary shadow-lg shadow-primary/25 transition-colors duration-200"
            >
              <GitHubMark />
              <span>{t("repo")}</span>
            </a>
            <a
              href={CONTRIBUTING_URL}
              target="_blank"
              rel="noopener noreferrer"
              data-testid="dev-contribute"
              className="glass inline-flex min-h-13 items-center gap-3 rounded-2xl px-5 text-base font-semibold text-fg transition-colors duration-200 hover:border-primary"
            >
              <svg aria-hidden="true" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <circle cx="6" cy="6" r="2.5" />
                <circle cx="6" cy="18" r="2.5" />
                <circle cx="18" cy="8" r="2.5" />
                <path d="M6 8.5v7M18 10.5c0 4-6 3-11 5.5" />
              </svg>
              <span>{t("contribute")}</span>
            </a>
          </div>
        </div>
        <ul className="grid gap-3">
          {POINTS.map((p) => (
            <li key={p} className="flex items-center gap-3 rounded-2xl border border-border bg-surface px-4 py-3 text-base font-medium">
              <svg aria-hidden="true" className="size-5 shrink-0 text-accent" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round">
                <path d="M5 12.5l4.5 4.5L19 7.5" />
              </svg>
              <span className="min-w-0 break-words">
                {t(`points.${p}`)}
              </span>
            </li>
          ))}
        </ul>
      </div>
    </section>
  );
}
