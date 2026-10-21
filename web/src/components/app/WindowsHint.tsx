"use client";

import { useTranslations } from "next-intl";
import { RELEASES_URL } from "@/lib/site";
import type { WindowsHint } from "@/engine/windows-hint";

// One short, honest tile: what the Windows app adds for these files (only what the browser really cannot do), and the
// link to its latest release on GitHub. Clicking the link is the visitor's choice; the page never fetches it.
export function WindowsHintTile({ hint }: { hint: WindowsHint }) {
  const t = useTranslations("app");
  return (
    <aside aria-label={t("hint.title")} data-testid="windows-hint" className="glass rounded-3xl p-4">
      <div className="flex items-center gap-2.5">
        <svg aria-hidden="true" width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" className="text-primary">
          <rect x="3" y="4" width="18" height="12" rx="2" />
          <path d="M8 20h8M12 16v4" />
        </svg>
        <h2 className="text-base font-bold">{t("hint.title")}</h2>
      </div>
      <ul className="mt-3 flex flex-wrap gap-1.5">
        {hint.items.map((item) => {
          const text = item.label ?? (item.labelKey ? t(item.labelKey as never) : "");
          return (
            <li key={item.label ?? item.labelKey} className="rounded-full border border-border bg-surface px-3 py-1 text-sm font-medium" dir={item.label ? "ltr" : undefined}>
              {text}
            </li>
          );
        })}
        {hint.more > 0 ? (
          <li dir="ltr" className="rounded-full px-2 py-1 text-sm font-semibold text-muted">
            {t("hint.more", { count: hint.more })}
          </li>
        ) : null}
      </ul>
      <a
        href={RELEASES_URL}
        target="_blank"
        rel="noopener noreferrer"
        data-testid="windows-download"
        className="mt-4 inline-flex min-h-11 items-center gap-2 rounded-2xl bg-primary px-5 text-sm font-bold text-on-primary shadow-lg shadow-primary/25"
      >
        <svg aria-hidden="true" viewBox="0 0 24 24" className="size-4" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round">
          <path d="M12 4v11M7 10l5 5 5-5M5 20h14" />
        </svg>
        {t("hint.download")}
      </a>
    </aside>
  );
}
