"use client";

import { useLocale, useTranslations } from "next-intl";
import { usePathname, useRouter } from "@/i18n/navigation";
import { routing } from "@/i18n/routing";

const labels: Record<string, { short: string; full: string }> = {
  ar: { short: "ع", full: "العربية" },
  en: { short: "EN", full: "English" },
};

export function LocaleSwitcher() {
  const locale = useLocale();
  const pathname = usePathname();
  const router = useRouter();
  const t = useTranslations("common");

  return (
    <div role="group" aria-label={t("language")} className="inline-flex rounded-full border border-border bg-surface p-0.5">
      {routing.locales.map((l) => (
        <button
          key={l}
          type="button"
          lang={l}
          aria-pressed={l === locale}
          // The accessible name starts with the visible text (WCAG 2.5.3 label in name): "EN English", "ع العربية".
          aria-label={`${labels[l].short} ${labels[l].full}`}
          onClick={() => router.replace(pathname, { locale: l })}
          className={`min-h-11 min-w-12 rounded-full px-3 text-sm font-semibold transition-colors duration-200 ${
            l === locale ? "bg-primary text-on-primary" : "text-muted hover:text-fg"
          }`}
        >
          {labels[l].short}
        </button>
      ))}
    </div>
  );
}
