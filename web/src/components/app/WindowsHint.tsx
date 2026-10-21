"use client";

import { useTranslations } from "next-intl";
import type { WindowsHint } from "@/engine/windows-hint";

// One short, honest tile: what the Windows app adds for these files. Not a promise of availability: the Windows
// app has no release yet, so the tile carries the same inert "Soon" badge as the landing page.
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
        <span className="rounded-full bg-surface-2 px-2.5 py-0.5 text-xs font-bold text-muted">{t("hint.soon")}</span>
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
    </aside>
  );
}
