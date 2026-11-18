import { useTranslations } from "next-intl";
import { LATEST_VERSION } from "@/lib/downloads";

// "What's new" in the released version: a few cards, one short line each. The list must only name what LATEST_VERSION ships.
export const NEWS = ["flip", "routes", "presets", "browser", "formats", "home"] as const;

const ICONS: Record<(typeof NEWS)[number], string> = {
  flip: "M4 12a8 8 0 0 1 14-5.3M20 12a8 8 0 0 1-14 5.3M18 3v4h-4M6 21v-4h4",
  routes: "M5 6a2 2 0 1 0 0-.01M19 18a2 2 0 1 0 0-.01M12 12a2 2 0 1 0 0-.01M6.5 7.5l4 3M13.5 13.5l4 3",
  presets: "M5 4h14v16l-7-4-7 4V4Z",
  browser: "M3 6a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6ZM3 9h18M12 12v6M9 15l3 3 3-3",
  formats: "M7 3h7l5 5v13H7V3ZM14 3v5h5M10 13h6M10 17h6",
  home: "M3 11l9-7 9 7v9a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1v-9Z",
};

export function WhatsNew() {
  const t = useTranslations("news");
  return (
    <section id="new" className="mx-auto max-w-6xl px-4 pb-16 sm:px-6 lg:pb-24" data-testid="whats-new">
      <h2 className="reveal text-4xl font-bold tracking-tight sm:text-6xl">
        {t("title")} <span className="text-gradient" dir="ltr">{LATEST_VERSION}</span>
      </h2>
      <ul className="mt-10 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {NEWS.map((n) => (
          <li key={n} className="reveal glass rounded-4xl p-6 transition-transform duration-300 hover:-translate-y-1">
            <span aria-hidden="true" className="flex size-12 items-center justify-center rounded-2xl bg-primary-soft text-primary">
              <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
                <path d={ICONS[n]} />
              </svg>
            </span>
            <h3 className="mt-4 text-xl font-bold tracking-tight">{t(`${n}.title`)}</h3>
            <p className="mt-1 text-muted">{t(`${n}.text`)}</p>
          </li>
        ))}
      </ul>
    </section>
  );
}
