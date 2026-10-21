import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";

// Inert until the first release exists: it looks like a button but is not a link, and says "Soon".
export function SoonButton({ kind, primary = false }: { kind: "windows" | "web"; primary?: boolean }) {
  const t = useTranslations("common");
  return (
    <span
      className={`inline-flex min-h-13 cursor-default select-none items-center gap-3 rounded-2xl px-5 text-base font-semibold ${
        primary ? "bg-primary text-on-primary shadow-lg shadow-primary/25" : "glass text-fg"
      }`}
    >
      <svg aria-hidden="true" width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        {kind === "windows" ? (
          <>
            <rect x="3" y="4" width="18" height="12" rx="2" />
            <path d="M8 20h8M12 16v4" />
          </>
        ) : (
          <>
            <circle cx="12" cy="12" r="9" />
            <path d="M3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18" />
          </>
        )}
      </svg>
      <span>{t(kind)}</span>
      <span className={`rounded-full px-2.5 py-0.5 text-xs font-bold ${primary ? "bg-on-primary/15" : "bg-surface-2 text-muted"}`}>{t("soon")}</span>
    </span>
  );
}

// The in-browser app is real: a link to /app (the Windows button above stays an inert "Soon" badge until a release exists).
export function WebButton({ primary = false }: { primary?: boolean }) {
  const t = useTranslations("common");
  return (
    <Link
      href="/app"
      // No viewport prefetch: it would load the /app route chunks on the landing page. The click fetches them.
      prefetch={false}
      className={`inline-flex min-h-13 items-center gap-3 rounded-2xl px-5 text-base font-semibold transition-colors duration-200 ${
        primary ? "bg-primary text-on-primary shadow-lg shadow-primary/25" : "glass text-fg hover:border-primary"
      }`}
    >
      <svg aria-hidden="true" width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <circle cx="12" cy="12" r="9" />
        <path d="M3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18" />
      </svg>
      <span>{t("web")}</span>
      <svg aria-hidden="true" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round" className="rtl:-scale-x-100">
        <path d="M5 12h14M13 6l6 6-6 6" />
      </svg>
    </Link>
  );
}
