import { useTranslations } from "next-intl";
import { CHECKSUMS_FILE, downloadUrl } from "@/lib/downloads";
import { CONTRIBUTING_URL, ISSUES_URL, LICENSE_URL, RELEASES_URL, REPO_URL, SECURITY_URL, SIGNING_POLICY_URL } from "@/lib/site";
import { CopyCode } from "./CopyCode";
import { GitHubMark } from "./GitHubLink";

// "Download from GitHub (for developers)": the open-source card and the build-from-source steps in ONE section.
// Plain links and copyable commands; nothing is fetched. Commands are code, not copy: they are the same in every language.
const POINTS = ["license", "stack", "issues"] as const;
// Project pages that used to crowd the footer: bugs, security, code-signing policy and the licence file.
const MORE = [
  ["issues", ISSUES_URL],
  ["security", SECURITY_URL],
  ["signing", SIGNING_POLICY_URL],
  ["license", LICENSE_URL],
] as const;
const NEEDS =["dotnet", "node", "android"] as const;
const RUN = [
  ["windows", "dotnet run --project src/DropFlow.App"],
  ["desktop", "dotnet run --project src/DropFlow.Desktop"],
  ["cli", "dotnet run --project src/DropFlow.Cli -- list"],
  ["web", "cd web && npm install && npm run dev"],
  ["android", "cd android && ./gradlew assembleDebug"],
  ["engines", "pwsh tools/fetch-engines.ps1"],
] as const;

function Step({ n, title, wide = false, children }: { n: number; title: string; wide?: boolean; children: React.ReactNode }) {
  return (
    <li className={`grid content-start gap-3 ${wide ? "lg:col-span-2" : ""}`}>
      <h3 className="flex items-center gap-3 text-base font-semibold">
        <span aria-hidden="true" className="inline-flex size-7 items-center justify-center rounded-full bg-primary-soft text-sm font-bold text-primary">
          {n}
        </span>
        {title}
      </h3>
      {children}
    </li>
  );
}

export function Developers() {
  const t = useTranslations("developers");
  const link = "inline-flex min-h-13 items-center gap-3 rounded-2xl px-5 text-base font-semibold transition-colors duration-200";
  return (
    <section id="developers" aria-labelledby="developers-title" className="mx-auto max-w-6xl px-4 pb-16 sm:px-6 lg:pb-24">
      <div className="reveal glass rounded-4xl p-6 sm:p-10">
        <div className="grid gap-8 lg:grid-cols-[1.3fr_1fr] lg:items-center">
          <div>
            <p className="inline-flex items-center gap-2 text-sm font-semibold text-accent">
              <GitHubMark className="size-4" />
              {t("eyebrow")}
            </p>
            <h2 id="developers-title" className="mt-3 text-3xl font-bold leading-tight tracking-tight sm:text-5xl">
              {t("title")}
            </h2>
            <div className="mt-8 flex flex-wrap gap-3">
              <a href={REPO_URL} target="_blank" rel="noopener noreferrer" data-testid="dev-repo" className={`${link} bg-primary text-on-primary shadow-lg shadow-primary/25`}>
                <GitHubMark />
                <span>{t("repo")}</span>
              </a>
              <a href={RELEASES_URL} target="_blank" rel="noopener noreferrer" data-testid="dev-releases" className={`${link} glass text-fg hover:border-primary`}>
                <svg aria-hidden="true" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M20.6 13.4l-7.2 7.2a2 2 0 0 1-2.8 0L3 13V3h10l7.6 7.6a2 2 0 0 1 0 2.8z" />
                  <circle cx="7.5" cy="7.5" r="1.5" />
                </svg>
                <span>{t("releases")}</span>
              </a>
              <a href={CONTRIBUTING_URL} target="_blank" rel="noopener noreferrer" data-testid="dev-contribute" className={`${link} glass text-fg hover:border-primary`}>
                <svg aria-hidden="true" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <circle cx="6" cy="6" r="2.5" />
                  <circle cx="6" cy="18" r="2.5" />
                  <circle cx="18" cy="8" r="2.5" />
                  <path d="M6 8.5v7M18 10.5c0 4-6 3-11 5.5" />
                </svg>
                <span>{t("contribute")}</span>
              </a>
            </div>
            <a href={downloadUrl(CHECKSUMS_FILE)} download rel="noopener" data-testid="dev-checksums" className="mt-4 inline-flex min-h-11 items-center gap-2 text-sm font-semibold text-primary underline-offset-4 hover:underline">
              <svg aria-hidden="true" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M12 3l7 3v5c0 4.5-3 8.5-7 10-4-1.5-7-5.5-7-10V6l7-3z" />
                <path d="M9 12l2 2 4-4" />
              </svg>
              {t("checksums")}
            </a>
            <ul aria-label={t("moreLabel")} className="mt-3 flex flex-wrap gap-2" data-testid="dev-links">
              {MORE.map(([key, href]) => (
                <li key={key}>
                  <a
                    href={href}
                    target="_blank"
                    rel="noopener noreferrer"
                    data-testid={`dev-${key}`}
                    className="inline-flex min-h-11 items-center rounded-full border border-border bg-surface px-4 text-sm font-semibold text-muted transition-colors duration-200 hover:border-primary hover:text-fg"
                  >
                    {t(`more.${key}`)}
                  </a>
                </li>
              ))}
            </ul>
          </div>
          <ul className="grid gap-3">
            {POINTS.map((p) => (
              <li key={p} data-testid="dev-point" className="flex items-center gap-3 rounded-2xl border border-border bg-surface px-4 py-3 text-base font-medium">
                <svg aria-hidden="true" className="size-5 shrink-0 text-accent" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M5 12.5l4.5 4.5L19 7.5" />
                </svg>
                <span className="min-w-0 break-words">{t(`points.${p}`)}</span>
              </li>
            ))}
          </ul>
        </div>

        <ol className="mt-10 grid gap-7 border-t border-border pt-10 lg:grid-cols-2" data-testid="dev-steps">
          <Step n={1} title={t("steps.clone")}>
            <CopyCode code={`git clone ${REPO_URL}.git`} label={t("steps.clone")} />
          </Step>
          <Step n={2} title={t("steps.needs")}>
            <ul className="flex flex-wrap gap-2">
              {NEEDS.map((k) => (
                <li key={k} className="rounded-full border border-border bg-surface px-3.5 py-1.5 text-sm font-medium">
                  {t(`needs.${k}`)}
                </li>
              ))}
            </ul>
          </Step>
          <Step n={3} title={t("steps.run")} wide>
            <dl className="grid gap-3 lg:grid-cols-2 lg:gap-x-6">
              {RUN.map(([k, cmd]) => (
                <div key={k} className="grid gap-1.5">
                  <dt className="text-sm font-medium text-muted">{t(`targets.${k}`)}</dt>
                  <dd>
                    <CopyCode code={cmd} label={t(`targets.${k}`)} />
                  </dd>
                </div>
              ))}
            </dl>
          </Step>
        </ol>
      </div>
    </section>
  );
}
